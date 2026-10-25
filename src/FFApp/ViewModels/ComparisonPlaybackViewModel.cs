using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFApp.Core.Models;
using FFApp.Core.Services;
using LibVLCSharp.Shared;

namespace FFApp.ViewModels;

public sealed record ComparisonTrack(string Path, double OffsetSeconds, CropRect? Crop = null, double SourceStartSeconds = 0);
public sealed record ComparisonFrames(byte[] Left, byte[]? Right);

public sealed partial class ComparisonPlaybackViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan FramePreviewDelay = TimeSpan.FromMilliseconds(80);
    private static readonly double FrameBoundaryTolerance = 1e-7;
    private static readonly double FrameSeekLead = 0.25;
    private static readonly int PrefetchFrameCount = 4;
    private readonly LibVLC? _vlc;
    private readonly IComparisonFrameService? _frames;
    private CancellationTokenSource? _frameCancellation;
    private DispatcherTimer? _timer;
    private Media? _leftMedia;
    private Media? _rightMedia;
    private ComparisonTrack? _left;
    private ComparisonTrack? _right;
    private bool _priming;
    private bool _updatingPosition;
    private bool _steppingFrame;
    private double _frameDuration = 1.0 / 24;

    public MediaPlayer? LeftPlayer { get; private set; }
    public MediaPlayer? RightPlayer { get; private set; }
    public bool IsAvailable => _vlc is not null;
    public ComparisonTrack? LeftTrack => _left;
    public ComparisonTrack? RightTrack => _right;
    public event EventHandler? PlayersChanged;

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
    [NotifyPropertyChangedFor(nameof(HasStillFrames))]
    [ObservableProperty] private ComparisonFrames? _framesToDisplay;
    public bool HasStillFrames => FramesToDisplay is not null;

    public IReadOnlyList<string> ZoomOptions { get; } = ["Fit", "100%", "200%"];
    public IReadOnlyList<string> LayoutOptions { get; } = ["Side by side", "A only", "B only"];
    public string TimeDisplay => $"{TimeSpan.FromSeconds(PositionSeconds):hh\\:mm\\:ss\\.fff} / {TimeSpan.FromSeconds(DurationSeconds):hh\\:mm\\:ss\\.fff}";
    public string PlayLabel => IsPlaying ? "Pause" : "Play sample";

    public ComparisonPlaybackViewModel(LibVLC? vlc, IComparisonFrameService? frames = null)
    {
        _vlc = vlc;
        _frames = frames;
    }

    public static long MediaTime(ComparisonTrack track, double position, double duration) =>
        (long)Math.Round((track.OffsetSeconds + Math.Clamp(position, 0, Math.Max(0, duration - 0.04))) * 1000);

    internal static double FrameTime(ComparisonTrack track, double position, double duration, double frameRate)
    {
        var sampleFrame = Math.Ceiling((track.SourceStartSeconds + track.OffsetSeconds) * frameRate - FrameBoundaryTolerance);
        var firstClipFrame = Math.Ceiling(track.SourceStartSeconds * frameRate - FrameBoundaryTolerance);
        var relativeFrame = Math.Floor(Math.Clamp(position, 0, Math.Max(0, duration - 1 / frameRate)) * frameRate + FrameBoundaryTolerance);
        // Accurate FFmpeg seeking selects the first frame at or after the target.
        // Seek inside the preceding frame, avoiding millisecond/frame-boundary rounding.
        return Math.Max(0, (sampleFrame + relativeFrame - firstClipFrame - FrameSeekLead) / frameRate);
    }

    public void LoadSource(ComparisonTrack source, double duration, double? frameRate) => Load(source, null, duration, frameRate);

    public void Load(ComparisonTrack left, ComparisonTrack? right, double duration, double? frameRate)
    {
        var wasSourcePreview = IsSourcePreview;
        Reset();
        _left = left;
        _right = right;
        IsSourcePreview = right is null;
        DurationSeconds = duration;
        _frameDuration = 1 / (frameRate is > 0 && double.IsFinite(frameRate.Value) ? frameRate.Value : 24);
        if (IsSourcePreview) Layout = "A only";
        else if (wasSourcePreview) Layout = "Side by side";
        if (_vlc is null)
        {
            Status = "Install VLC to preview samples. You can still create test clips and compare their sizes.";
            return;
        }
        _leftMedia = CreateMedia(left);
        LeftPlayer = new MediaPlayer(_leftMedia) { Mute = true };
        ApplyCrop(LeftPlayer, left.Crop);
        if (right is not null)
        {
            _rightMedia = CreateMedia(right);
            RightPlayer = new MediaPlayer(_rightMedia) { Mute = true };
            ApplyCrop(RightPlayer, right.Crop);
        }
        ApplyZoom();
        PlayersChanged?.Invoke(this, EventArgs.Empty);
        _priming = true;
        LeftPlayer.Play();
        RightPlayer?.Play();
        Status = IsSourcePreview ? "Loading original sample…" : "Loading comparison…";
        if (_timer is null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _timer.Tick += OnTick;
        }
        _timer.Start();
    }

    private Media CreateMedia(ComparisonTrack track)
    {
        var media = new Media(_vlc ?? throw new InvalidOperationException("VLC unavailable."), track.Path, FromType.FromPath);
        media.AddOption(":no-audio");
        media.AddOption(":no-spu");
        media.AddOption(":start-time=" + track.OffsetSeconds.ToString("F6", CultureInfo.InvariantCulture));
        return media;
    }

    private static void ApplyCrop(MediaPlayer player, CropRect? crop)
    {
        if (crop is { Width: > 0, Height: > 0 })
            player.CropGeometry = FormattableString.Invariant($"{crop.Width}x{crop.Height}+{crop.X}+{crop.Y}");
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // DispatcherTimer is a synchronous UI callback. Handle errors at this boundary
        // so an unavailable decoder cannot take down the comparison window.
        try
        {
            if (LeftPlayer is null || _left is null)
                return;
            if (LeftPlayer.State == VLCState.Error || RightPlayer?.State == VLCState.Error)
            {
                _timer?.Stop();
                HasMedia = false;
                Status = "VLC could not decode this sample. Choose another sample or check the installed VLC codecs.";
                return;
            }
            if (_priming)
            {
                if (LeftPlayer.State is not (VLCState.Playing or VLCState.Paused)
                    || (RightPlayer is not null && RightPlayer.State is not (VLCState.Playing or VLCState.Paused))) return;
                LeftPlayer.SetPause(true);
                RightPlayer?.SetPause(true);
                Seek(0);
                QueueFrames();
                _priming = false;
                HasMedia = true;
                Status = IsSourcePreview
                    ? "Paused · Audio muted"
                    : "Audio muted";
                return;
            }
            if (!IsPlaying) return;
            var position = Math.Max(0, LeftPlayer.Time / 1000.0 - _left.OffsetSeconds);
            if (position >= DurationSeconds - _frameDuration || LeftPlayer.State == VLCState.Ended
                || RightPlayer?.State == VLCState.Ended)
            {
                if (Loop)
                {
                    LeftPlayer.Play();
                    RightPlayer?.Play();
                    Seek(0);
                }
                else Pause();
                return;
            }
            if (RightPlayer is not null && _right is not null
                && Math.Abs(RightPlayer.Time - MediaTime(_right, position, DurationSeconds)) > 120)
                RightPlayer.Time = MediaTime(_right, position, DurationSeconds);
            _updatingPosition = true;
            PositionSeconds = Math.Min(position, DurationSeconds);
            _updatingPosition = false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Comparison playback failed: {ex}");
            _timer?.Stop();
            IsPlaying = false;
            Status = $"Playback failed: {ex.Message}";
        }
    }

    partial void OnPositionSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(TimeDisplay));
        if (!_updatingPosition)
        {
            if (IsPlaying) Pause(false);
            if (_frames is null) Seek(value);
        }
        if (!IsPlaying) QueueFrames();
    }

    partial void OnDurationSecondsChanged(double value) => OnPropertyChanged(nameof(TimeDisplay));
    partial void OnZoomChanged(string value) => ApplyZoom();
    partial void OnIsPlayingChanged(bool value)
    {
        if (value) CancelFrames();
        if (HasMedia && IsSourcePreview)
            Status = value ? "Playing · Audio muted"
                : "Paused · Audio muted";
    }

    private void ApplyZoom()
    {
        var scale = Zoom switch { "100%" => 1f, "200%" => 2f, _ => 0f };
        if (LeftPlayer is not null) LeftPlayer.Scale = scale;
        if (RightPlayer is not null) RightPlayer.Scale = scale;
    }

    private void Seek(double value)
    {
        if (_left is not null && LeftPlayer is not null)
        {
            LeftPlayer.Time = MediaTime(_left, value, DurationSeconds);
        }
        if (_right is not null && RightPlayer is not null)
        {
            RightPlayer.Time = MediaTime(_right, value, DurationSeconds);
        }
    }

    private void QueueFrames()
    {
        // Property changes and VLC timer ticks are synchronous callbacks. The
        // async boundary below catches errors and cancels superseded requests.
        _ = RefreshFramesAsync(!_steppingFrame);
    }

    internal async Task RefreshFramesAsync(bool debounce = true)
    {
        _frameCancellation?.Cancel();
        if (_frames is null || _left is null || IsPlaying) return;
        var cancellation = new CancellationTokenSource();
        _frameCancellation = cancellation;
        try
        {
            var left = _left;
            var right = _right;
            var position = PositionSeconds;
            var leftTime = FrameTime(left, position, DurationSeconds, 1 / _frameDuration);
            var rightTime = right is null ? 0 : FrameTime(right, position, DurationSeconds, 1 / _frameDuration);
            var leftCached = _frames.TryGetFrame(left.Path, leftTime, left.Crop, out var leftFrame);
            byte[]? rightFrame = null;
            var rightCached = right is null || _frames.TryGetFrame(right.Path, rightTime, right.Crop, out rightFrame);
            if (leftCached && rightCached)
            {
                FramesToDisplay = new ComparisonFrames(leftFrame, rightFrame);
                PrefetchFrames(position);
                return;
            }
            // Scrubbing is debounced; explicit frame steps start immediately.
            if (debounce) await Task.Delay(FramePreviewDelay, cancellation.Token);
            var leftTask = _frames.DecodeAsync(left.Path,
                leftTime, left.Crop, cancellation.Token);
            var rightTask = right is null ? Task.FromResult<byte[]?>(null)
                : DecodeRightAsync(right, rightTime, cancellation.Token);
            await Task.WhenAll(new Task[] { leftTask, rightTask });
            if (!cancellation.IsCancellationRequested && ReferenceEquals(_frameCancellation, cancellation) && !IsPlaying)
            {
                FramesToDisplay = new ComparisonFrames(await leftTask, await rightTask);
                PrefetchFrames(position);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"Comparison frame decoding failed: {ex}");
            if (ReferenceEquals(_frameCancellation, cancellation)) Status = $"Frame preview failed: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_frameCancellation, cancellation)) _frameCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task<byte[]?> DecodeRightAsync(ComparisonTrack track, double seconds, CancellationToken ct) =>
        _frames is null ? null : await _frames.DecodeAsync(track.Path,
            seconds, track.Crop, ct);

    private void PrefetchFrames(double position)
    {
        if (_frames is null || _left is null) return;
        var nextFrame = Math.Floor(position / _frameDuration + FrameBoundaryTolerance) + 1;
        var lastFrame = Math.Floor(Math.Max(0, DurationSeconds / _frameDuration - 1) + FrameBoundaryTolerance);
        var count = (int)Math.Clamp(lastFrame - nextFrame + 1, 0, PrefetchFrameCount);
        if (count == 0) return;
        var nextPosition = nextFrame * _frameDuration;
        _frames.Prefetch(_left.Path, FrameTime(_left, nextPosition, DurationSeconds, 1 / _frameDuration),
            _frameDuration, count, _left.Crop);
        if (_right is { } right)
            _frames.Prefetch(right.Path, FrameTime(right, nextPosition, DurationSeconds, 1 / _frameDuration),
                _frameDuration, count, right.Crop);
    }

    private void CancelFrames()
    {
        _frameCancellation?.Cancel();
        _frameCancellation = null;
        FramesToDisplay = null;
    }

    [RelayCommand]
    private void TogglePlay()
    {
        if (!HasMedia) return;
        if (IsPlaying) Pause();
        else
        {
            Seek(PositionSeconds);
            LeftPlayer?.SetPause(false);
            RightPlayer?.SetPause(false);
            IsPlaying = true;
        }
    }

    public void Pause(bool refreshFrames = true)
    {
        LeftPlayer?.SetPause(true);
        RightPlayer?.SetPause(true);
        IsPlaying = false;
        if (refreshFrames) QueueFrames();
    }

    [RelayCommand]
    private void StepForward()
    {
        if (!HasMedia || LeftPlayer is null) return;
        AdvanceFrame(LeftPlayer.NextFrame, RightPlayer is { } right ? right.NextFrame : null);
    }

    internal void AdvanceFrame(Action advanceLeft, Action? advanceRight)
    {
        Pause(false);
        var target = PositionSeconds + _frameDuration;
        if (target >= DurationSeconds - _frameDuration * 1e-9) return;

        // The still-frame backend decodes the new position below. Only use VLC
        // frame stepping when no still-frame service is available.
        if (_frames is null)
        {
            advanceLeft();
            advanceRight?.Invoke();
        }
        _updatingPosition = true;
        _steppingFrame = true;
        try { PositionSeconds = target; }
        finally { _updatingPosition = false; _steppingFrame = false; }
    }

    [RelayCommand]
    private void SeekBackward()
    {
        Pause();
        PositionSeconds = Math.Max(0, PositionSeconds - 1);
    }

    public void Reset()
    {
        CancelFrames();
        _frames?.ClearCache();
        _timer?.Stop();
        HasMedia = false;
        IsPlaying = false;
        var left = LeftPlayer;
        var right = RightPlayer;
        LeftPlayer = null;
        RightPlayer = null;
        PlayersChanged?.Invoke(this, EventArgs.Empty);
        left?.Stop();
        right?.Stop();
        left?.Dispose();
        right?.Dispose();
        _leftMedia?.Dispose();
        _rightMedia?.Dispose();
        _leftMedia = null;
        _rightMedia = null;
        _left = null;
        _right = null;
        _priming = false;
        PositionSeconds = 0;
    }

    public void Dispose()
    {
        Reset();
        if (_timer is not null) _timer.Tick -= OnTick;
        GC.SuppressFinalize(this);
    }
}
