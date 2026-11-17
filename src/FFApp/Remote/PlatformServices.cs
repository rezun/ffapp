using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

using Microsoft.Extensions.Logging;

namespace FFApp.Remote;

/// <summary>Keeps the computer awake while files are transferred to or from the server.</summary>
public interface ISleepInhibitor
{
    IDisposable Acquire(string reason);
}

public static class SleepInhibitors
{
    public static ISleepInhibitor ForPlatform(ILogger logger) =>
        OperatingSystem.IsMacOS() ? new MacSleepInhibitor(logger) : new NoSleepInhibitor();
}

public sealed class NoSleepInhibitor : ISleepInhibitor
{
    public IDisposable Acquire(string reason) => new Released();

    private sealed class Released : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

/// <summary>An IOKit power assertion, like <c>caffeinate -i</c>: idle sleep is prevented, the display may sleep.</summary>
[SupportedOSPlatform("macos")]
public sealed partial class MacSleepInhibitor(ILogger logger) : ISleepInhibitor
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static readonly uint AssertionLevelOn = 255;
    private static readonly uint Utf8Encoding = 0x08000100;
    private static readonly string PreventIdleSleep = "PreventUserIdleSystemSleep";

    public IDisposable Acquire(string reason)
    {
        var type = CreateString(PreventIdleSleep);
        var name = CreateString(reason);
        try
        {
            var status = IOPMAssertionCreateWithName(type, AssertionLevelOn, name, out var id);
            if (status != 0)
            {
                logger.LogWarning("Could not prevent sleep (IOReturn {Status})", status);
                return new NoSleepInhibitor().Acquire(reason);
            }
            return new Assertion(id);
        }
        finally
        {
            CFRelease(type);
            CFRelease(name);
        }
    }

    private static IntPtr CreateString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\0");
        return CFStringCreateWithCString(IntPtr.Zero, bytes, Utf8Encoding);
    }

    private sealed class Assertion(uint id) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                _ = IOPMAssertionRelease(id);
        }
    }

    [LibraryImport(IOKit)]
    private static partial int IOPMAssertionCreateWithName(IntPtr assertionType, uint level, IntPtr name, out uint assertionId);

    [LibraryImport(IOKit)]
    private static partial int IOPMAssertionRelease(uint assertionId);

    [LibraryImport(CoreFoundation)]
    private static partial IntPtr CFStringCreateWithCString(IntPtr allocator, byte[] value, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(IntPtr reference);
}

/// <summary>Desktop notifications for events the user is likely not watching, such as finished server jobs.</summary>
public interface INotificationService
{
    void Notify(string title, string message);
}

/// <summary>Uses <c>osascript</c> on macOS and <c>notify-send</c> on Linux; Windows shows nothing yet.</summary>
public sealed class DesktopNotificationService(ILogger<DesktopNotificationService> logger) : INotificationService
{
    public void Notify(string title, string message)
    {
        ProcessStartInfo? start = null;
        if (OperatingSystem.IsMacOS())
        {
            start = new ProcessStartInfo("/usr/bin/osascript");
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add($"display notification \"{Escape(message)}\" with title \"{Escape(title)}\"");
        }
        else if (OperatingSystem.IsLinux())
        {
            start = new ProcessStartInfo("notify-send");
            start.ArgumentList.Add("--app-name=FFApp");
            start.ArgumentList.Add(title);
            start.ArgumentList.Add(message);
        }
        if (start is null)
            return;

        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        try
        {
            using var process = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogInformation("Desktop notification unavailable: {Message}", ex.Message);
        }
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
