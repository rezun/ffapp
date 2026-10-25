using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFmpeg.AutoGen;

namespace FFApp.Infrastructure;

/// <summary>Initializes the process-wide native bindings once; decoders belong to their comparison.</summary>
public sealed class ComparisonDecoderFactory(ExecutablePathsConfig paths) : IComparisonDecoderFactory
{
    private readonly object _gate = new();
    private bool _initialized;

    public IComparisonDecoder Open(string path, CropRect? crop, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
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
                _initialized = true;
            }
        }
        // The factory owns decoder construction; each caller owns and disposes its session.
        return new NativeComparisonDecoder(path, crop, ct);
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
