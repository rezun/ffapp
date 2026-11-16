using System.Reflection;
using System.Runtime.InteropServices;
using FFApp.Core.Services;
using FFmpeg.AutoGen;

namespace FFApp.Infrastructure;

/// <summary>Checks native libraries without binding AutoGen to a path before the user finishes setup.</summary>
public sealed class ComparisonRuntimeService : IComparisonRuntimeService
{
    private static readonly string[] ToneMappingFilters =
        ["buffer", "buffersink", "format", "setparams", "trim", "zscale", "tonemap", "haldclutsrc", "haldclut"];
    private static readonly string[] VideoToolboxFilters =
        ["buffer", "buffersink", "format", "hwupload", "hwdownload", "scale_vt"];

    public ComparisonRuntimeStatus Check(string ffmpegPath) =>
        CheckDirectory(ComparisonDecoderFactory.FindLibraryDirectory(ffmpegPath));

    internal static ComparisonRuntimeStatus CheckDirectory(string directory)
    {
        var handles = new List<IntPtr>();
        try
        {
            foreach (var (name, major) in new[]
            {
                ("avutil", ffmpeg.LIBAVUTIL_VERSION_MAJOR), ("avcodec", ffmpeg.LIBAVCODEC_VERSION_MAJOR),
                ("avformat", ffmpeg.LIBAVFORMAT_VERSION_MAJOR), ("swscale", ffmpeg.LIBSWSCALE_VERSION_MAJOR),
            })
            {
                var handle = Load(directory, name, major);
                handles.Add(handle);
                var version = Marshal.GetDelegateForFunctionPointer<VersionFunction>(NativeLibrary.GetExport(handle, name + "_version"))();
                if ((version >> 16) != major)
                    return new(false, false, $"Comparison playback requires FFmpeg 9 shared libraries. {name} has an incompatible version in {directory}.");
            }
            var toneMapping = false;
            try
            {
                var filterLibrary = Load(directory, "avfilter", ffmpeg.LIBAVFILTER_VERSION_MAJOR);
                handles.Add(filterLibrary);
                var version = Marshal.GetDelegateForFunctionPointer<VersionFunction>(NativeLibrary.GetExport(filterLibrary, "avfilter_version"))();
                var findFilter = Marshal.GetDelegateForFunctionPointer<FilterFunction>(NativeLibrary.GetExport(filterLibrary, "avfilter_get_by_name"));
                toneMapping = (version >> 16) == ffmpeg.LIBAVFILTER_VERSION_MAJOR
                    && (ToneMappingFilters.All(name => findFilter(name) != IntPtr.Zero)
                        || OperatingSystem.IsMacOS() && VideoToolboxFilters.All(name => findFilter(name) != IntPtr.Zero));
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                // Filtering is optional; the core libraries still support SDR comparison playback.
                return new(true, false, $"Comparison playback available ({directory}). HDR tone mapping is unavailable: {ex.Message}");
            }
            return new(true, toneMapping, $"Comparison playback available ({directory}). "
                + (toneMapping ? "HDR tone-mapping filters are available." : "HDR tone-mapping filters are missing; HDR previews may look flat."));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return new(false, false, "Comparison playback unavailable. Install FFmpeg 9 shared libraries matching the app architecture "
                + "(avcodec 63, avformat 63, avutil 61, swscale 10), beside ffmpeg or in its sibling lib directory. "
                + $"A standalone static ffmpeg executable is insufficient. Details: {ex.Message}");
        }
        finally
        {
            for (var index = handles.Count - 1; index >= 0; index--) NativeLibrary.Free(handles[index]);
        }
    }

    private static IntPtr Load(string directory, string name, int major)
    {
        var fileName = OperatingSystem.IsWindows() ? $"{name}-{major}.dll"
            : OperatingSystem.IsMacOS() ? $"lib{name}.{major}.dylib" : $"lib{name}.so.{major}";
        return NativeLibrary.Load(Path.Combine(directory, fileName), Assembly.GetExecutingAssembly(),
            DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint VersionFunction();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr FilterFunction([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
}
