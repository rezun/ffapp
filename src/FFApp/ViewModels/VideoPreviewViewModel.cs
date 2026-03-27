using Avalonia.Data.Converters;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFApp.Core.Models;
using LibVLCSharp.Shared;

namespace FFApp.ViewModels;

public partial class VideoPreviewViewModel : ObservableObject, IDisposable
{
    public static readonly FuncValueConverter<bool, string> ExpandCollapseConverter =
        new(expanded => expanded ? "\u25BC Collapse" : "\u25B6 Expand");

    private readonly LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media? _currentMedia;
    private double _frameRate = 30.0;
    private bool _disposed;
    private bool _pauseOnFirstPlay;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _hasMedia;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private double _position;

    [ObservableProperty]
    private long _timeMs;

    [ObservableProperty]
    private long _durationMs;

    [ObservableProperty]
    private string _timeDisplay = "--:--:--.--- / --:--:--.---";

    [ObservableProperty]
    private bool _isSeeking;

    [ObservableProperty]
    private double _trimInSeconds;

    [ObservableProperty]
    private double _trimOutSeconds;

    [ObservableProperty]
    private string _trimInDisplay = "00:00:00.000";

    [ObservableProperty]
    private string _trimOutDisplay = "00:00:00.000";

    [ObservableProperty]
    private int _cropX;

    [ObservableProperty]
    private int _cropY;

    [ObservableProperty]
    private int _cropW;

    [ObservableProperty]
    private int _cropH;

    [ObservableProperty]
    private int _sourceWidth;

    [ObservableProperty]
    private int _sourceHeight;

    [ObservableProperty]
    private string _statusText = "No media loaded";

    [ObservableProperty]
    private string _playPauseLabel = "\u25B6\uFE0E";

    [ObservableProperty]
    private bool _isAvailable;

    /// <summary>
    /// Raised when the MediaPlayer instance changes so the view can wire it to the VideoView.
    /// </summary>
    public event EventHandler<MediaPlayer?>? MediaPlayerChanged;

    public VideoPreviewViewModel(LibVLC? libVlc)
    {
        _libVlc = libVlc;
        IsAvailable = libVlc is not null;
        if (!IsAvailable)
            StatusText = "VLC not installed";
    }

    public void LoadMedia(SourceMediaInfo mediaInfo)
    {
        if (_libVlc is null) return;

        DisposePlayer();

        var primary = mediaInfo.PrimaryVideoStream;
        _frameRate = primary?.FrameRate ?? 30.0;
        SourceWidth = primary?.Width ?? 0;
        SourceHeight = primary?.Height ?? 0;
        CropX = 0;
        CropY = 0;
        CropW = SourceWidth;
        CropH = SourceHeight;

        _currentMedia = new Media(_libVlc, mediaInfo.SourcePath, FromType.FromPath);
        _mediaPlayer = new MediaPlayer(_currentMedia);

        _mediaPlayer.TimeChanged += OnTimeChanged;
        _mediaPlayer.LengthChanged += OnLengthChanged;
        _mediaPlayer.Playing += OnPlaying;
        _mediaPlayer.Paused += OnPaused;
        _mediaPlayer.Stopped += OnStopped;
        _mediaPlayer.EndReached += OnEndReached;
        _mediaPlayer.EncounteredError += OnEncounteredError;

        HasMedia = true;
        IsPlaying = false;
        Position = 0;
        TimeMs = 0;
        DurationMs = (long)((mediaInfo.DurationSeconds ?? 0) * 1000);
        TrimInSeconds = 0;
        TrimOutSeconds = mediaInfo.DurationSeconds ?? 0;
        TrimInDisplay = FormatTimePrecise(0);
        TrimOutDisplay = FormatTimePrecise(TrimOutSeconds);
        TimeDisplay = $"00:00:00.000 / {FormatTimePrecise(mediaInfo.DurationSeconds ?? 0)}";
        StatusText = System.IO.Path.GetFileName(mediaInfo.SourcePath);
        PlayPauseLabel = "\u25B6\uFE0E";

        MediaPlayerChanged?.Invoke(this, _mediaPlayer);

        // Start and immediately pause so VLC enters Paused state.
        // This enables seeking before the user clicks play and shows the first frame.
        _pauseOnFirstPlay = true;
        _mediaPlayer.Play();
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (_mediaPlayer is null) return;

        if (_mediaPlayer.IsPlaying)
            _mediaPlayer.Pause();
        else
            _mediaPlayer.Play();
    }

    [RelayCommand]
    private void StepForward()
    {
        if (_mediaPlayer is null) return;
        _mediaPlayer.NextFrame();
        var frameDurationMs = _frameRate > 0 ? (long)(1000.0 / _frameRate) : 33;
        var targetMs = Math.Min(DurationMs, TimeMs + frameDurationMs);
        TimeMs = targetMs;
        Position = DurationMs > 0 ? (double)targetMs / DurationMs : 0;
        TimeDisplay = $"{FormatTimePrecise(targetMs / 1000.0)} / {FormatTimePrecise(DurationMs / 1000.0)}";
    }

