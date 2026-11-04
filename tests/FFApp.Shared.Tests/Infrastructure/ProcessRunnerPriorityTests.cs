using System.Globalization;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Infrastructure;
using FluentAssertions;

namespace FFApp.Shared.Tests.Infrastructure;

public sealed class ProcessRunnerPriorityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CreateStartInfo_PreservesArgumentsAndUsesNiceOnlyOnUnix(bool lowPriority, bool windows)
    {
        var command = new GeneratedCommand("/path with spaces/ffmpeg", ["-i", "a file;name.mp4"], "");
        var info = ProcessRunner.CreateStartInfo(command, lowPriority, windows);
        var arguments = ProcessRunner.BuildArguments(command);

        info.UseShellExecute.Should().BeFalse();
        info.RedirectStandardOutput.Should().BeTrue();
        info.RedirectStandardError.Should().BeTrue();
        if (lowPriority && !windows)
        {
            info.FileName.Should().Be("/usr/bin/nice");
            info.ArgumentList.Should().Equal(new[] { "-n", "10", command.ExecutablePath }.Concat(arguments));
        }
        else
        {
            info.FileName.Should().Be(command.ExecutablePath);
            info.ArgumentList.Should().Equal(arguments);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_AppliesPriorityToActualProcess(bool withPauseState)
    {
        // nice is Unix-only; Windows priority is not observable this way. xUnit v2 has no runtime skip
        // here (no Xunit.SkippableFact), so this passes vacuously on Windows.
        if (OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(Path.GetTempPath(), $"ffapp-priority-{Guid.NewGuid()}.sh");
        try
        {
            await File.WriteAllTextAsync(path, "#!/bin/sh\nps -o ni= -p $$ >&2\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            // The script ignores the FFmpeg progress arguments supplied by ProcessRunner.
            var command = new GeneratedCommand(path, [], "");
            var preferences = new TranscodingPreferences();
            var runner = new ProcessRunner(TimeProvider.System, preferences);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var normal = await runner.RunAsync(command, ct: cts.Token);
            preferences.UseLowPriority = true;
            var state = withPauseState ? new ExecutionPauseState(TimeProvider.System) : null;
            var lowered = await runner.RunAsync(command, pauseState: state, ct: cts.Token);

            normal.State.Should().Be(JobState.Completed, normal.StderrOutput);
            lowered.State.Should().Be(JobState.Completed, lowered.StderrOutput);
            var baseline = int.Parse(normal.StderrOutput.Trim(), CultureInfo.InvariantCulture);
            var actual = int.Parse(lowered.StderrOutput.Trim(), CultureInfo.InvariantCulture);
            actual.Should().Be(Math.Min(baseline + 10, 19));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RunAsync_PlainCommandsRetainNormalPriority()
    {
        var command = OperatingSystem.IsWindows()
            ? new GeneratedCommand("powershell.exe", ["-NoProfile", "-Command",
                "[Console]::Error.WriteLine([System.Diagnostics.Process]::GetCurrentProcess().PriorityClass)"], "", ProcessProtocol.Plain)
            : new GeneratedCommand("/bin/sh", ["-c", "ps -o ni= -p $$ >&2"], "", ProcessProtocol.Plain);
        var preferences = new TranscodingPreferences();
        var runner = new ProcessRunner(TimeProvider.System, preferences);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var normal = await runner.RunAsync(command, ct: cts.Token);
        preferences.UseLowPriority = true;
        var unchanged = await runner.RunAsync(command, ct: cts.Token);

        normal.State.Should().Be(JobState.Completed);
        unchanged.State.Should().Be(JobState.Completed);
        unchanged.StderrOutput.Should().Be(normal.StderrOutput);
    }
}
