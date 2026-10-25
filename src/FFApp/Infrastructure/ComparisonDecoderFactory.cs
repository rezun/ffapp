using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFmpeg.AutoGen;

namespace FFApp.Infrastructure;

/// <summary>
/// Initializes the process-wide native bindings once; decoders belong to their comparison.
/// Owns the shared VideoToolbox device; each decoder holds its own reference to it.
/// </summary>
public sealed unsafe class ComparisonDecoderFactory(ExecutablePathsConfig paths) : IComparisonDecoderFactory, IDisposable
{
    private readonly object _gate = new();
    private bool _initialized;
    private bool _disposed;
    private HdrConversion _hdrConversion;
    private AVBufferRef* _videoToolbox;

    /// <summary>How opened decoders convert PQ/HLG frames to SDR; known after the first Open.</summary>
    internal HdrConversion HdrConversion
    {
        get { lock (_gate) return _hdrConversion; }
    }

    internal bool IsToneMappingAvailable => HdrConversion != HdrConversion.None;

    public IComparisonDecoder Open(string path, CropRect? crop, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        HdrConversion conversion;
        AVBufferRef* device;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_initialized)
            {
                ffmpeg.RootPath = FindLibraryDirectory(paths.FfmpegPath);
                try
                {
                    // ABI versions must match the generated bindings before any native structs are accessed.
                    if ((ffmpeg.avcodec_version() >> 16) != ffmpeg.LIBAVCODEC_VERSION_MAJOR
                        || (ffmpeg.avformat_version() >> 16) != ffmpeg.LIBAVFORMAT_VERSION_MAJOR
                        || (ffmpeg.avutil_version() >> 16) != ffmpeg.LIBAVUTIL_VERSION_MAJOR
                        || (ffmpeg.swscale_version() >> 16) != ffmpeg.LIBSWSCALE_VERSION_MAJOR)
                        throw new NotSupportedException("Comparison playback requires FFmpeg 9 shared libraries.");
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                {
                    throw new NotSupportedException(
                        "Comparison playback requires FFmpeg 9 shared libraries (avcodec, avformat, avutil and swscale). Install a shared FFmpeg build beside ffmpeg or in the system library directory.", ex);
                }
                _videoToolbox = CreateVideoToolboxDevice();
                _hdrConversion = _videoToolbox != null ? HdrConversion.VideoToolbox
                    : ComparisonToneMapping.IsAvailable() ? HdrConversion.Clut : HdrConversion.None;
                _initialized = true;
            }
            conversion = _hdrConversion;
            // The decoder takes ownership of this reference, so Dispose here cannot pull the device from under it.
            device = _videoToolbox == null ? null : ffmpeg.av_buffer_ref(_videoToolbox);
        }
        // The factory owns decoder construction; each caller owns and disposes its session.
        return new NativeComparisonDecoder(path, crop, conversion, device, ct);
    }

    /// <summary>Null when not on macOS, or when FFmpeg lacks VideoToolbox or its filters.</summary>
    private static AVBufferRef* CreateVideoToolboxDevice()
    {
        if (!OperatingSystem.IsMacOS() || !ComparisonToneMapping.IsVideoToolboxAvailable()) return null;
        AVBufferRef* device = null;
        return ffmpeg.av_hwdevice_ctx_create(&device, AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX, null, null, 0) >= 0
            ? device : null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            var device = _videoToolbox;
            ffmpeg.av_buffer_unref(&device);
            _videoToolbox = null;
        }
    }

    internal static string FindLibraryDirectory(string executable)
    {
        var candidates = new List<string> { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "ffmpeg") };
        var executablePath = executable;
        if (!Path.IsPathRooted(executable))
        {
            var fileName = OperatingSystem.IsWindows() && !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? executable + ".exe" : executable;
            executablePath = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(directory => Path.Combine(directory, fileName))
                .FirstOrDefault(File.Exists) ?? executable;
        }
        if (File.Exists(executablePath))
        {
            var resolved = File.ResolveLinkTarget(executablePath, true)?.FullName ?? Path.GetFullPath(executablePath);
            var directory = Path.GetDirectoryName(resolved);
            if (directory is not null)
            {
                candidates.Add(directory);
                candidates.Add(Path.GetFullPath(Path.Combine(directory, "..", "lib")));
            }
        }
        candidates.AddRange(OperatingSystem.IsMacOS()
            ? ["/opt/homebrew/lib", "/usr/local/lib"]
            : ["/usr/lib/x86_64-linux-gnu", "/usr/lib/aarch64-linux-gnu", "/usr/lib64", "/usr/lib", "/usr/local/lib"]);
        var name = OperatingSystem.IsWindows() ? "avcodec-63.dll"
            : OperatingSystem.IsMacOS() ? "libavcodec.63.dylib" : "libavcodec.so.63";
        return candidates.FirstOrDefault(directory => File.Exists(Path.Combine(directory, name))) ?? AppContext.BaseDirectory;
    }
}
