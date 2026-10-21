using FFApp.Core.Models;
using FFApp.Infrastructure;
using FluentAssertions;

namespace FFApp.Tests.Infrastructure;

public sealed class ProcessRunnerPauseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunningProcess_StopsProducingOutputWhilePaused_ThenResumesOrCancels(bool cancel)
    {
        var state = new ExecutionPauseState(TimeProvider.System);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var firstOutput = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lines = 0;
        var command = OperatingSystem.IsWindows()
            ? new GeneratedCommand("powershell.exe",
                ["-NoProfile", "-Command", "1..15 | ForEach-Object { Write-Output tick; Start-Sleep -Milliseconds 100 }"],
                "pause test", ProcessProtocol.Plain)
            : new GeneratedCommand("/bin/sh",
                ["-c", "i=0; while [ $i -lt 15 ]; do echo tick; sleep 0.1; i=$((i+1)); done"],
                "pause test", ProcessProtocol.Plain);

        var run = new ProcessRunner(TimeProvider.System, new FFApp.Core.TranscodingPreferences()).RunAsync(command, logCallback: _ =>
        {
            Interlocked.Increment(ref lines);
            firstOutput.TrySetResult();
        }, pauseState: state, ct: cts.Token);

        try
        {
            await firstOutput.Task.WaitAsync(cts.Token);
            state.Pause();
            // Allow already-buffered stdout to drain before measuring the suspension.
            await Task.Delay(200, cts.Token);
            var pausedLines = Volatile.Read(ref lines);
            await Task.Delay(300, cts.Token);
            Volatile.Read(ref lines).Should().Be(pausedLines);
            run.IsCompleted.Should().BeFalse();

            if (cancel)
                cts.Cancel();
            else
                state.Resume();

            var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
            result.State.Should().Be(cancel ? JobState.Canceled : JobState.Completed);
            if (!cancel)
                Volatile.Read(ref lines).Should().Be(15);
        }
        finally
        {
            await cts.CancelAsync();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task PausedBeforeStartup_CancellationDoesNotLaunchProcess()
    {
        var state = new ExecutionPauseState(TimeProvider.System);
        state.Pause();
        using var cts = new CancellationTokenSource();
        var run = new ProcessRunner(TimeProvider.System, new FFApp.Core.TranscodingPreferences()).RunAsync(
            new GeneratedCommand("must-not-be-started", [], ""), pauseState: state, ct: cts.Token);
        cts.Cancel();
        Func<Task> act = () => run;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
