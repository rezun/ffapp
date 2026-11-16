using System.Diagnostics;
using FFApp.Core.Models;
using FFApp.Infrastructure;
using FluentAssertions;

namespace FFApp.Shared.Tests.Infrastructure;

public sealed class UnixToolFactAttribute : FactAttribute
{
    public UnixToolFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Uses a POSIX script as a controlled executable.";
    }
}

public sealed class DependencyManagerProcessTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("ffapp-tool-tests-").FullName;
    public void Dispose() => Directory.Delete(_directory, true);

    private string Script(string name, string body)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [UnixToolFact]
    public async Task OptionalToolAndLibplaceboMayBeMissingWithoutBlockingRequiredTools()
    {
        var ffmpeg = Script("ffmpeg", """
            case "$*" in
              *-version*) echo 'ffmpeg version 9.0.2' ;;
              *-encoders*) echo ' V....D libx264 libx264 H.264 / AVC' ;;
              *-filters*) echo ' .. scale V->V Scale the input video size' ;;
            esac
            """);
        var ffprobe = Script("ffprobe", "echo 'ffprobe version 9.0.2'");
        var result = await new DependencyManager().ValidatePathsAsync(new(ffmpeg, ffprobe, Path.Combine(_directory, "missing")));
        result.IsReady.Should().BeTrue();
        result.Mkvpropedit.IsValid.Should().BeFalse();
        result.IsLibplaceboAvailable.Should().BeFalse();
        result.Capabilities?.Encoders.Should().Contain("libx264");
        result.Capabilities?.Filters.Should().Contain("scale");
    }

    [UnixToolFact]
    public async Task VersionTimeout_TerminatesAndReapsTheChildProcess()
    {
        var pidFile = Path.Combine(_directory, "pid");
        var ffmpeg = Script("ffmpeg", $"echo $$ > '{pidFile}'\nexec /bin/sleep 30");
        var missing = Path.Combine(_directory, "missing");
        var result = await new DependencyManager().ValidatePathsAsync(new(ffmpeg, missing, missing));
        result.Ffmpeg.Error.Should().Contain("timed out");
        var pid = int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture);
        var getProcess = () => Process.GetProcessById(pid);
        getProcess.Should().Throw<ArgumentException>();
    }

    [UnixToolFact]
    public async Task SuccessfulExitFromUnrelatedExecutable_IsRejected()
    {
        var unrelated = Script("unrelated", "exit 0");
        var result = await new DependencyManager().ValidatePathsAsync(new(unrelated, unrelated, unrelated));
        result.IsReady.Should().BeFalse();
        result.Ffmpeg.Error.Should().Contain("recognizable");
        result.Mkvpropedit.IsValid.Should().BeFalse();
    }
}
