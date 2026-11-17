using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FFApp.Infrastructure;

/// <summary>
/// Windows AppUserModelIDs, which the Start menu and taskbar use to tell apps apart.
/// </summary>
public static class WindowsAppUserModel
{
    /// <summary>
    /// ID of the FFRux bundled inside the FFApp installation. Without an explicit ID, Windows
    /// attributes everything below the Velopack install folder to FFApp and hides FFRux's Start menu entry.
    /// </summary>
    public static readonly string FFRuxId = "velopack.FFApp.FFRux";

    /// <summary>Makes the current process's windows group under <paramref name="appId"/> in the taskbar.</summary>
    [SupportedOSPlatform("windows")]
    public static void ApplyToCurrentProcess(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(appId));
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