    [RelayCommand]
    private void StepBackward()
    {
        if (_mediaPlayer is null) return;
        var frameDurationMs = _frameRate > 0 ? (long)(1000.0 / _frameRate) : 33;
        var targetMs = Math.Max(0, _mediaPlayer.Time - frameDurationMs);
        _mediaPlayer.Time = targetMs;
        TimeMs = targetMs;
        Position = DurationMs > 0 ? (double)targetMs / DurationMs : 0;
        TimeDisplay = $"{FormatTimePrecise(targetMs / 1000.0)} / {FormatTimePrecise(DurationMs / 1000.0)}";
    }

    [RelayCommand]
    private void SetTrimIn()
    {
        TrimInSeconds = TimeMs / 1000.0;
        TrimInDisplay = FormatTimePrecise(TrimInSeconds);
    }

    [RelayCommand]
    private void SetTrimOut()
    {
        TrimOutSeconds = TimeMs / 1000.0;
        TrimOutDisplay = FormatTimePrecise(TrimOutSeconds);
    }

    [RelayCommand]
    private void ClearTrim()
    {
        TrimInSeconds = 0;
        TrimOutSeconds = DurationMs / 1000.0;
        TrimInDisplay = FormatTimePrecise(0);
        TrimOutDisplay = FormatTimePrecise(TrimOutSeconds);
    }

    [RelayCommand]
    private void ResetCrop()
    {
        CropX = 0;
        CropY = 0;
        CropW = SourceWidth;
        CropH = SourceHeight;
    }

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
    }

    public void BeginSeek()
    {
        IsSeeking = true;
    }

    public void EndSeek()
    {
        if (_mediaPlayer is not null && DurationMs > 0)
        {
            var targetMs = (long)(Position * DurationMs);
            _mediaPlayer.Time = targetMs;
            TimeMs = targetMs;
            TimeDisplay = $"{FormatTimePrecise(targetMs / 1000.0)} / {FormatTimePrecise(DurationMs / 1000.0)}";
        }
        IsSeeking = false;
    }

    private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (IsSeeking) return;
            TimeMs = e.Time;
            Position = DurationMs > 0 ? (double)e.Time / DurationMs : 0;
            TimeDisplay = $"{FormatTimePrecise(e.Time / 1000.0)} / {FormatTimePrecise(DurationMs / 1000.0)}";
        });
    }

    private void OnLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            DurationMs = e.Length;
            if (TrimOutSeconds == 0)
            {
                TrimOutSeconds = e.Length / 1000.0;
                TrimOutDisplay = FormatTimePrecise(TrimOutSeconds);
            }
            TimeDisplay = $"{FormatTimePrecise(TimeMs / 1000.0)} / {FormatTimePrecise(e.Length / 1000.0)}";
        });
    }

    private void OnPlaying(object? sender, EventArgs e)
    {
        if (_pauseOnFirstPlay)
        {
            _pauseOnFirstPlay = false;
            // Must schedule pause off the VLC event thread
            Dispatcher.UIThread.Post(() => _mediaPlayer?.Pause());
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            IsPlaying = true;
            PlayPauseLabel = "\u2759\u2759";
        });
    }

    private void OnPaused(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsPlaying = false;
            PlayPauseLabel = "\u25B6\uFE0E";
        });
    }

    private void OnStopped(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsPlaying = false;
            PlayPauseLabel = "\u25B6\uFE0E";
        });
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        // Must NOT call Stop/Play from this handler — VLC deadlock.
        Dispatcher.UIThread.Post(() =>
        {
            IsPlaying = false;
            Position = 0;
            TimeMs = 0;
            PlayPauseLabel = "\u25B6\uFE0E";
            TimeDisplay = $"00:00:00.000 / {FormatTimePrecise(DurationMs / 1000.0)}";
        });
    }

    private void OnEncounteredError(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsPlaying = false;
            PlayPauseLabel = "\u25B6\uFE0E";
            StatusText = "Playback error";
        });
    }

    private static string FormatTime(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"00:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private static string FormatTimePrecise(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds:D3}";
    }

    private void DisposePlayer()
    {
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.TimeChanged -= OnTimeChanged;
            _mediaPlayer.LengthChanged -= OnLengthChanged;
            _mediaPlayer.Playing -= OnPlaying;
            _mediaPlayer.Paused -= OnPaused;
            _mediaPlayer.Stopped -= OnStopped;
            _mediaPlayer.EndReached -= OnEndReached;
            _mediaPlayer.EncounteredError -= OnEncounteredError;

            MediaPlayerChanged?.Invoke(this, null);

            _mediaPlayer.Stop();
            _mediaPlayer.Dispose();
            _mediaPlayer = null;
        }

        if (_currentMedia is not null)
        {
            _currentMedia.Dispose();
            _currentMedia = null;
        }

        HasMedia = false;
        IsPlaying = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposePlayer();
    }
}
