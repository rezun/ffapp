using System.Buffers;
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
    private SwsContext* _scale;
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
    private static readonly double TimestampTolerance = 0.000001;
    private static readonly double SequentialReadWindowSeconds = 0.5;

    public NativeComparisonDecoder(string path, CropRect? crop, CancellationToken ct)
    {
        _crop = crop;
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
            // Keep queues small for interactive decoding and avoid two decoders oversubscribing the CPU.
            _codec->thread_count = 2;
            Check(ffmpeg.avcodec_open2(_codec, decoder, null));
            _packet = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
            _received = ffmpeg.av_frame_alloc();
            if (_packet == null || _frame == null || _received == null) throw new OutOfMemoryException();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public ComparisonVideoFrame ReadAt(double seconds, CancellationToken ct = default)
    {
        MoveTo(seconds, ct);
        return CopyFrame();
    }

    private void MoveTo(double seconds, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _readCancellation = ct;
        CheckCancellation(ct);
        seconds = Math.Max(0, seconds);
        if (!_needsSeek && (Math.Abs(_seconds - seconds) <= TimestampTolerance
            || (double.IsFinite(_previousSeconds) && seconds > _previousSeconds + TimestampTolerance
                && seconds <= _seconds + TimestampTolerance))) return;
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
        while (_seconds + TimestampTolerance < seconds)
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
        _scale = ffmpeg.sws_getCachedContext(_scale, width, height, (AVPixelFormat)_frame->format,
            width, height, AVPixelFormat.AV_PIX_FMT_BGRA, (int)SwsFlags.SWS_BILINEAR, null, null, null);
        if (_scale == null) throw new InvalidOperationException("Could not convert the comparison frame to BGRA.");
        // Honor the stream's YUV matrix/range; both sides are converted identically to full-range BGRA.
        var colorspace = _frame->colorspace == AVColorSpace.AVCOL_SPC_UNSPECIFIED
            ? ffmpeg.SWS_CS_DEFAULT : (int)_frame->colorspace;
        var coefficients = ffmpeg.sws_getCoefficients(colorspace);
        var matrix = new int_array4();
        for (uint index = 0; index < 4; index++) matrix[index] = coefficients[index];
        Check(ffmpeg.sws_setColorspaceDetails(_scale, matrix,
            _frame->color_range == AVColorRange.AVCOL_RANGE_JPEG ? 1 : 0, matrix, 1, 0, 1 << 16, 1 << 16));
        var full = MemoryPool<byte>.Shared.Rent(checked(width * height * 4));
        try
        {
            using (var pin = full.Memory.Pin())
            {
                byte*[] data = [(byte*)pin.Pointer, null, null, null];
                int[] stride = [width * 4, 0, 0, 0];
                Check(ffmpeg.sws_scale(_scale, _frame->data, _frame->linesize, 0, height, data, stride));
            }
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
        if (_scale != null) ffmpeg.sws_freeContext(_scale);
        var frame = _frame;
        ffmpeg.av_frame_free(&frame);
        var received = _received;
        ffmpeg.av_frame_free(&received);
        var packet = _packet;
        ffmpeg.av_packet_free(&packet);
        var codec = _codec;
        ffmpeg.avcodec_free_context(&codec);
        var format = _format;
        ffmpeg.avformat_close_input(&format);
        GC.KeepAlive(_interrupt);
    }
}
