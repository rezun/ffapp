using FFApp.Core.Models;
using FluentAssertions;

namespace FFApp.Tests.Infrastructure;

public sealed class ExecutionPauseStateTests
{
    [Fact]
    public async Task PauseAndResume_AreIdempotentAndControlTheActiveProcess()
    {
        var state = new ExecutionPauseState(TimeProvider.System);
        var pauses = 0;
        var resumes = 0;
        state.StartProcess(() => { }, () => pauses++, () => resumes++);

        state.Pause();
        state.Pause();
        var wait = state.WaitWhilePausedAsync();
        state.IsPaused.Should().BeTrue();
        wait.IsCompleted.Should().BeFalse();
        pauses.Should().Be(1);

        state.Resume();
        state.Resume();
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        state.IsPaused.Should().BeFalse();
        resumes.Should().Be(1);
    }

    [Fact]
    public void PauseDuringProcessTransition_IsAppliedToTheNewProcess()
    {
        var state = new ExecutionPauseState(TimeProvider.System);
        var oldResumes = 0;
        state.StartProcess(() => { }, () => { }, () => oldResumes++);
        state.Pause();
        state.DetachProcess();
        var calls = new List<string>();

        state.StartProcess(() => calls.Add("start"), () => calls.Add("pause"), () => calls.Add("resume"));
        calls.Should().Equal("start", "pause");
        state.Resume();
        calls.Should().Equal("start", "pause", "resume");
        oldResumes.Should().Be(0);
    }

    [Fact]
    public async Task WaitWhilePaused_CanBeCanceledWithoutResume()
    {
        var state = new ExecutionPauseState(TimeProvider.System);
        state.Pause();
        using var cts = new CancellationTokenSource();
        var wait = state.WaitWhilePausedAsync(cts.Token);
        cts.Cancel();

        Func<Task> act = () => wait;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void NativeFailure_DoesNotChangePauseState()
    {
        var state = new ExecutionPauseState(TimeProvider.System);
        state.StartProcess(() => { }, () => throw new InvalidOperationException("pause failed"), () => { });
        var pause = () => state.Pause();
        pause.Should().Throw<InvalidOperationException>();
        state.IsPaused.Should().BeFalse();

        state.DetachProcess();
        state.Pause();
        state.StartProcess(() => { }, () => { }, () => throw new InvalidOperationException("resume failed"));
        var resume = () => state.Resume();
        resume.Should().Throw<InvalidOperationException>();
        state.IsPaused.Should().BeTrue();
    }

    [Fact]
    public void TotalPausedTime_AccumulatesOnlyWhilePaused()
    {
        var time = new ManualTimeProvider();
        var state = new ExecutionPauseState(time);
        time.Advance(10);
        state.TotalPausedTime.Should().Be(TimeSpan.Zero);
        state.Pause();
        time.Advance(3);
        state.TotalPausedTime.Should().Be(TimeSpan.FromSeconds(3));
        state.Resume();
        time.Advance(10);
        state.Pause();
        time.Advance(2);
        state.Resume();
        state.TotalPausedTime.Should().Be(TimeSpan.FromSeconds(5));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(int seconds) => _timestamp += TimeSpan.FromSeconds(seconds).Ticks;
    }
}
