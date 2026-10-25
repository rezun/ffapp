using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFApp.Core.Models;
using FFApp.Core.Services;

namespace FFApp.ViewModels;

public sealed record ComparisonTrack(string Path, double OffsetSeconds, CropRect? Crop = null, double SourceStartSeconds = 0);
public sealed record ComparisonFrames(ComparisonVideoFrame Left, ComparisonVideoFrame? Right) : IDisposable
{
    public void Dispose() { Left.Dispose(); Right?.Dispose(); }
}

/// <summary>One presentation clock and a serial worker for two persistent FFmpeg decoders.</summary>
public sealed partial class ComparisonPlaybackViewModel : ObservableObject, IDisposable, IAsyncDisposable
{
    private readonly IComparisonDecoderFactory? _decoders;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _worker = new(1, 1);
    private readonly List<Task> _operations = [];
    private CancellationTokenSource? _cancellation;
    private IComparisonDecoder? _leftDecoder;
    private IComparisonDecoder? _rightDecoder;
    private ComparisonTrack? _openedLeft;
    private ComparisonTrack? _openedRight;
    private ComparisonTrack? _left;
    private ComparisonTrack? _right;
    private bool _updatingPosition;
    private bool _disposed;
    private double _frameDuration = 1.0 / 24;
    private double? _displayedLeftTime;
    private static readonly double FrameBoundaryTolerance = 1e-7;
    private static readonly TimeSpan ScrubDelay = TimeSpan.FromMilliseconds(40);

    public bool IsAvailable => _decoders is not null;
    public ComparisonTrack? LeftTrack => _left;
    public ComparisonTrack? RightTrack => _right;
    [ObservableProperty] private bool _hasMedia;
    [ObservableProperty] private bool _isSourcePreview = true;
    [NotifyPropertyChangedFor(nameof(PlayLabel))]
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _loop = true;
    [ObservableProperty] private double _durationSeconds = 1;
    [ObservableProperty] private double _positionSeconds;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _zoom = "Fit";
    [ObservableProperty] private string _layout = "Side by side";
    [NotifyPropertyChangedFor(nameof(HasFrames))]
    [ObservableProperty] private ComparisonFrames? _framesToDisplay;
    public bool HasFrames => FramesToDisplay is not null;
    public IReadOnlyList<string> ZoomOptions { get; } = ["Fit", "100%", "200%"];
    public IReadOnlyList<string> LayoutOptions { get; } = ["Side by side", "A only", "B only"];
    public string TimeDisplay => $"{TimeSpan.FromSeconds(PositionSeconds):hh\\:mm\\:ss\\.fff} / {TimeSpan.FromSeconds(DurationSeconds):hh\\:mm\\:ss\\.fff}";
    public string PlayLabel => IsPlaying ? "Pause" : "Play sample";

    public ComparisonPlaybackViewModel(IComparisonDecoderFactory? decoders, TimeProvider? time = null)
    {
        _decoders = decoders;
        _time = time ?? TimeProvider.System;
    }

    public static long MediaTime(ComparisonTrack track, double position, double duration) =>
        (long)Math.Round((track.OffsetSeconds + Math.Clamp(position, 0, Math.Max(0, duration - 0.04))) * 1000);

    private double SourceOrigin(ComparisonTrack track) =>
        Math.Ceiling(track.SourceStartSeconds / _frameDuration - FrameBoundaryTolerance) * _frameDuration;
    private double SampleOrigin(ComparisonTrack track) =>
        Math.Ceiling((track.SourceStartSeconds + track.OffsetSeconds) / _frameDuration - FrameBoundaryTolerance)
        * _frameDuration - SourceOrigin(track);

    public void LoadSource(ComparisonTrack source, double duration, double? frameRate) => Load(source, null, duration, frameRate);
    public void Load(ComparisonTrack left, ComparisonTrack? right, double duration, double? frameRate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var wasSourcePreview = IsSourcePreview;
        Reset();
        _left = left;
        _right = right;
        IsSourcePreview = right is null;
        DurationSeconds = duration;
        _frameDuration = 1 / (frameRate is > 0 && double.IsFinite(frameRate.Value) ? frameRate.Value : 24);
        if (IsSourcePreview) Layout = "A only";
        else if (wasSourcePreview) Layout = "Side by side";
        if (_decoders is null) { Status = "FFmpeg comparison decoder unavailable."; return; }
        Status = "Loading comparison…";
        // Selection callbacks are synchronous. RenderAsync handles all errors;
        // disposal cancels and awaits every tracked operation.
        Track(RenderAsync());
    }

