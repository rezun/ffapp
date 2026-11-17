using Avalonia.Data.Converters;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFApp.Core.Models;
using FFApp.Core.Services;
using LibVLCSharp.Shared;

namespace FFApp.ViewModels;

public partial class VideoPreviewViewModel : ObservableObject, IDisposable
{
    public static readonly FuncValueConverter<bool, string> ExpandCollapseConverter =
        new(expanded => expanded ? "\u25BC Collapse" : "\u25B6 Expand");

    private readonly LibVLC? _libVlc;
    private readonly ICropDetectionService? _cropDetectionService;
    private MediaPlayer? _mediaPlayer;
    private Media? _currentMedia;
    private SourceMediaInfo? _currentMediaInfo;
    private const long DefaultFrameDurationMs = 33; // ~30 fps fallback
    private double _frameRate = 30.0;
    private bool _disposed;
    // Priming: after loading, VLC plays muted until the video output shows a frame,
    // then pauses. Only touched on the UI thread.
    private static readonly long PrimingFallbackMs = 1000;
    private bool _isPriming;
    private bool _primingHasVideo;
    private bool _primingVoutReady;
    // Incremented whenever a player is disposed. VLC events capture it on the VLC
    // thread so callbacks queued by a previous player are dropped on the UI thread.
    // The event sender cannot be used for this: LibVLCSharp raises events with its
    // internal event manager as sender, not the MediaPlayer.
    private int _playerGeneration;
    private const int MaximumVolume = 100;
    private int _volume = MaximumVolume;
    private int _lastAudibleVolume = MaximumVolume;

