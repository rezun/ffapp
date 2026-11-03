using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FFApp.Infrastructure;

internal static class ProcessSuspension
{
    private const int MacStop = 17;
    private const int MacContinue = 19;
    private const int MacUser1 = 30;
    private const int MacUser2 = 31;
    private const int LinuxStop = 19;
    private const int LinuxContinue = 18;
    private const int NoSuchProcess = 3;

    private static readonly string WrapperShell = "/bin/sh";
    private static readonly string WrapperName = "ffapp-pause";

    /// <summary>
    /// Runs its arguments as a background child, stops it on SIGUSR1, continues it on SIGUSR2, and exits with
    /// its status. A trapped signal interrupts <c>wait</c> with a status above 128, so the loop waits again.
    /// </summary>
    private static readonly string WrapperScript = """
        "$@" &
        child=$!
        trap 'interrupted=1; kill -STOP "$child"' USR1
        trap 'interrupted=1; kill -CONT "$child"' USR2
        while :; do
          interrupted=0
          wait "$child"
          status=$?
          if [ "$interrupted" = 0 ] || [ "$status" -le 128 ]; then exit "$status"; fi
        done
        """;

    /// <summary>
    /// On macOS a stopped <em>direct</em> child sends the .NET runtime's SIGCHLD handler into a busy loop:
    /// <c>waitid(WEXITED | WNOHANG | WNOWAIT)</c> keeps reporting the stopped child. The app then burns a
    /// CPU core for as long as the encoder is paused, and every <see cref="Process.Start()"/> blocks until
    /// it resumes, which froze the UI (for example when opening a folder in Finder). Behind this wrapper
    /// the stopped encoder is a grandchild, which the runtime never sees, while the wrapper keeps running.
    /// </summary>
    internal static bool NeedsWrapper => OperatingSystem.IsMacOS();

    /// <summary>Returns a start info that runs <paramref name="inner"/> behind the pause wrapper.</summary>
    internal static ProcessStartInfo Wrap(ProcessStartInfo inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        var wrapped = new ProcessStartInfo
        {
            FileName = WrapperShell,
            RedirectStandardOutput = inner.RedirectStandardOutput,
            RedirectStandardError = inner.RedirectStandardError,
            UseShellExecute = false,
            CreateNoWindow = inner.CreateNoWindow,
        };
        wrapped.ArgumentList.Add("-c");
        wrapped.ArgumentList.Add(WrapperScript);
        wrapped.ArgumentList.Add(WrapperName);
        wrapped.ArgumentList.Add(inner.FileName);
        foreach (var argument in inner.ArgumentList)
            wrapped.ArgumentList.Add(argument);
        return wrapped;
    }

    /// <param name="wrapped">True when <paramref name="process"/> is the wrapper from <see cref="Wrap"/>.</param>
    internal static void SetPaused(Process process, bool paused, bool wrapped = false)
    {
        if (process.HasExited) return;

        if (wrapped)
        {
            // The wrapper itself keeps running and forwards SIGSTOP/SIGCONT to the encoder.
            Signal(process, paused ? MacUser1 : MacUser2);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            // Suspend the whole process, including all encoder worker threads.
            var status = paused
                ? NtSuspendProcess(process.SafeHandle)
                : NtResumeProcess(process.SafeHandle);
            if (status < 0 && !process.HasExited)
                throw new InvalidOperationException($"Unable to {(paused ? "pause" : "resume")} the encoder (NTSTATUS 0x{status:X8}).");
            return;
        }

        var signal = OperatingSystem.IsMacOS()
            ? paused ? MacStop : MacContinue
            : OperatingSystem.IsLinux()
                ? paused ? LinuxStop : LinuxContinue
                : throw new PlatformNotSupportedException("Encoder pause is supported on Windows, macOS, and Linux.");
        Signal(process, signal);
    }

    private static void Signal(Process process, int signal)
    {
        if (Kill(process.Id, signal) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            // Finishing concurrently with a pause/resume request is harmless.
            if (error != NoSuchProcess)
                throw new Win32Exception(error, "Unable to change the encoder pause state.");
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(Microsoft.Win32.SafeHandles.SafeProcessHandle process);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(Microsoft.Win32.SafeHandles.SafeProcessHandle process);
}
