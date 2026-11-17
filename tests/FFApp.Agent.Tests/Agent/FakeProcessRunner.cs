using FFApp.Core.Models;
using FFApp.Core.Services;

namespace FFApp.Agent.Tests.Agent;

/// <summary>
/// Pretends to be FFmpeg: reports progress and writes the output file (the last argument).
/// With <see cref="HoldJobs"/> set, each encode waits until <see cref="Release"/> is called,
/// honoring pause and cancellation like a real process.
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Lock _lock = new();
    private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool HoldJobs { get; init; }
    public byte[] OutputContent { get; init; } = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
    public int ExitCode { get; set; }
    public List<GeneratedCommand> Commands { get; } = [];

    public Task Started => _started.Task;

    public void Release()
    {
        lock (_lock)
            _release.TrySetResult();
    }

    /// <summary>Holds the next encodes again after <see cref="Release"/>.</summary>
    public void Rearm()
    {
        lock (_lock)
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public async Task<ProcessExecutionResult> RunAsync(
        GeneratedCommand command,
        double? expectedDurationSeconds = null,
        int? expectedFrameCount = null,
        Action<ProcessProgress>? progressCallback = null,
        Action<string>? logCallback = null,
        ExecutionPauseState? pauseState = null,
        CancellationToken ct = default)
    {
        lock (_lock)
            Commands.Add(command);
        _started.TrySetResult();
        logCallback?.Invoke("frame=1 fps=1.0");
        progressCallback?.Invoke(new ProcessProgress(1, 30, 50, 1.0));

        if (HoldJobs)
        {
            try
            {
                Task release;
                lock (_lock)
                    release = _release.Task;
                await release.WaitAsync(ct);
                if (pauseState is not null)
                    await pauseState.WaitWhilePausedAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return new ProcessExecutionResult(command, JobState.Canceled, -1, 1, "canceled");
            }
        }

        if (ExitCode != 0)
        {
            logCallback?.Invoke("Error while encoding: out of cheese");
            return new ProcessExecutionResult(command, JobState.Failed, ExitCode, 1, "Error while encoding: out of cheese");
        }

        if (command.Protocol == ProcessProtocol.Ffmpeg && command.Arguments.Length > 0)
            await File.WriteAllBytesAsync(command.Arguments[^1], OutputContent, CancellationToken.None);
        progressCallback?.Invoke(new ProcessProgress(2, 60, 100, 1.0));
        return new ProcessExecutionResult(command, JobState.Completed, 0, 2, "");
    }
}
