using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Logging;

namespace FFApp.Infrastructure;

/// <summary>
/// Locates and initializes LibVLC from a system VLC installation.
/// Returns null if VLC is not installed.
/// </summary>
public static class VlcLocator
{
    public static LibVLC? TryCreate(ILogger? logger = null)
    {
        try
        {
            var libPath = FindLibVlcPath();
            InitializeNative(libPath);
            return new LibVLC("--no-video-title-show");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "VLC preview is unavailable");
            return null;
        }
    }

    private static string? FindLibVlcPath()
    {
        if (OperatingSystem.IsMacOS())
        {
            // VLC.app bundle
            const string vlcAppLib = "/Applications/VLC.app/Contents/MacOS/lib";
            if (Directory.Exists(vlcAppLib))
                return vlcAppLib;
        }
        else if (OperatingSystem.IsWindows())
        {
            // Standard VLC install locations on Windows
            string[] candidates =
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VideoLAN", "VLC"),
            ];

            foreach (var path in candidates)
            {
                if (File.Exists(Path.Combine(path, "libvlc.dll")))
                    return path;
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            // System-installed libvlc — Core.Initialize(null) handles default search paths.
            // Check common locations to confirm VLC is present.
            string[] candidates =
            [
                "/usr/lib/x86_64-linux-gnu",
                "/usr/lib/aarch64-linux-gnu",
                "/usr/lib64",
                "/usr/lib",
            ];

            foreach (var path in candidates)
            {
                if (File.Exists(Path.Combine(path, "libvlc.so"))
                    || File.Exists(Path.Combine(path, "libvlc.so.5")))
                    return path;
            }
        }

        return null;
    }

    private static void InitializeNative(string? libPath)
    {
        if (OperatingSystem.IsMacOS() && libPath is not null)
        {
            // macOS requires special handling: .NET's Environment.SetEnvironmentVariable
            // doesn't propagate to native getenv(), so we use native setenv directly.
            // VLC plugins also need libvlccore loaded with RTLD_GLOBAL to resolve @rpath refs.
            var pluginPath = Path.Combine(Directory.GetParent(libPath)!.FullName, "plugins");
            if (Directory.Exists(pluginPath))
                NativeSetenv("VLC_PLUGIN_PATH", pluginPath, 1);

            DlOpen(Path.Combine(libPath, "libvlccore.dylib"), RtldNow | RtldGlobal);
            DlOpen(Path.Combine(libPath, "libvlc.dylib"), RtldNow | RtldGlobal);
        }

        // LibVLCSharp only supports an explicit library directory on Windows and macOS.
        LibVLCSharp.Shared.Core.Initialize(OperatingSystem.IsLinux() ? null : libPath);
    }

    [DllImport("libdl.dylib", EntryPoint = "dlopen")]
    private static extern IntPtr DlOpen(string path, int flags);

    [DllImport("libSystem.B.dylib", EntryPoint = "setenv")]
    private static extern int NativeSetenv(string name, string value, int overwrite);

    private const int RtldNow = 2;
    private const int RtldGlobal = 8;
}