    public int Volume
    {
        get => _volume;
        set
        {
            var volume = Math.Clamp(value, 0, MaximumVolume);
            if (!SetProperty(ref _volume, volume)) return;

            if (volume > 0)
                _lastAudibleVolume = volume;
            IsMuted = volume == 0;
            ApplyAudioSettings();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MuteLabel))]
    private bool _isMuted;

    public string MuteLabel => IsMuted ? "Unmute" : "Mute";

    partial void OnIsMutedChanged(bool value) => ApplyAudioSettings();

    private void ApplyAudioSettings()
    {
        if (_mediaPlayer is null) return;
        _mediaPlayer.Volume = Volume;
        _mediaPlayer.Mute = IsMuted || _isPriming;
    }

    [RelayCommand]
    private void ToggleMute()
    {
        if (IsMuted && Volume == 0)
            Volume = _lastAudibleVolume;
        else
            IsMuted = !IsMuted;
    }

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCropSuggestion))]
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
    private bool _isDetectingCrop;

    [ObservableProperty]
    private string _cropDetectionStatus = "";

    /// <summary>Black-bar crop suggested for the current source by sampling the whole video.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCropSuggestion), nameof(CropSuggestionText))]
    private CropRect? _suggestedCrop;

    [ObservableProperty]
    private bool _isAnalyzingCrop;

    /// <summary>True while a suggestion exists and the user has not set a crop yet.</summary>
    public bool HasCropSuggestion => HasMedia && SuggestedCrop is not null && !IsCropDirty;

    public string CropSuggestionText => SuggestedCrop is { } crop
        ? $"Black bars: crop to {crop.Width} x {crop.Height}"
        : "";

    /// <summary>Explicitly set when the user modifies trim values.</summary>
    [ObservableProperty]
    private bool _isTrimDirty;

    /// <summary>Explicitly set when the user modifies crop values.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCropSuggestion))]
    private bool _isCropDirty;

    /// <summary>Guard to suppress dirty-marking during programmatic updates.</summary>
    private bool _suppressDirtyTracking;

    partial void OnCropXChanged(int value) { if (!_suppressDirtyTracking) IsCropDirty = true; }
    partial void OnCropYChanged(int value) { if (!_suppressDirtyTracking) IsCropDirty = true; }
    partial void OnCropWChanged(int value) { if (!_suppressDirtyTracking) IsCropDirty = true; }
    partial void OnCropHChanged(int value) { if (!_suppressDirtyTracking) IsCropDirty = true; }
    partial void OnHasMediaChanged(bool value) => DetectCropCommand.NotifyCanExecuteChanged();
    partial void OnIsDetectingCropChanged(bool value) => DetectCropCommand.NotifyCanExecuteChanged();

    [ObservableProperty]
    private string _statusText = "No media loaded";

    [ObservableProperty]
    private string _playPauseLabel = "\u25B6\uFE0E";

    [ObservableProperty]
    private bool _isAvailable;

    /// <summary>When true, the current trim values apply to all source files.</summary>
    [ObservableProperty]
    private bool _applyTrimToAll;

    /// <summary>When true, the current crop values apply to all source files.</summary>
    [ObservableProperty]
    private bool _applyCropToAll;

    /// <summary>
    /// Raised when the MediaPlayer instance changes so the view can wire it to the VideoView.
    /// </summary>
    public event EventHandler<MediaPlayer?>? MediaPlayerChanged;

    public VideoPreviewViewModel(
        LibVLC? libVlc,
        ICropDetectionService? cropDetectionService = null)
    {
        _libVlc = libVlc;
        _cropDetectionService = cropDetectionService;
        IsAvailable = libVlc is not null;
        if (!IsAvailable)
            StatusText = "VLC unavailable";
    }

    public void LoadMedia(SourceMediaInfo mediaInfo)
    {
        _currentMediaInfo = mediaInfo;
        CropDetectionStatus = "";
        DetectCropCommand.NotifyCanExecuteChanged();

        if (_libVlc is null) return;

        DisposePlayer();

        _suppressDirtyTracking = true;
        var primary = mediaInfo.PrimaryVideoStream;
        _frameRate = primary?.FrameRate ?? 30.0;
        SourceWidth = primary?.Width ?? 0;
        SourceHeight = primary?.Height ?? 0;
        CropX = 0;
        CropY = 0;
        CropW = SourceWidth;
        CropH = SourceHeight;
        _suppressDirtyTracking = false;

        _currentMedia = new Media(_libVlc, mediaInfo.SourcePath, FromType.FromPath);
        _mediaPlayer = new MediaPlayer(_currentMedia);
        _isPriming = true;
        _primingHasVideo = primary is not null;
        _primingVoutReady = false;
        ApplyAudioSettings();

        _mediaPlayer.TimeChanged += OnTimeChanged;
        _mediaPlayer.LengthChanged += OnLengthChanged;
        _mediaPlayer.Playing += OnPlaying;
        _mediaPlayer.Paused += OnPaused;
        _mediaPlayer.Stopped += OnStopped;
        _mediaPlayer.EndReached += OnEndReached;
        _mediaPlayer.EncounteredError += OnEncounteredError;
        _mediaPlayer.Vout += OnVout;

        HasMedia = true;
        IsPlaying = false;
        Position = 0;
        TimeMs = 0;
        DurationMs = (long)((mediaInfo.DurationSeconds ?? 0) * 1000);
        TrimInSeconds = 0;
        TrimOutSeconds = mediaInfo.DurationSeconds ?? 0;
        TrimInDisplay = FormatTimePrecise(0);
        TrimOutDisplay = FormatTimePrecise(TrimOutSeconds);
        IsTrimDirty = false;
        IsCropDirty = false;
        TimeDisplay = $"00:00:00.000 / {FormatTimePrecise(mediaInfo.DurationSeconds ?? 0)}";
        StatusText = System.IO.Path.GetFileName(mediaInfo.SourcePath);
        PlayPauseLabel = "\u25B6\uFE0E";

        MediaPlayerChanged?.Invoke(this, _mediaPlayer);

        // Play muted until the video output has shown a frame, then pause (see
        // TryFinishPriming). Pausing on the Playing event is too early for slow
        // decoders such as HEVC: VLC has no video output yet, so nothing is shown
        // and seeking stays black until the user presses play.
        _mediaPlayer.Play();
    }

    /// <summary>
    /// Saves the current trim/crop values to the given source file ViewModel.
    /// Called before switching away from a source file.
    /// </summary>
    public void SaveTrimCropToSource(SourceFileViewModel source)
    {
        source.TrimInSeconds = TrimInSeconds;
        source.TrimOutSeconds = TrimOutSeconds;
        source.IsTrimDirty = IsTrimDirty;
        source.CropX = CropX;
        source.CropY = CropY;
        source.CropW = CropW;
        source.CropH = CropH;
        source.IsCropDirty = IsCropDirty;
    }

    /// <summary>
    /// Restores trim/crop values from the given source file ViewModel.
    /// Called after switching to a new source file, respecting "Apply to All" flags.
    /// </summary>
    public void RestoreTrimCropFromSource(SourceFileViewModel source)
    {
        _suppressDirtyTracking = true;

        if (!ApplyTrimToAll)
        {
            TrimInSeconds = source.TrimInSeconds;
            TrimOutSeconds = source.TrimOutSeconds;
            TrimInDisplay = FormatTimePrecise(TrimInSeconds);
            TrimOutDisplay = FormatTimePrecise(TrimOutSeconds);
            IsTrimDirty = source.IsTrimDirty;
        }

        if (!ApplyCropToAll)
        {
            CropX = source.CropX;
            CropY = source.CropY;
            CropW = source.CropW;
            CropH = source.CropH;
            IsCropDirty = source.IsCropDirty;
        }

        _suppressDirtyTracking = false;
    }

    /// <summary>
    /// Gets the effective trim/crop for a source file, considering "Apply to All" flags.
    /// Used when creating jobs for all source files.
    /// </summary>
    public (double? TrimIn, double? TrimOut, CropRect? Crop) GetEffectiveTrimCrop(SourceFileViewModel source)
    {
        double trimIn, trimOut;
        int cropX, cropY, cropW, cropH;

        if (ApplyTrimToAll)
        {
            trimIn = TrimInSeconds;
            trimOut = TrimOutSeconds;
        }
        else
        {
            trimIn = source.TrimInSeconds;
            trimOut = source.TrimOutSeconds;
        }

        if (ApplyCropToAll)
        {
            cropX = CropX;
            cropY = CropY;
            cropW = CropW;
            cropH = CropH;
        }
        else
        {
            cropX = source.CropX;
            cropY = source.CropY;
            cropW = source.CropW;
            cropH = source.CropH;
        }

        double? effectiveTrimIn = trimIn > 0 ? trimIn : null;
        double? effectiveTrimOut = null;
        var duration = source.MediaInfo?.DurationSeconds ?? 0;
        if (trimOut > 0 && trimOut < duration)
            effectiveTrimOut = trimOut;

        var crop = new CropRect(cropX, cropY, cropW, cropH);
        return (effectiveTrimIn, effectiveTrimOut, crop);
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (_mediaPlayer is null) return;

        if (_isPriming)
        {
            // The player is already running muted; keep it playing for the user.
            _isPriming = false;
            ApplyAudioSettings();
            IsPlaying = true;
            PlayPauseLabel = "\u2759\u2759";
            return;
        }

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
        var frameDurationMs = _frameRate > 0 ? (long)(1000.0 / _frameRate) : DefaultFrameDurationMs;
        var targetMs = Math.Min(DurationMs, TimeMs + frameDurationMs);
        TimeMs = targetMs;
        Position = DurationMs > 0 ? (double)targetMs / DurationMs : 0;
        TimeDisplay = $"{FormatTimePrecise(targetMs / 1000.0)} / {FormatTimePrecise(DurationMs / 1000.0)}";
    }

    [RelayCommand]
    private void StepBackward()
    {
        if (_mediaPlayer is null) return;
        var frameDurationMs = _frameRate > 0 ? (long)(1000.0 / _frameRate) : DefaultFrameDurationMs;
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
        IsTrimDirty = true;
    }

    [RelayCommand]
    private void SetTrimOut()
    {
        TrimOutSeconds = TimeMs / 1000.0;
        TrimOutDisplay = FormatTimePrecise(TrimOutSeconds);
        IsTrimDirty = true;
    }

    [RelayCommand]
    private void ClearTrim()
    {
        TrimInSeconds = 0;
        TrimOutSeconds = DurationMs / 1000.0;
        TrimInDisplay = FormatTimePrecise(0);
        TrimOutDisplay = FormatTimePrecise(TrimOutSeconds);
        IsTrimDirty = false;
    }

    [RelayCommand]
    private void ResetCrop()
    {
        _suppressDirtyTracking = true;
        CropX = 0;
        CropY = 0;
        CropW = SourceWidth;
        CropH = SourceHeight;
        _suppressDirtyTracking = false;
        IsCropDirty = false;
        CropDetectionStatus = "";
    }

    [RelayCommand]
    private void ApplySuggestedCrop()
    {
        if (SuggestedCrop is not { } crop)
            return;

        CropX = crop.X;
        CropY = crop.Y;
        CropW = crop.Width;
        CropH = crop.Height;
        CropDetectionStatus = $"Applied suggested crop {crop.Width} x {crop.Height} at {crop.X}, {crop.Y}";
    }

    private bool CanDetectCrop() =>
        HasMedia && !IsDetectingCrop && _currentMediaInfo is not null && _cropDetectionService is not null;

    [RelayCommand(CanExecute = nameof(CanDetectCrop))]
    private async Task DetectCropAsync(CancellationToken ct)
    {
        if (_currentMediaInfo is not { } mediaInfo || _cropDetectionService is null)
            return;

        var position = TimeSpan.FromMilliseconds(TimeMs);
        IsDetectingCrop = true;
        CropDetectionStatus = "Detecting crop...";

        try
        {
            var crop = await _cropDetectionService.DetectAsync(mediaInfo, position, ct);
            if (!ReferenceEquals(mediaInfo, _currentMediaInfo))
                return;

            CropX = crop.X;
            CropY = crop.Y;
            CropW = crop.Width;
            CropH = crop.Height;
            CropDetectionStatus = $"Detected {crop.Width} x {crop.Height} at {crop.X}, {crop.Y}";
        }
        catch (CropDetectionException ex)
        {
            if (ReferenceEquals(mediaInfo, _currentMediaInfo))
                CropDetectionStatus = ex.Message;
        }
        finally
        {
            IsDetectingCrop = false;
        }
    }

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
    }

    partial void OnPositionChanged(double value)
    {
        if (IsSeeking && DurationMs > 0)
        {
            var targetMs = (long)(value * DurationMs);
            TimeMs = targetMs;
            TimeDisplay = $"{FormatTimePrecise(targetMs / 1000.0)} / {FormatTimePrecise(DurationMs / 1000.0)}";
        }
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
        var generation = Volatile.Read(ref _playerGeneration);
        Dispatcher.UIThread.Post(() =>
        {
            // Events already queued from a previous player must not affect this one.
            if (generation != _playerGeneration) return;
            if (_isPriming)
            {
                TryFinishPriming(e.Time);
                return;
            }
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

    private void OnVout(object? sender, MediaPlayerVoutEventArgs e)
    {
        if (e.Count <= 0) return;
        var generation = Volatile.Read(ref _playerGeneration);
        Dispatcher.UIThread.Post(() =>
        {
            if (generation == _playerGeneration)
                _primingVoutReady = true;
        });
    }

    /// <summary>
    /// Ends priming once playback time advances after the video output exists, so a
    /// frame is on screen. Media without video, or a video output that never reports,
    /// falls back to pausing after a short muted pre-roll. Seeks back to the position
    /// the UI shows, which is the start unless the user moved the slider meanwhile.
    /// </summary>
    private void TryFinishPriming(long timeMs)
    {
        var frameShown = !_primingHasVideo || (_primingVoutReady && timeMs > 0);
        if (!frameShown && timeMs < PrimingFallbackMs) return;
        if (_mediaPlayer is null) return;

        _isPriming = false;
        _mediaPlayer.SetPause(true);
        _mediaPlayer.Time = TimeMs;
        ApplyAudioSettings();
    }

    private void EndPrimingWithoutPause()
    {
        if (!_isPriming) return;
        _isPriming = false;
        ApplyAudioSettings();
    }

    private void OnPlaying(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // VLC may recreate its audio output when starting or replaying media.
            ApplyAudioSettings();
            if (_isPriming) return;

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
            EndPrimingWithoutPause();
            IsPlaying = false;
            PlayPauseLabel = "\u25B6\uFE0E";
        });
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        // Must NOT call Stop/Play from this handler — VLC deadlock.
        Dispatcher.UIThread.Post(() =>
        {
            EndPrimingWithoutPause();
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
            EndPrimingWithoutPause();
            IsPlaying = false;
            PlayPauseLabel = "\u25B6\uFE0E";
            StatusText = "Playback error";
        });
    }

    private static bool TryParseTimecode(string text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // Try HH:MM:SS.mmm or HH:MM:SS
        var parts = text.Split(':');
        if (parts.Length == 3)
        {
            if (int.TryParse(parts[0], out var h) &&
                int.TryParse(parts[1], out var m))
            {
                if (double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var s))
                {
                    seconds = h * 3600 + m * 60 + s;
                    return seconds >= 0;
                }
            }
        }

        return false;
    }

    public void ApplyTrimInText(string text)
    {
        if (TryParseTimecode(text, out var seconds))
        {
            var maxSeconds = DurationMs / 1000.0;
            seconds = Math.Clamp(seconds, 0, maxSeconds);
            TrimInSeconds = seconds;
            TrimInDisplay = FormatTimePrecise(seconds);
            IsTrimDirty = true;
        }
        else
        {
            // Revert to current value
            TrimInDisplay = FormatTimePrecise(TrimInSeconds);
        }
    }

    public void ApplyTrimOutText(string text)
    {
        if (TryParseTimecode(text, out var seconds))
        {
            var maxSeconds = DurationMs / 1000.0;
            seconds = Math.Clamp(seconds, 0, maxSeconds);
            TrimOutSeconds = seconds;
            TrimOutDisplay = FormatTimePrecise(seconds);
            IsTrimDirty = true;
        }
        else
        {
            // Revert to current value
            TrimOutDisplay = FormatTimePrecise(TrimOutSeconds);
        }
    }

    /// <summary>
    /// Sets trim values and updates their display strings.
    /// </summary>
    public void SetTrimValues(double trimIn, double trimOut)
    {
        TrimInSeconds = trimIn;
        TrimOutSeconds = trimOut;
        TrimInDisplay = FormatTimePrecise(trimIn);
        TrimOutDisplay = FormatTimePrecise(trimOut);
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
            _mediaPlayer.Vout -= OnVout;

            MediaPlayerChanged?.Invoke(this, null);

            _mediaPlayer.Stop();
            _mediaPlayer.Dispose();
            _mediaPlayer = null;
            Interlocked.Increment(ref _playerGeneration);
        }

        if (_currentMedia is not null)
        {
            _currentMedia.Dispose();
            _currentMedia = null;
        }

        _isPriming = false;
        HasMedia = false;
        IsPlaying = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposePlayer();
        GC.SuppressFinalize(this);
    }
}
