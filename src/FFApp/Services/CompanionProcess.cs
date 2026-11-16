using System.Diagnostics;

namespace FFApp.Services;

public static class CompanionProcess
{
    private static readonly string ProcessName = "FFRux";

    public static bool IsRunning()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        foreach (var process in processes) process.Dispose();
        return processes.Length > 0;
    }
}
