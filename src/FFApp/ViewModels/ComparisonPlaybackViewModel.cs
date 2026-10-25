using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFApp.Core.Models;
using LibVLCSharp.Shared;

namespace FFApp.ViewModels;

public sealed record ComparisonTrack(string Path, double OffsetSeconds, CropRect? Crop = null);

public sealed partial class ComparisonPlaybackViewModel : ObservableObject, IDisposable
{
    private readonly LibVLC? _vlc;
    private DispatcherTimer? _timer;
    private Media? _leftMedia;
    private Media? _rightMedia;
    private ComparisonTrack? _left;
    private ComparisonTrack? _right;
    private bool _priming;
    private bool _updatingPosition;
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

    public IReadOnlyList<string> ZoomOptions { get; } = ["Fit", "100%", "200%"];
    public IReadOnlyList<string> LayoutOptions { get; } = ["Side by side", "A only", "B only"];
    public string TimeDisplay => $"{TimeSpan.FromSeconds(PositionSeconds):hh\\:mm\\:ss\\.fff} / {TimeSpan.FromSeconds(DurationSeconds):hh\\:mm\\:ss\\.fff}";
    public string PlayLabel => IsPlaying ? "Pause" : "Play sample";

    public ComparisonPlaybackViewModel(LibVLC? vlc)
    {
        _vlc = vlc;
    }

    public static long MediaTime(ComparisonTrack track, double position, double duration) =>
        (long)((track.OffsetSeconds + Math.Clamp(position, 0, Math.Max(0, duration - 0.04))) * 1000);

    public void LoadSource(ComparisonTrack source, double duration, double? frameRate) => Load(source, null, duration, frameRate);

    public void Load(ComparisonTrack left, ComparisonTrack? right, double duration, double? frameRate)
    {
        var wasSourcePreview = IsSourcePreview;
        Reset();
        _left = left;
        _right = right;
        IsSourcePreview = right is null;
        DurationSeconds = duration;
        if (IsSourcePreview) Layout = "A only";
        else if (wasSourcePreview) Layout = "Side by side";
        if (_vlc is null)
        {
            Status = "Install VLC to preview samples. You can still create test clips and compare their sizes.";
            return;
        }
        _frameDuration = 1 / (frameRate is > 0 ? frameRate.Value : 24);
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
        if (!_updatingPosition) Seek(value);
    }

    partial void OnDurationSecondsChanged(double value) => OnPropertyChanged(nameof(TimeDisplay));
    partial void OnZoomChanged(string value) => ApplyZoom();
    partial void OnIsPlayingChanged(bool value)
    {
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
            LeftPlayer.Time = MediaTime(_left, value, DurationSeconds);
        if (_right is not null && RightPlayer is not null)
            RightPlayer.Time = MediaTime(_right, value, DurationSeconds);
    }

    [RelayCommand]
    private void TogglePlay()
    {
        if (!HasMedia) return;
        if (IsPlaying) Pause();
        else
        {
            LeftPlayer?.SetPause(false);
            RightPlayer?.SetPause(false);
            IsPlaying = true;
        }
    }

    public void Pause()
    {
        LeftPlayer?.SetPause(true);
        RightPlayer?.SetPause(true);
        IsPlaying = false;
    }

    [RelayCommand]
    private void StepForward() { Pause(); PositionSeconds = Math.Min(DurationSeconds, PositionSeconds + _frameDuration); }
    [RelayCommand]
    private void StepBackward() { Pause(); PositionSeconds = Math.Max(0, PositionSeconds - _frameDuration); }

    public void Reset()
    {
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
