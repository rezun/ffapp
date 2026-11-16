using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FFApp.Services;

public sealed class CompanionAppService(IDialogService dialogs, ILogger<CompanionAppService> logger)
    : ICompanionAppService
{
    public async Task OpenAsync()
    {
        try
        {
            var path = FindCompanionPath(AppContext.BaseDirectory);
            var isBundle = OperatingSystem.IsMacOS() && path.EndsWith(".app", StringComparison.Ordinal);
            var start = new ProcessStartInfo(isBundle ? "/usr/bin/open" : path)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory,
            };
            if (isBundle)
            {
                start.ArgumentList.Add("-a");
                start.ArgumentList.Add(path);
            }
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("The operating system could not start FFRux.");
            // open is a short-lived launcher; the companion itself has an independent lifetime.
            if (isBundle)
            {
                await process.WaitForExitAsync();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException($"macOS could not open FFRux (exit code {process.ExitCode}).");
            }
            logger.LogInformation("Opened bundled FFRux at {CompanionPath}", path);
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Opening FFRux failed");
            await dialogs.ShowErrorAsync("Could not open FFRux", ex.Message);
        }
    }

    internal static string FindCompanionPath(string baseDirectory)
    {
        var executable = OperatingSystem.IsWindows() ? "FFRux.exe" : "FFRux";
        if (OperatingSystem.IsMacOS())
        {
            var bundle = Path.GetFullPath(Path.Combine(baseDirectory, "..", "Helpers", "FFRux.app"));
            if (File.Exists(Path.Combine(bundle, "Contents", "MacOS", "FFRux")))
                return bundle;
        }

        var bundled = Path.Combine(baseDirectory, "companion", "FFRux", executable);
        if (File.Exists(bundled))
            return bundled;

        // dotnet run/build keeps the two projects in separate bin/<configuration>/<framework> folders.
        var output = new DirectoryInfo(baseDirectory);
        var sourceRoot = Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", ".."));
        if (File.Exists(Path.Combine(sourceRoot, "FFApp", "FFApp.csproj")) && output.Parent is { } configuration)
        {
            var development = Path.Combine(sourceRoot, "FFRux", "bin", configuration.Name, output.Name, executable);
            if (File.Exists(development))
                return development;
        }

        throw new FileNotFoundException(
            "FFRux is missing from this FFApp installation. Reinstall the complete FFApp package. "
            + "When running from source, build the solution first with dotnet build.");
    }
}