    partial void OnPositionSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(TimeDisplay));
        if (_updatingPosition) return;
        Pause();
        _displayedLeftTime = null;
        if (_left is not null && _decoders is not null)
        {
            // A slider property setter cannot await. RenderAsync is the error boundary.
            Track(RenderAsync(debounce: true));
        }
    }
    partial void OnDurationSecondsChanged(double value) => OnPropertyChanged(nameof(TimeDisplay));
    partial void OnFramesToDisplayChanged(ComparisonFrames? oldValue, ComparisonFrames? newValue) => oldValue?.Dispose();

    private void Track(Task operation)
    {
        _operations.RemoveAll(task => task.IsCompleted);
        _operations.Add(operation);
    }
    private CancellationTokenSource BeginRequest()
    {
        _cancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        return cancellation;
    }
    private void FinishRequest(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
        cancellation.Dispose();
    }

    internal async Task RenderAsync(bool next = false, bool debounce = false)
    {
        if (_left is not { } left || _decoders is null || _disposed) return;
        var right = _right;
        var cancellation = BeginRequest();
        var ct = cancellation.Token;
        var position = PositionSeconds;
        var displayed = _displayedLeftTime;
        try
        {
            if (debounce) await Task.Delay(ScrubDelay, _time, ct);
            var frames = await DecodeAsync(left, right, position, displayed, next, ct);
            if (ct.IsCancellationRequested) { frames?.Dispose(); return; }
            if (frames is not null) Publish(frames, left);
            HasMedia = FramesToDisplay is not null;
            Status = "Paused · Audio muted";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"Comparison decoding failed: {ex}");
            if (!ct.IsCancellationRequested) Status = $"Playback failed: {ex.Message}";
        }
        finally { FinishRequest(cancellation); }
    }

    private async Task<ComparisonFrames?> DecodeAsync(ComparisonTrack left, ComparisonTrack? right,
        double position, double? displayed, bool next, CancellationToken ct)
    {
        // Snapshot mapping before dispatch: selection changes may update timing while cancelled work unwinds.
        var sampleOrigin = SampleOrigin(left);
        var originDifference = SourceOrigin(left) - SourceOrigin(right ?? left);
        var duration = DurationSeconds;
        var target = displayed ?? sampleOrigin + Math.Clamp(position, 0, Math.Max(0, duration - _frameDuration));
        await _worker.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                EnsureDecoders(left, right, ct);
                var decoder = _leftDecoder ?? throw new InvalidOperationException("No comparison decoder.");
                var selected = next ? decoder.ReadNext(target, ct)
                    : decoder.ReadAt(target, decoder.TimestampResolutionSeconds / 2, ct);
                if (selected is null) return null;
                if (selected.Seconds - sampleOrigin >= duration - FrameBoundaryTolerance)
                {
                    selected.Dispose();
                    return null;
                }
                try
                {
                    // Each PTS may be rounded by half a tick of its own time base.
                    // Combining those uncertainties avoids skip/repeat cadence when
                    // e.g. a Blu-ray MKV and a millisecond-based WebM are compared.
                    var tolerance = (decoder.TimestampResolutionSeconds
                        + (_rightDecoder?.TimestampResolutionSeconds ?? 0)) / 2;
                    var other = _rightDecoder?.ReadAt(Math.Max(0, selected.Seconds + originDifference), tolerance, ct);
                    return new ComparisonFrames(selected, other);
                }
                catch { selected.Dispose(); throw; }
            }, ct);
        }
        finally { _worker.Release(); }
    }

    private void EnsureDecoders(ComparisonTrack left, ComparisonTrack? right, CancellationToken ct)
    {
        if (_openedLeft == left && _openedRight == right) return;
        CloseDecoders();
        try
        {
            _leftDecoder = _decoders?.Open(left.Path, left.Crop, ct);
            _rightDecoder = right is null ? null : _decoders?.Open(right.Path, right.Crop, ct);
            _openedLeft = left;
            _openedRight = right;
        }
        catch { CloseDecoders(); throw; }
    }
    private void Publish(ComparisonFrames frames, ComparisonTrack left)
    {
        _displayedLeftTime = frames.Left.Seconds;
        _updatingPosition = true;
        try { PositionSeconds = Math.Clamp(frames.Left.Seconds - SampleOrigin(left), 0, DurationSeconds); }
        finally { _updatingPosition = false; }
        FramesToDisplay = frames;
    }

    [RelayCommand]
    private void TogglePlay()
    {
        if (!HasMedia) return;
        if (IsPlaying) Pause();
        else
        {
            // ICommand.Execute is synchronous; PlayAsync catches all errors.
            Track(PlayAsync());
        }
    }
    internal async Task PlayAsync()
    {
        if (_left is not { } left || _decoders is null || _disposed) return;
        var right = _right;
        var cancellation = BeginRequest();
        var ct = cancellation.Token;
        IsPlaying = true;
        Status = "Playing · Audio muted";
        var clockStart = _time.GetTimestamp();
        var startPosition = PositionSeconds;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frames = await DecodeAsync(left, right, PositionSeconds, _displayedLeftTime, true, ct);
                if (frames is null)
                {
                    if (!Loop) break;
                    frames = await DecodeAsync(left, right, 0, null, false, ct);
                    if (frames is null) break;
                    startPosition = 0;
                    clockStart = _time.GetTimestamp();
                }
                try
                {
                    var due = TimeSpan.FromSeconds(Math.Max(0, frames.Left.Seconds - SampleOrigin(left) - startPosition));
                    var remaining = due - _time.GetElapsedTime(clockStart);
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining, _time, ct);
                    ct.ThrowIfCancellationRequested();
                    Publish(frames, left);
                }
                catch { frames.Dispose(); throw; }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"Comparison playback failed: {ex}");
            if (!ct.IsCancellationRequested) Status = $"Playback failed: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation))
            {
                IsPlaying = false;
                if (Status.StartsWith("Playing", StringComparison.Ordinal)) Status = "Paused · Audio muted";
            }
            FinishRequest(cancellation);
        }
    }
    public void Pause()
    {
        _cancellation?.Cancel();
        _cancellation = null;
        IsPlaying = false;
        if (HasMedia) Status = "Paused · Audio muted";
    }
    [RelayCommand]
    private Task StepForward()
    {
        if (!HasMedia) return Task.CompletedTask;
        Pause();
        var operation = RenderAsync(next: true);
        Track(operation);
        return operation;
    }
    [RelayCommand]
    private void SeekBackward()
    {
        Pause();
        PositionSeconds = Math.Max(0, PositionSeconds - 1);
    }
    public void Reset()
    {
        Pause();
        HasMedia = false;
        FramesToDisplay = null;
        _displayedLeftTime = null;
        _left = null;
        _right = null;
        _updatingPosition = true;
        try { PositionSeconds = 0; }
        finally { _updatingPosition = false; }
        // Selection callbacks are synchronous. Cleanup is serialized with native calls
        // and awaited by DisposeAsync, so active native resources cannot be freed early.
        Track(CloseDecodersAsync());
    }
    private async Task CloseDecodersAsync()
    {
        try
        {
            await _worker.WaitAsync();
            try { await Task.Run(CloseDecoders); }
            finally { _worker.Release(); }
        }
        catch (Exception ex) { Debug.WriteLine($"Comparison decoder cleanup failed: {ex}"); }
    }

    public async Task ResetAsync()
    {
        Reset();
        await Task.WhenAll(_operations.ToArray());
    }
    private void CloseDecoders()
    {
        _leftDecoder?.Dispose();
        _rightDecoder?.Dispose();
        _leftDecoder = null;
        _rightDecoder = null;
        _openedLeft = null;
        _openedRight = null;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Reset();
        GC.SuppressFinalize(this);
    }
    public async ValueTask DisposeAsync()
    {
        Dispose();
        await Task.WhenAll(_operations.ToArray());
    }
}
