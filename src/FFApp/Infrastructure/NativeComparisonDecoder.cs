using System.Buffers;
using System.Globalization;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFmpeg.AutoGen;

namespace FFApp.Infrastructure;

/// <summary>
/// Persistent demuxer/decoder. Native frames and codec buffers survive pause and forward stepping.
/// All methods, including Dispose, run under the playback owner's serial worker gate.
/// </summary>
internal sealed unsafe class NativeComparisonDecoder : IComparisonDecoder
{
    private AVFormatContext* _format;
    private AVCodecContext* _codec;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private AVFrame* _received;
    private AVFrame* _transferred;
    private AVFrame* _bgra;
    private AVBufferRef* _videoToolbox;
    private SwsContext* _scale;
    private AVFilterGraph* _toneMapGraph;
    private AVFilterContext* _toneMapSource;
    private AVFilterContext* _toneMapSink;
    private AVFrame* _toneMapped;
    private ToneMapInput? _toneMapInput;
    private double? _peakNits;
    private bool _peakResolved;
    private long _toneMapTimestamp;
    private readonly HdrConversion _hdrConversion;
    private readonly CropRect? _crop;
    private readonly AVIOInterruptCB_callback _interrupt;
    private CancellationToken _readCancellation;
    private int _stream;
    private double _timeBase;
    private double _start;
    private double _seconds = double.NegativeInfinity;
    private double _previousSeconds = double.NegativeInfinity;
    private bool _draining;
    private bool _disposed;
    private bool _needsSeek;
    private static readonly AVFrameSideDataType[] HdrSideData =
    [
        AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA,
        AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL,
        AVFrameSideDataType.AV_FRAME_DATA_AMBIENT_VIEWING_ENVIRONMENT,
        AVFrameSideDataType.AV_FRAME_DATA_DYNAMIC_HDR_PLUS,
        AVFrameSideDataType.AV_FRAME_DATA_DYNAMIC_HDR_VIVID,
        AVFrameSideDataType.AV_FRAME_DATA_DOVI_METADATA,
        AVFrameSideDataType.AV_FRAME_DATA_DOVI_RPU_BUFFER,
    ];
    private static readonly double TimestampTolerance = 0.000001;
    private static readonly double SequentialReadWindowSeconds = 0.5;
    public double TimestampResolutionSeconds => _timeBase;

    /// <summary>Frame properties the tone-mapping graph was configured for.</summary>
    private readonly record struct ToneMapInput(int Width, int Height, int Format, AVColorSpace ColorSpace,
        AVColorRange Range, AVColorTransferCharacteristic Transfer, AVColorPrimaries Primaries, nint FramesContext);

    /// <summary>Software-only decoder, for HDR conversions that need no GPU device.</summary>
    public NativeComparisonDecoder(string path, CropRect? crop, HdrConversion hdrConversion, CancellationToken ct)
        : this(path, crop, hdrConversion, null, ct)
    {
    }

