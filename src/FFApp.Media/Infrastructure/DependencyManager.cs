using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using FFApp.Core.Models;
using FFApp.Core.Services;

namespace FFApp.Infrastructure;

public sealed partial class DependencyManager : IDependencyService
{
    public static readonly int MinimumFfmpegMajorVersion = 9;
    private static readonly TimeSpan ValidationTimeout = TimeSpan.FromSeconds(5);

    public static string? FindInstalledExecutable(string name) => FindExecutable(name, EnumerateSearchDirectories());

    public Task<DependencyStatus> DiscoverAsync(ExecutablePaths configured, CancellationToken ct = default) =>
        ValidatePathsAsync(configured, ct);

    public async Task<DependencyStatus> ValidatePathsAsync(ExecutablePaths paths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ct.ThrowIfCancellationRequested();
        var ffmpegTask = ValidateExecutableAsync("ffmpeg", ResolveCandidate("ffmpeg", paths.Ffmpeg), "-version", ct);
        var ffprobeTask = ValidateExecutableAsync("ffprobe", ResolveCandidate("ffprobe", paths.Ffprobe), "-version", ct);
        var mkvpropeditTask = ValidateExecutableAsync("mkvpropedit", ResolveCandidate("mkvpropedit", paths.Mkvpropedit), "--version", ct);
        await Task.WhenAll(ffmpegTask, ffprobeTask, mkvpropeditTask).ConfigureAwait(false);

        var ffmpeg = await ffmpegTask.ConfigureAwait(false);
        FfmpegCapabilities? capabilities = null;
        if (ffmpeg.IsValid && ffmpeg.Path is { } executable)
        {
            try
            {
                var encodersTask = RunCheckAsync(executable, ["-hide_banner", "-encoders"], ct);
                var filtersTask = RunCheckAsync(executable, ["-hide_banner", "-filters"], ct);
                await Task.WhenAll(encodersTask, filtersTask).ConfigureAwait(false);
                var encoders = await encodersTask.ConfigureAwait(false);
                var filters = await filtersTask.ConfigureAwait(false);
                if (encoders.ExitCode != 0 || filters.ExitCode != 0)
                    ffmpeg = ffmpeg with { IsValid = false, Error = "FFmpeg could not list its encoders and filters. Install a complete FFmpeg 9 or newer build." };
                else
                    capabilities = new(ParseComponentNames(encoders.Output), ParseComponentNames(filters.Output));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                ffmpeg = ffmpeg with { IsValid = false, Error = "FFmpeg encoder/filter check timed out." };
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
            {
                ffmpeg = ffmpeg with { IsValid = false, Error = $"Failed to inspect FFmpeg encoders and filters: {ex.Message}" };
            }
        }

        return new(ffmpeg, await ffprobeTask.ConfigureAwait(false), await mkvpropeditTask.ConfigureAwait(false),
            capabilities?.Filters.Contains("libplacebo") == true, capabilities);
    }

    private static string? ResolveCandidate(string name, string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return FindExecutable(name, EnumerateSearchDirectories());
        configuredPath = configuredPath.Trim();
        if (configuredPath.Equals("~", StringComparison.Ordinal)
            || configuredPath.StartsWith("~/", StringComparison.Ordinal)
            || configuredPath.StartsWith("~\\", StringComparison.Ordinal))
        {
            var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            configuredPath = Path.Combine(homeDirectory, configuredPath[1..].TrimStart('/', '\\'));
        }
        if (!Path.IsPathRooted(configuredPath) && Path.GetFileName(configuredPath) == configuredPath)
            return FindExecutable(configuredPath, EnumerateSearchDirectories()) ?? configuredPath;
        return configuredPath;
    }

    internal static string? FindExecutable(string name, IEnumerable<string> directories)
    {
        var extensions = OperatingSystem.IsWindows() && !Path.HasExtension(name)
            ? new[] { ".exe", ".com" } : new[] { "" };
        foreach (var directory in directories)
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, name + extension);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    internal static IEnumerable<string> EnumerateSearchDirectories()
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var directory in pathDirectories.Concat(GetFallbackDirectories()))
            if (seen.Add(directory)) yield return directory;
    }

    internal static IReadOnlyList<string> GetFallbackDirectories() =>
        OperatingSystem.IsMacOS() ? ["/opt/homebrew/bin", "/usr/local/bin", "/opt/local/bin"]
        : OperatingSystem.IsLinux() ? ["/usr/local/bin", "/home/linuxbrew/.linuxbrew/bin"] : [];

    internal static ImmutableHashSet<string> ParseComponentNames(string output)
    {
        var names = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            // Ignore table legends and prose: entries have flags, a name, and a description.
            if (columns.Length >= 3 && columns[1] != "=" && columns[0].Length is 2 or 3 or 6
                && columns[0].All(character => character == '.' || char.IsAsciiLetterUpper(character)))
                names.Add(columns[1]);
        }
        return names.ToImmutable();
    }

    [GeneratedRegex(@"^(ffmpeg|ffprobe) version (?:n)?(\d+)(?:\.|\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    internal static ExecutableValidation ValidateVersionOutput(string name, string path, string output)
    {
        var firstLine = output.Split('\n', 2)[0].Trim();
        if (name is "ffmpeg" or "ffprobe")
        {
            var match = VersionPattern().Match(firstLine);
            if (!match.Success || !string.Equals(match.Groups[1].Value, name, StringComparison.Ordinal)
                || !int.TryParse(match.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var major))
                return new(name, path, false, Error: $"This executable did not report a recognizable {name} release version. Select {name} {MinimumFfmpegMajorVersion} or newer.");
            if (major < MinimumFfmpegMajorVersion)
                return new(name, path, false, firstLine, $"{name} {MinimumFfmpegMajorVersion} or newer is required. Found: {firstLine}");
        }
        else if (!firstLine.StartsWith("mkvpropedit v", StringComparison.Ordinal))
            return new(name, path, false, Error: "This executable did not identify itself as mkvpropedit.");
        return new(name, path, true, firstLine);
    }

    private static async Task<ExecutableValidation> ValidateExecutableAsync(
        string name, string? path, string versionArgument, CancellationToken ct)
    {
        if (path is null)
            return new(name, null, false, Error: $"{name} was not found. Install it or select its executable.");
        if (!File.Exists(path))
            return new(name, path, false, Error: $"Path does not exist or is not a file: {path}");
        try
        {
            var result = await RunCheckAsync(path, [versionArgument], ct).ConfigureAwait(false);
            if (result.ExitCode != 0)
                return new(name, path, false, Error: $"{name} exited with code {result.ExitCode}. Check that the executable and its libraries are installed correctly.");
            return ValidateVersionOutput(name, path, result.Output);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(name, path, false, Error: $"{name} version check timed out.");
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return new(name, path, false, Error: $"Failed to run {name}: {ex.Message}");
        }
    }

    private sealed record CheckResult(int ExitCode, string Output);

    private static async Task<CheckResult> RunCheckAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ValidationTimeout);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            return new(process.ExitCode, string.IsNullOrWhiteSpace(output) ? await stderr.ConfigureAwait(false) : output);
        }
        finally
        {
            // Dispose does not terminate a timed-out child. Kill and reap it before returning.
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                // The primary operation already reports timeout/cancellation; observe both readers too.
            }
        }
    }
}
