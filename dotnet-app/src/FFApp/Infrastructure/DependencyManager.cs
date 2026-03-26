using System.Diagnostics;
using System.Runtime.InteropServices;

using FFApp.Core.Models;
using FFApp.Core.Services;

namespace FFApp.Infrastructure;

public sealed class DependencyManager : IDependencyService
{
    private static readonly TimeSpan ValidationTimeout = TimeSpan.FromSeconds(5);

    public async Task<DependencyStatus> DiscoverAsync(
        AppSettings settings, CancellationToken ct = default)
    {
        var configured = settings.ExecutablePaths;
        var ffmpegPath = ResolveCandidate("ffmpeg", configured.Ffmpeg);
        var ffprobePath = ResolveCandidate("ffprobe", configured.Ffprobe);

        return await ValidatePathsAsync(
            new ExecutablePaths(Ffmpeg: ffmpegPath, Ffprobe: ffprobePath), ct);
    }

    public async Task<DependencyStatus> ValidatePathsAsync(
        ExecutablePaths paths, CancellationToken ct = default)
    {
        var ffmpegTask = ValidateExecutableAsync("ffmpeg", paths.Ffmpeg, ct);
        var ffprobeTask = ValidateExecutableAsync("ffprobe", paths.Ffprobe, ct);

        await Task.WhenAll(ffmpegTask, ffprobeTask);

        return new DependencyStatus(
            Ffmpeg: ffmpegTask.Result,
            Ffprobe: ffprobeTask.Result);
    }

    private static string? ResolveCandidate(string name, string? configuredPath)
    {
        if (configuredPath is not null)
        {
            // Expand ~ to the user's home directory
            if (configuredPath.StartsWith('~'))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                configuredPath = Path.Combine(home, configuredPath[1..].TrimStart(Path.DirectorySeparatorChar));
            }

            return configuredPath;
        }

        return FindInPath(name);
    }

    private static string? FindInPath(string name)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
            return null;

        var separator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? [".exe", ".cmd", ".bat", ".com"]
            : Array.Empty<string>();

        foreach (var directory in pathVar.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            // On non-Windows, check the name directly
            if (extensions.Length == 0)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                    return candidate;
            }
            else
            {
                // On Windows, try each extension
                foreach (var ext in extensions)
                {
                    var candidate = Path.Combine(directory, name + ext);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
        }

        return null;
    }

    private static async Task<ExecutableValidation> ValidateExecutableAsync(
        string name, string? path, CancellationToken ct)
    {
        if (path is null)
            return new ExecutableValidation(name, null, IsValid: false, Error: $"{name} was not found.");

        if (!File.Exists(path))
            return new ExecutableValidation(name, path, IsValid: false,
                Error: $"Path does not exist: {path}");

        if (Directory.Exists(path))
            return new ExecutableValidation(name, path, IsValid: false,
                Error: $"Path is not a file: {path}");

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.Start();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ValidationTimeout);

            var stdout = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);

            if (process.ExitCode != 0)
            {
                return new ExecutableValidation(name, path, IsValid: false,
                    Error: $"{name} exited with code {process.ExitCode}.");
            }

            var firstLine = stdout.AsSpan().IndexOf('\n') is var idx and >= 0
                ? stdout[..idx].TrimEnd()
                : stdout.TrimEnd();

            return new ExecutableValidation(name, path, IsValid: true, VersionOutput: firstLine);
        }
        catch (OperationCanceledException)
        {
            return new ExecutableValidation(name, path, IsValid: false,
                Error: $"{name} version check timed out.");
        }
        catch (Exception ex)
        {
            return new ExecutableValidation(name, path, IsValid: false,
                Error: $"Failed to run {name}: {ex.Message}");
        }
    }
}