    /// <param name="videoToolbox">
    /// A device reference this decoder takes ownership of. When set, codecs VideoToolbox supports are
    /// decoded on the GPU; others fall back to software decoding.
    /// </param>
    public NativeComparisonDecoder(string path, CropRect? crop, HdrConversion hdrConversion, AVBufferRef* videoToolbox,
        CancellationToken ct)
    {
        if (hdrConversion == HdrConversion.VideoToolbox && videoToolbox == null)
            throw new ArgumentException("VideoToolbox conversion requires a VideoToolbox device.", nameof(videoToolbox));
        _videoToolbox = videoToolbox;
        _crop = crop;
        _hdrConversion = hdrConversion;
        _readCancellation = ct;
        _interrupt = _ => _readCancellation.IsCancellationRequested ? 1 : 0;
        try
        {
            var format = ffmpeg.avformat_alloc_context();
            if (format == null) throw new OutOfMemoryException();
            _format = format;
            format->interrupt_callback = new AVIOInterruptCB { callback = _interrupt };
            var opened = ffmpeg.avformat_open_input(&format, path, null, null);
            _format = format;
            ct.ThrowIfCancellationRequested();
            Check(opened);
            var info = ffmpeg.avformat_find_stream_info(_format, null);
            ct.ThrowIfCancellationRequested();
            Check(info);
            AVCodec* decoder = null;
            _stream = Check(ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &decoder, 0));
            var stream = _format->streams[_stream];
            _timeBase = ffmpeg.av_q2d(stream->time_base);
            _start = stream->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : stream->start_time * _timeBase;
            _codec = ffmpeg.avcodec_alloc_context3(decoder);
            if (_codec == null) throw new OutOfMemoryException();
            Check(ffmpeg.avcodec_parameters_to_context(_codec, stream->codecpar));
            // With a device set, the default get_format picks VideoToolbox whenever it supports the stream.
            if (videoToolbox != null) _codec->hw_device_ctx = ffmpeg.av_buffer_ref(videoToolbox);
            // Software decoding of 4K needs frame threads to keep up; two decoders share the cores.
            _codec->thread_count = Math.Max(2, Environment.ProcessorCount / 2);
            Check(ffmpeg.avcodec_open2(_codec, decoder, null));
            _packet = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
            _received = ffmpeg.av_frame_alloc();
            _transferred = ffmpeg.av_frame_alloc();
            _bgra = ffmpeg.av_frame_alloc();
            if (_packet == null || _frame == null || _received == null || _transferred == null || _bgra == null)
                throw new OutOfMemoryException();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public ComparisonVideoFrame ReadAt(double seconds, CancellationToken ct = default) => ReadAt(seconds, 0, ct);

    public ComparisonVideoFrame ReadAt(double seconds, double timestampToleranceSeconds, CancellationToken ct = default)
    {
        MoveTo(seconds, ct, timestampToleranceSeconds);
        return CopyFrame();
    }

    private void MoveTo(double seconds, CancellationToken ct, double timestampToleranceSeconds = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _readCancellation = ct;
        CheckCancellation(ct);
        seconds = Math.Max(0, seconds);
        // Different containers round the same frame's PTS to different grids.
        // Include both grids' rounding uncertainty when aligning tracks, rather than
        // treating a sub-millisecond difference as a reason to decode another frame.
        var tolerance = TimestampTolerance + Math.Max(0, timestampToleranceSeconds);
        if (!_needsSeek && (Math.Abs(_seconds - seconds) <= tolerance
            || (double.IsFinite(_previousSeconds) && seconds > _previousSeconds + tolerance
                && seconds <= _seconds + tolerance))) return;
        if (_needsSeek || seconds < _seconds || seconds > _seconds + SequentialReadWindowSeconds)
        {
            var timestamp = (long)Math.Floor((seconds + _start) / _timeBase);
            var seek = ffmpeg.av_seek_frame(_format, _stream, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD);
            CheckCancellation(ct);
            Check(seek);
            ffmpeg.avcodec_flush_buffers(_codec);
            ffmpeg.av_packet_unref(_packet);
            ffmpeg.av_frame_unref(_frame);
            _draining = false;
            _seconds = double.NegativeInfinity;
            _previousSeconds = double.NegativeInfinity;
            _needsSeek = false;
        }
        while (_seconds + tolerance < seconds)
        {
            if (!DecodeNext(ct))
            {
                if (double.IsNegativeInfinity(_seconds)) throw new InvalidDataException("No video frame at the requested position.");
                break;
            }
        }
    }

    public ComparisonVideoFrame? ReadNext(double? afterSeconds = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _readCancellation = ct;
        if (afterSeconds is { } position) MoveTo(position, ct);
        return DecodeNext(ct) ? CopyFrame() : null;
    }

    private bool DecodeNext(CancellationToken ct)
    {
        while (true)
        {
            CheckCancellation(ct);
            var result = ffmpeg.avcodec_receive_frame(_codec, _received);
            if (result == 0)
            {
                // Keep the last successful frame intact when receive reports EOF/EAGAIN.
                var previous = _frame;
                _frame = _received;
                _received = previous;
                var pts = _frame->best_effort_timestamp;
                if (pts == ffmpeg.AV_NOPTS_VALUE) throw new InvalidDataException("Video frame has no presentation timestamp.");
                _previousSeconds = _seconds;
                _seconds = pts * _timeBase - _start;
                return true;
            }
            if (result == ffmpeg.AVERROR_EOF) return false;
            if (result != ffmpeg.AVERROR(ffmpeg.EAGAIN)) Check(result);
            if (_draining) return false;
            do
            {
                ffmpeg.av_packet_unref(_packet);
                result = ffmpeg.av_read_frame(_format, _packet);
                CheckCancellation(ct);
                if (result == ffmpeg.AVERROR_EOF)
                {
                    Check(ffmpeg.avcodec_send_packet(_codec, null));
                    _draining = true;
                    break;
                }
                Check(result);
            } while (_packet->stream_index != _stream);
            if (!_draining)
            {
                try { Check(ffmpeg.avcodec_send_packet(_codec, _packet)); }
                finally { ffmpeg.av_packet_unref(_packet); }
            }
        }
    }

    private void CheckCancellation(CancellationToken ct)
    {
        if (!ct.IsCancellationRequested) return;
        // A cancelled read may already have consumed a packet. Reposition before reusing the decoder.
        _needsSeek = true;
        ct.ThrowIfCancellationRequested();
    }

    private ComparisonVideoFrame CopyFrame()
    {
        var width = _frame->width;
        var height = _frame->height;
        var aspect = ffmpeg.av_guess_sample_aspect_ratio(_format, _format->streams[_stream], _frame);
        var pixelAspectRatio = aspect.num > 0 && aspect.den > 0 ? ffmpeg.av_q2d(aspect) : 1;
        var crop = _crop ?? new CropRect(0, 0, width, height);
        if (crop.X < 0 || crop.Y < 0 || crop.Width <= 0 || crop.Height <= 0
            || (long)crop.X + crop.Width > width || (long)crop.Y + crop.Height > height)
            throw new InvalidDataException("Comparison crop is outside the decoded frame.");
        var full = MemoryPool<byte>.Shared.Rent(checked(width * height * 4));
        try
        {
            if (_hdrConversion != HdrConversion.None && ComparisonToneMapping.IsHdr(_frame->color_trc)) ToneMap(full.Memory);
            else ConvertToBgra(SoftwareFrame(), full.Memory);
            if (_crop is null)
            {
                var result = new ComparisonVideoFrame(width, height, _seconds, full, pixelAspectRatio);
                full = null;
                return result;
            }
            var pixels = MemoryPool<byte>.Shared.Rent(checked(crop.Width * crop.Height * 4));
            for (var row = 0; row < crop.Height; row++)
                full.Memory.Span.Slice(((row + crop.Y) * width + crop.X) * 4, crop.Width * 4)
                    .CopyTo(pixels.Memory.Span.Slice(row * crop.Width * 4));
            return new ComparisonVideoFrame(crop.Width, crop.Height, _seconds, pixels, pixelAspectRatio);
        }
        finally { full?.Dispose(); }
    }

    /// <summary>The current frame in system memory, downloading it first when the GPU decoded it.</summary>
    private AVFrame* SoftwareFrame()
    {
        if (_frame->format != (int)AVPixelFormat.AV_PIX_FMT_VIDEOTOOLBOX) return _frame;
        ffmpeg.av_frame_unref(_transferred);
        Check(ffmpeg.av_hwframe_transfer_data(_transferred, _frame, 0));
        Check(ffmpeg.av_frame_copy_props(_transferred, _frame));
        return _transferred;
    }

    private void ConvertToBgra(AVFrame* source, Memory<byte> destination)
    {
        if (_scale == null)
        {
            _scale = ffmpeg.sws_alloc_context();
            if (_scale == null) throw new OutOfMemoryException();
            // A single-threaded 4K conversion takes ~20 ms, half a frame interval; two decoders share the cores.
            _scale->threads = Math.Max(2, Environment.ProcessorCount / 2);
            _scale->flags = (uint)SwsFlags.SWS_BILINEAR;
        }
        // sws_scale_frame honors the source's YUV matrix/range; both sides become full-range BGRA.
        // Matching primaries and transfer keep swscale from applying its own color management:
        // tone mapping is the graph's job, and SDR frames must keep their values.
        using var pin = destination.Pin();
        _bgra->color_primaries = source->color_primaries;
        _bgra->color_trc = source->color_trc;
        _bgra->width = source->width;
        _bgra->height = source->height;
        _bgra->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
        _bgra->colorspace = AVColorSpace.AVCOL_SPC_RGB;
        _bgra->color_range = AVColorRange.AVCOL_RANGE_JPEG;
        _bgra->data[0] = (byte*)pin.Pointer;
        _bgra->linesize[0] = source->width * 4;
        try { Check(ffmpeg.sws_scale_frame(_scale, _bgra, source)); }
        finally
        {
            // The destination is borrowed; never leave a dangling pointer in the frame.
            _bgra->data[0] = null;
            _bgra->linesize[0] = 0;
        }
    }

    /// <summary>Runs the current frame through the HDR→SDR graph into a tightly packed BGRA buffer.</summary>
    private void ToneMap(Memory<byte> destination)
    {
        // The GPU path accepts decoded GPU frames directly; the CLUT path needs them in system memory.
        var source = _hdrConversion == HdrConversion.VideoToolbox ? _frame : SoftwareFrame();
        var framesContext = source->hw_frames_ctx == null ? 0 : (nint)source->hw_frames_ctx->data;
        var input = new ToneMapInput(source->width, source->height, source->format, source->colorspace,
            source->color_range, source->color_trc, source->color_primaries, framesContext);
        if (_toneMapInput != input) CreateToneMapGraph(input, source);
        var pts = source->pts;
        try
        {
            // haldclut pairs frames with the CLUT by timestamp, so feed it increasing timestamps
            // regardless of seeks. Keep the decoded frame: it is reused when the same position is read again.
            source->pts = _toneMapTimestamp++;
            Check(ffmpeg.av_buffersrc_add_frame_flags(_toneMapSource, source, (int)AvBuffersrcFlag.AV_BUFFERSRC_FLAG_KEEP_REF));
            Check(ffmpeg.av_buffersink_get_frame(_toneMapSink, _toneMapped));
        }
        catch
        {
            // A frame may be stuck inside the graph; rebuild it for the next read.
            FreeToneMapGraph();
            throw;
        }
        finally { source->pts = pts; }
        try
        {
            var expected = _hdrConversion == HdrConversion.VideoToolbox
                ? AVPixelFormat.AV_PIX_FMT_P010LE : AVPixelFormat.AV_PIX_FMT_RGB48LE;
            if (_toneMapped->width != input.Width || _toneMapped->height != input.Height
                || _toneMapped->format != (int)expected)
                throw new InvalidDataException("Tone mapping returned an unexpected frame layout.");
            // The frame is SDR now, but still carries the source's HDR metadata. swscale would read it
            // and brighten the picture a second time, so it must not reach the BGRA conversion.
            foreach (var type in HdrSideData) ffmpeg.av_frame_remove_side_data(_toneMapped, type);
            ConvertToBgra(_toneMapped, destination);
        }
        finally { ffmpeg.av_frame_unref(_toneMapped); }
    }

    private void CreateToneMapGraph(ToneMapInput input, AVFrame* frame)
    {
        FreeToneMapGraph();
        _toneMapGraph = ffmpeg.avfilter_graph_alloc();
        _toneMapped = ffmpeg.av_frame_alloc();
        if (_toneMapGraph == null || _toneMapped == null) throw new OutOfMemoryException();
        // Two decoders run side by side; each may use half of the cores for slice threading.
        _toneMapGraph->nb_threads = Math.Max(2, Environment.ProcessorCount / 2);
        var arguments = string.Create(CultureInfo.InvariantCulture,
            $"video_size={input.Width}x{input.Height}:pix_fmt={input.Format}:time_base=1/1:pixel_aspect=1/1:colorspace={(int)input.ColorSpace}:range={(int)input.Range}");
        var source = ffmpeg.avfilter_graph_alloc_filter(_toneMapGraph, ffmpeg.avfilter_get_by_name("buffer"), "in");
        if (source == null) throw new OutOfMemoryException();
        _toneMapSource = source;
        if (frame->hw_frames_ctx != null)
        {
            // GPU frames carry their pool; the buffer source must know it before initialization.
            var parameters = ffmpeg.av_buffersrc_parameters_alloc();
            if (parameters == null) throw new OutOfMemoryException();
            parameters->hw_frames_ctx = frame->hw_frames_ctx;
            var set = ffmpeg.av_buffersrc_parameters_set(source, parameters);
            ffmpeg.av_free(parameters);
            Check(set);
        }
        Check(ffmpeg.avfilter_init_str(source, arguments));
        AVFilterContext* sink;
        Check(ffmpeg.avfilter_graph_create_filter(&sink, ffmpeg.avfilter_get_by_name("buffersink"), "out", null, null, _toneMapGraph));
        _toneMapSink = sink;
        var outputs = ffmpeg.avfilter_inout_alloc();
        var inputs = ffmpeg.avfilter_inout_alloc();
        try
        {
            if (outputs == null || inputs == null) throw new OutOfMemoryException();
            outputs->name = ffmpeg.av_strdup("in");
            outputs->filter_ctx = source;
            inputs->name = ffmpeg.av_strdup("out");
            inputs->filter_ctx = sink;
            var graph = _hdrConversion == HdrConversion.VideoToolbox
                ? ComparisonToneMapping.VideoToolboxFilterGraph(frame->hw_frames_ctx != null)
                : ComparisonToneMapping.FilterGraph(ffmpeg.av_color_transfer_name(input.Transfer),
                    ffmpeg.av_color_primaries_name(input.Primaries), PeakNits());
            Check(ffmpeg.avfilter_graph_parse_ptr(_toneMapGraph, graph, &inputs, &outputs, null));
            // hwupload needs the device for software-decoded frames.
            if (_videoToolbox != null)
                for (var index = 0; index < _toneMapGraph->nb_filters; index++)
                    _toneMapGraph->filters[index]->hw_device_ctx = ffmpeg.av_buffer_ref(_videoToolbox);
            Check(ffmpeg.avfilter_graph_config(_toneMapGraph, null));
        }
        finally
        {
            ffmpeg.avfilter_inout_free(&inputs);
            ffmpeg.avfilter_inout_free(&outputs);
        }
        _toneMapInput = input;
        _toneMapTimestamp = 0;
    }

    /// <summary>
    /// Content peak in nits, preferring MaxCLL over the mastering display like FFmpeg's tonemap.
    /// Resolved once per file so frames without repeated SEI do not rebuild the CLUT.
    /// </summary>
    private double? PeakNits()
    {
        if (_peakResolved) return _peakNits;
        var parameters = _format->streams[_stream]->codecpar;
        var streamLight = ffmpeg.av_packet_side_data_get(parameters->coded_side_data, parameters->nb_coded_side_data,
            AVPacketSideDataType.AV_PKT_DATA_CONTENT_LIGHT_LEVEL);
        var streamMastering = ffmpeg.av_packet_side_data_get(parameters->coded_side_data, parameters->nb_coded_side_data,
            AVPacketSideDataType.AV_PKT_DATA_MASTERING_DISPLAY_METADATA);
        var frameLight = ffmpeg.av_frame_get_side_data(_frame, AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL);
        var frameMastering = ffmpeg.av_frame_get_side_data(_frame, AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA);
        var light = streamLight != null ? (AVContentLightMetadata*)streamLight->data
            : frameLight != null ? (AVContentLightMetadata*)frameLight->data : null;
        var mastering = streamMastering != null ? (AVMasteringDisplayMetadata*)streamMastering->data
            : frameMastering != null ? (AVMasteringDisplayMetadata*)frameMastering->data : null;
        _peakNits = light != null && light->MaxCLL > 0 ? light->MaxCLL
            : mastering != null && mastering->has_luminance != 0 && mastering->max_luminance.den > 0
                ? ffmpeg.av_q2d(mastering->max_luminance) : null;
        _peakResolved = true;
        return _peakNits;
    }

    private void FreeToneMapGraph()
    {
        var graph = _toneMapGraph;
        ffmpeg.avfilter_graph_free(&graph);
        _toneMapGraph = null;
        _toneMapSource = null;
        _toneMapSink = null;
        _toneMapInput = null;
        var toneMapped = _toneMapped;
        ffmpeg.av_frame_free(&toneMapped);
        _toneMapped = null;
    }

    private static int Check(int result)
    {
        if (result >= 0) return result;
        var buffer = stackalloc byte[256];
        ffmpeg.av_strerror(result, buffer, 256);
        throw new InvalidDataException(System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)buffer)
            ?? $"FFmpeg error {result}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var scale = _scale;
        ffmpeg.sws_free_context(&scale);
        FreeToneMapGraph();
        var frame = _frame;
        ffmpeg.av_frame_free(&frame);
        var received = _received;
        ffmpeg.av_frame_free(&received);
        var transferred = _transferred;
        ffmpeg.av_frame_free(&transferred);
        var bgra = _bgra;
        ffmpeg.av_frame_free(&bgra);
        var packet = _packet;
        ffmpeg.av_packet_free(&packet);
        var codec = _codec;
        ffmpeg.avcodec_free_context(&codec);
        var format = _format;
        ffmpeg.avformat_close_input(&format);
        var videoToolbox = _videoToolbox;
        ffmpeg.av_buffer_unref(&videoToolbox);
        GC.KeepAlive(_interrupt);
    }
}
