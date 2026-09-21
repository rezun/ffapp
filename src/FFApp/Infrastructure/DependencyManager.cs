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
        var mkvpropeditPath = ResolveCandidate("mkvpropedit", configured.Mkvpropedit);

        return await ValidatePathsAsync(
            new ExecutablePaths(
                Ffmpeg: ffmpegPath,
                Ffprobe: ffprobePath,
                Mkvpropedit: mkvpropeditPath), ct);
    }

    public async Task<DependencyStatus> ValidatePathsAsync(
        ExecutablePaths paths, CancellationToken ct = default)
    {
        var ffmpegTask = ValidateExecutableAsync("ffmpeg", paths.Ffmpeg, "-version", ct);
        var ffprobeTask = ValidateExecutableAsync("ffprobe", paths.Ffprobe, "-version", ct);
        var mkvpropeditPath = ResolveCandidate("mkvpropedit", paths.Mkvpropedit);
        var mkvpropeditTask = ValidateExecutableAsync(
            "mkvpropedit", mkvpropeditPath, "--version", ct);

        await Task.WhenAll(ffmpegTask, ffprobeTask, mkvpropeditTask);

        var ffmpegValidation = await ffmpegTask;
        var isLibplaceboAvailable = ffmpegValidation.IsValid && paths.Ffmpeg is not null
            && await DetectLibplaceboAsync(paths.Ffmpeg, ct);

        return new DependencyStatus(
            Ffmpeg: ffmpegValidation,
            Ffprobe: await ffprobeTask,
            Mkvpropedit: await mkvpropeditTask,
            IsLibplaceboAvailable: isLibplaceboAvailable);
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

    private static string? FindInPath(string name) =>
        FindExecutable(name, EnumerateSearchDirectories());

    internal static string? FindExecutable(string name, IEnumerable<string> directories)
    {
        var extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? [".exe", ".cmd", ".bat", ".com"]
            : Array.Empty<string>();

        foreach (var directory in directories)
        {
            if (extensions.Length == 0)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                    return candidate;
            }
            else
            {
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

    internal static IEnumerable<string> EnumerateSearchDirectories()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var separator = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
        var pathVar = Environment.GetEnvironmentVariable("PATH");

        if (!string.IsNullOrEmpty(pathVar))
        {
            foreach (var dir in pathVar.Split(separator, StringSplitOptions.RemoveEmptyEntries))
            {
                if (seen.Add(dir))
                    yield return dir;
            }
        }

        // GUI apps on macOS (and some Linux desktop environments) don't inherit the shell's
        // PATH, so Homebrew and other common install locations are missed. Probe them explicitly.
        foreach (var dir in GetFallbackDirectories())
        {
            if (seen.Add(dir))
                yield return dir;
        }
    }

    internal static IReadOnlyList<string> GetFallbackDirectories()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return ["/opt/homebrew/bin", "/usr/local/bin", "/opt/local/bin"];
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return ["/usr/local/bin", "/home/linuxbrew/.linuxbrew/bin"];
        }

        return [];
    }

    private static async Task<bool> DetectLibplaceboAsync(string ffmpegPath, CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = "-h filter=libplacebo",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.Start();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ValidationTimeout);

            await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            await process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<ExecutableValidation> ValidateExecutableAsync(
        string name, string? path, string versionArgument, CancellationToken ct)
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
                    Arguments = versionArgument,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.Start();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ValidationTimeout);

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            await process.WaitForExitAsync(timeoutCts.Token);

            if (process.ExitCode != 0)
            {
                return new ExecutableValidation(name, path, IsValid: false,
                    Error: $"{name} exited with code {process.ExitCode}.");
            }

            var output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            var firstLine = output.AsSpan().IndexOf('\n') is var idx and >= 0
                ? output[..idx].TrimEnd()
                : output.TrimEnd();

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
