using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FFApp.Infrastructure;

internal static class ProcessSuspension
{
    private const int MacStop = 17;
    private const int MacContinue = 19;
    private const int LinuxStop = 19;
    private const int LinuxContinue = 18;
    private const int NoSuchProcess = 3;

    internal static void SetPaused(Process process, bool paused)
    {
        if (process.HasExited) return;

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
