namespace FFApp.Core.Models;

/// <summary>
/// Per-execution synchronization state, shared by the queue and its child processes.
/// A pause survives process boundaries (for example between encoding passes).
/// </summary>
public sealed class ExecutionPauseState(TimeProvider timeProvider)
{
    private readonly Lock _lock = new();
    private TaskCompletionSource? _resumeSignal;
    private Action? _pauseProcess;
    private Action? _resumeProcess;
    private long _pauseStarted;
    private TimeSpan _completedPauses;

    public TimeSpan TotalPausedTime
    {
        get
        {
            lock (_lock)
                return _completedPauses + (_resumeSignal is null
                    ? TimeSpan.Zero
                    : timeProvider.GetElapsedTime(_pauseStarted));
        }
    }

    public bool IsPaused
    {
        get { lock (_lock) return _resumeSignal is not null; }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_resumeSignal is not null) return;
            _pauseProcess?.Invoke();
            _pauseStarted = timeProvider.GetTimestamp();
            _resumeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (_resumeSignal is null) return;
            _resumeProcess?.Invoke();
            _completedPauses += timeProvider.GetElapsedTime(_pauseStarted);
            _resumeSignal.SetResult();
            _resumeSignal = null;
        }
    }

    public Task WaitWhilePausedAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_lock)
            return _resumeSignal?.Task.WaitAsync(ct) ?? Task.CompletedTask;
    }

    // Serialize process startup/teardown with Pause and Resume so no request is lost.
    public void StartProcess(Action start, Action pause, Action resume)
    {
        lock (_lock)
        {
            start();
            _pauseProcess = pause;
            _resumeProcess = resume;
            if (_resumeSignal is not null)
                pause();
        }
    }

    public void DetachProcess()
    {
        lock (_lock)
        {
            _pauseProcess = null;
            _resumeProcess = null;
        }
    }
}
