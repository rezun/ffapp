using System.Collections.Immutable;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Core.Validation;

namespace FFApp.ViewModels;

public partial class OutputSettingsViewModel : ObservableObject
{
    private readonly IValidationService _validationService;
    private readonly IMessenger _messenger;
    private readonly IFfmpegCapabilityService _capabilityService;
    private readonly ExecutablePathsConfig _pathsConfig;
    private bool _isSuppressing;
    private SourceMediaInfo? _mediaInfo;
    private string? _resolvedOutputPath;
    private double? _trimmedDurationSeconds;

    public bool IsVideoModeEditable { get; init; } = true;

    public FormatViewModel Format { get; } = new();
    public AudioViewModel Audio { get; } = new();
    public EncodingStrategyViewModel Strategy { get; } = new();
    public StreamHandlingViewModel StreamHandling { get; } = new();
    public HdrViewModel Hdr { get; } = new();
    public ColorMetadataViewModel Color { get; } = new();

    [ObservableProperty]
    private ResolutionPreset _selectedResolution = ResolutionPreset.Original;

    [ObservableProperty]
    private FrameRatePreset _selectedFrameRate = FrameRatePreset.Original;

    [ObservableProperty]
    private string _keyframeIntervalText = "";

    [ObservableProperty]
    private KeyframeUnit _selectedKeyframeUnit = KeyframeUnit.Frames;

    [ObservableProperty]
    private IReadOnlyList<KeyframeUnit> _availableKeyframeUnits = [KeyframeUnit.Frames];

    [ObservableProperty]
    private bool _recalculateMatroskaStatistics;

    public bool IsMatroskaPostProcessingVisible =>
        Format.SelectedContainer is OutputContainer.Mkv
            or OutputContainer.Mka
            or OutputContainer.Webm;

    public bool IsMatroskaPostProcessingAvailable =>
        IsMatroskaPostProcessingVisible && _pathsConfig.MkvpropeditPath is not null;

    public IReadOnlyList<ResolutionPreset> AvailableResolutions { get; } =
        Enum.GetValues<ResolutionPreset>();

    public IReadOnlyList<FrameRatePreset> AvailableFrameRates { get; } =
        Enum.GetValues<FrameRatePreset>();

    [ObservableProperty]
    private string _validationText = "";

    [ObservableProperty]
    private bool _hasValidationIssues;

    [ObservableProperty]
    private bool _hasValidationErrors;

    partial void OnSelectedResolutionChanged(ResolutionPreset value)
    {
        if (!_isSuppressing)
            RebuildAndNotify();
    }

    partial void OnSelectedFrameRateChanged(FrameRatePreset value)
    {
        if (!_isSuppressing)
            RebuildAndNotify();
    }

    partial void OnKeyframeIntervalTextChanged(string value)
    {
        if (!_isSuppressing)
            RebuildAndNotify();
    }

    partial void OnSelectedKeyframeUnitChanged(KeyframeUnit value)
    {
        if (!_isSuppressing)
            RebuildAndNotify();
    }

    partial void OnRecalculateMatroskaStatisticsChanged(bool value)
    {
        if (!_isSuppressing)
            RebuildAndNotify();
    }

    public int? KeyframeInterval =>
        int.TryParse(KeyframeIntervalText, out var v) && v > 0 ? v : null;

    public OutputSettingsViewModel(
        IValidationService validationService,
        IMessenger messenger,
        IFfmpegCapabilityService capabilityService,
        ExecutablePathsConfig pathsConfig)
    {
        _validationService = validationService;
        _messenger = messenger;
        _capabilityService = capabilityService;
        _pathsConfig = pathsConfig;

        Hdr.IsLibplaceboAvailable = capabilityService.IsLibplaceboAvailable;

        Format.PropertyChanged += OnChildPropertyChanged;
        Audio.PropertyChanged += OnChildPropertyChanged;
        Strategy.PropertyChanged += OnChildPropertyChanged;
        Strategy.FileSize.PropertyChanged += OnChildPropertyChanged;
        Strategy.Bitrate.PropertyChanged += OnChildPropertyChanged;
        Strategy.Quality.PropertyChanged += OnChildPropertyChanged;
        StreamHandling.PropertyChanged += OnChildPropertyChanged;
        Hdr.PropertyChanged += OnChildPropertyChanged;
        Color.PropertyChanged += OnChildPropertyChanged;

        Format.PropertyChanged += OnFormatPropertyChanged;

        Audio.AvailableAudioCodecs = Format.AvailableAudioCodecs;
    }

    public void UpdateResolvedOutputPath(string? resolvedOutputPath)
    {
        _resolvedOutputPath = resolvedOutputPath;
        RefreshValidation();
    }

    public void UpdateEffectiveDuration(double? effectiveDurationSeconds)
    {
        _trimmedDurationSeconds = effectiveDurationSeconds;
        RefreshValidation();
        var settings = BuildSettings();
        UpdateFileSizeBitrateDisplay(settings);
        UpdateBitrateFileSizeDisplay(settings);
    }

    public void UpdateSourceInfo(
        SourceMediaInfo mediaInfo, ImmutableArray<int>? selectedAudioStreamIndices = null)
    {
        _mediaInfo = mediaInfo;
        Audio.UpdateTracks(mediaInfo, selectedAudioStreamIndices);
        Format.SourceHasVideo = mediaInfo.VideoStreams.Length > 0;
        Audio.AvailableAudioCodecs = Format.AvailableAudioCodecs;

        var hdrFormat = mediaInfo.PrimaryVideoStream?.HdrFormat ?? HdrFormat.Sdr;
        Hdr.UpdateForSource(hdrFormat);
        Hdr.UpdateForCodec(Format.SelectedVideoCodec);
        Hdr.IsHdrModeVisible = Format.IsVideoReencode && Hdr.IsSourceHdr;

        RebuildAndNotify();
    }

    public void ApplySettings(OutputSettings settings)
    {
        _isSuppressing = true;
        try
        {
            Format.SelectedVideoMode = settings.VideoMode;
            Format.SelectedContainer = settings.Container;
            Audio.AvailableAudioCodecs = Format.AvailableAudioCodecs;
            Format.SelectedVideoCodec = settings.VideoCodec;
            Strategy.SelectedStrategy = settings.EncodingStrategy;
            SelectedResolution = settings.ResolutionPreset;
            SelectedFrameRate = settings.FrameRatePreset;
            KeyframeIntervalText = settings.KeyframeInterval?.ToString() ?? "";
            UpdateKeyframeUnitsForCodec(settings.VideoCodec);
            SelectedKeyframeUnit = settings.KeyframeUnit;

            // Audio
            Audio.SelectedAudioMode = settings.AudioOptions.Mode;
            Audio.SelectedAudioCodec = settings.AudioOptions.Codec;
            Audio.AudioBitrate = settings.AudioOptions.Bitrate ?? AudioViewModel.DefaultBitrateBps;
            Audio.SelectedAudioChannels = settings.AudioOptions.AudioChannels ?? 0;
            Audio.SelectedSampleRate = settings.AudioOptions.SampleRate ?? 0;
            Audio.RemoveTrackTitles = settings.AudioOptions.RemoveTrackTitles;

            // Stream handling
            StreamHandling.RemoveSubtitles = settings.StreamHandling.RemoveSubtitles;
            StreamHandling.RemoveData = settings.StreamHandling.RemoveData;
            StreamHandling.RemoveImages = settings.StreamHandling.RemoveImages;
            RecalculateMatroskaStatistics =
                settings.PostProcessing.RecalculateMatroskaStatistics;

            // File size
            Strategy.FileSize.TargetSizeMb = settings.TargetFileSize.TargetSizeMb;
            Strategy.FileSize.TwoPass = settings.TargetFileSize.TwoPass;
            Strategy.FileSize.MuxingOverheadRatio = settings.TargetFileSize.MuxingOverheadRatio;
            Strategy.FileSize.AbsoluteOverheadBytes = settings.TargetFileSize.AbsoluteOverheadBytes;

            // Bitrate
            Strategy.Bitrate.VideoBitrateKbps = settings.TargetBitrate.VideoBitrate / 1000;
            Strategy.Bitrate.TwoPass = settings.TargetBitrate.TwoPass;

            // Update dependent state before applying values that depend on it
            Strategy.Quality.UpdateForCodec(settings.VideoCodec);
            Hdr.UpdateForCodec(settings.VideoCodec);
            Hdr.IsHdrModeVisible = Format.IsVideoReencode && Hdr.IsSourceHdr;
            Audio.IsRemoveAllowed = settings.VideoMode != VideoMode.Remove;

            // Quality - apply codec-specific settings (after UpdateForCodec sets defaults)
            ApplyQualitySettings(settings.TargetQuality, settings.VideoCodec);

            // Encoder options
            ApplyEncoderOptions(settings.VideoEncoderOptions);

            Color.ApplySettings(settings.ColorMetadata);

            // HDR
            Hdr.SelectedHdrMode = settings.HdrMode;
            Hdr.SelectedEngine = settings.ToneMapSettings.Engine;
            Hdr.SelectedAlgorithm = settings.ToneMapSettings.Algorithm;
            Hdr.Desaturation = settings.ToneMapSettings.Desaturation;
            Hdr.PeakNitsText = settings.ToneMapSettings.PeakNits?.ToString() ?? "";
            Hdr.SelectedLibplaceboAlgorithm = settings.ToneMapSettings.LibplaceboAlgorithm;
            Hdr.SelectedGamutMode = settings.ToneMapSettings.GamutMode;
            Hdr.Saturation = settings.ToneMapSettings.Saturation;
            Hdr.ContrastRecovery = settings.ToneMapSettings.ContrastRecovery;
            Hdr.PeakDetect = settings.ToneMapSettings.PeakDetect;
            Hdr.TonemappingParamText = settings.ToneMapSettings.TonemappingParam?.ToString() ?? "";
        }
        finally
        {
            _isSuppressing = false;
        }

        RebuildAndNotify();
    }

    public OutputSettings BuildSettings()
    {
        var videoMode = Format.SelectedVideoMode;
        var codec = Format.SelectedVideoCodec;
        var quality = Strategy.Quality;
        var audioMode = Audio.SelectedAudioMode;

        return new OutputSettings(
            Container: Format.SelectedContainer,
            VideoMode: videoMode,
            VideoCodec: codec,
            EncodingStrategy: Strategy.SelectedStrategy,
            ResolutionPreset: SelectedResolution,
            FrameRatePreset: SelectedFrameRate,
            VideoEncoderOptions: videoMode == VideoMode.Reencode
                ? BuildVideoEncoderOptions(codec, quality) : null,
            AudioOptions: new AudioOptions(
                Mode: audioMode,
                Codec: audioMode == AudioMode.Reencode ? Audio.SelectedAudioCodec : null,
                Bitrate: audioMode == AudioMode.Reencode ? Audio.AudioBitrate : null,
                AudioChannels: audioMode == AudioMode.Reencode && Audio.SelectedAudioChannels != 0
                    ? Audio.SelectedAudioChannels
                    : null,
                SampleRate: audioMode == AudioMode.Reencode && Audio.SelectedSampleRate != 0
                    ? Audio.SelectedSampleRate
                    : null,
                RemoveTrackTitles: Audio.RemoveTrackTitles),
            StreamHandling: new StreamHandlingOptions(
                RemoveSubtitles: StreamHandling.RemoveSubtitles,
                RemoveData: StreamHandling.RemoveData,
                RemoveImages: StreamHandling.RemoveImages),
            TargetFileSize: new TargetFileSizeOptions(
                TargetSizeMb: (int)Strategy.FileSize.TargetSizeMb,
                TwoPass: Strategy.FileSize.TwoPass,
                MuxingOverheadRatio: Strategy.FileSize.MuxingOverheadRatio,
                AbsoluteOverheadBytes: Strategy.FileSize.AbsoluteOverheadBytes),
            TargetBitrate: new TargetBitrateOptions(
                VideoBitrate: (int)Strategy.Bitrate.VideoBitrateKbps * 1000,
                TwoPass: Strategy.Bitrate.TwoPass),
            TargetQuality: videoMode == VideoMode.Reencode
                ? BuildQualityOptions(codec, quality) : H26xQualityOptions.ForCodec(VideoCodec.H264),
            ColorMetadata: Color.BuildSettings(),
            HdrMode: Hdr.SelectedHdrMode,
            ToneMapSettings: new ToneMapSettings(
                Engine: Hdr.SelectedEngine,
                Algorithm: Hdr.SelectedAlgorithm,
                Desaturation: Hdr.Desaturation,
                PeakNits: Hdr.PeakNits,
                LibplaceboAlgorithm: Hdr.SelectedLibplaceboAlgorithm,
                GamutMode: Hdr.SelectedGamutMode,
                Saturation: Hdr.Saturation,
                ContrastRecovery: Hdr.ContrastRecovery,
                PeakDetect: Hdr.PeakDetect,
                TonemappingParam: Hdr.TonemappingParam),
            KeyframeInterval: KeyframeInterval,
            KeyframeUnit: SelectedKeyframeUnit,
            PostProcessing: new PostProcessingOptions(
                RecalculateMatroskaStatistics:
                    IsMatroskaPostProcessingAvailable
                    && RecalculateMatroskaStatistics));
    }

    private void OnChildPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isSuppressing)
            return;
        if (sender == Color && e.PropertyName is not (nameof(ColorMetadataViewModel.SelectedMode)
            or nameof(ColorMetadataViewModel.SelectedPrimaries) or nameof(ColorMetadataViewModel.SelectedTransfer)
            or nameof(ColorMetadataViewModel.SelectedMatrix) or nameof(ColorMetadataViewModel.SelectedRange)))
            return;

        RebuildAndNotify();
    }

    private void OnFormatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isSuppressing)
            return;

        if (e.PropertyName == nameof(FormatViewModel.SelectedVideoMode))
        {
            Audio.IsRemoveAllowed = Format.SelectedVideoMode != VideoMode.Remove;
            Hdr.IsHdrModeVisible = Format.IsVideoReencode && Hdr.IsSourceHdr;
        }

        if (e.PropertyName == nameof(FormatViewModel.SelectedVideoCodec))
        {
            Strategy.Quality.UpdateForCodec(Format.SelectedVideoCodec);
            Hdr.UpdateForCodec(Format.SelectedVideoCodec);
            UpdateKeyframeUnitsForCodec(Format.SelectedVideoCodec);
        }

        if (e.PropertyName is nameof(FormatViewModel.AvailableAudioCodecs)
                             or nameof(FormatViewModel.SelectedContainer))
        {
            Audio.AvailableAudioCodecs = Format.AvailableAudioCodecs;
            if (Audio.SelectedAudioCodec is null || !Audio.AvailableAudioCodecs.Contains(Audio.SelectedAudioCodec.Value))
                Audio.SelectedAudioCodec = Audio.AvailableAudioCodecs.Count > 0 ? Audio.AvailableAudioCodecs[0] : null;
        }

        if (e.PropertyName == nameof(FormatViewModel.SelectedContainer))
        {
            OnPropertyChanged(nameof(IsMatroskaPostProcessingVisible));
            OnPropertyChanged(nameof(IsMatroskaPostProcessingAvailable));
        }
    }

    private void RebuildAndNotify()
    {
        Color.UpdateContext(_mediaInfo?.PrimaryVideoStream, Hdr.SelectedHdrMode, Format.IsVideoReencode);
        var settings = BuildSettings();
        RefreshValidation(settings);
        UpdateFileSizeBitrateDisplay(settings);
        UpdateBitrateFileSizeDisplay(settings);
        _messenger.Send(new OutputSettingsChangedMessage(settings));
    }

    private void RefreshValidation(OutputSettings? settings = null)
    {
        settings ??= BuildSettings();

        if (_mediaInfo is null)
            return;

        var result = _validationService.Validate(
            settings, AudioTrackSelection.Apply(_mediaInfo, Audio.SelectedAudioStreamIndices),
            _trimmedDurationSeconds);
        var allIssues = result.Issues;

        if (!string.IsNullOrEmpty(_resolvedOutputPath)
            && string.Equals(
                Path.GetFullPath(_resolvedOutputPath),
                Path.GetFullPath(_mediaInfo.SourcePath),
                StringComparison.OrdinalIgnoreCase))
        {
            allIssues = allIssues.Insert(0, new ValidationIssue(
                ValidationSeverity.Error, "OUTPUT_SAME_AS_SOURCE",
                "Output file is the same as the source file."));
        }

        HasValidationErrors = allIssues.Any(i => i.Severity == ValidationSeverity.Error);
        HasValidationIssues = allIssues.Length > 0;
        ValidationText = allIssues.Length > 0
            ? string.Join(Environment.NewLine, allIssues.Select(i => $"{i.Severity}: {i.Message}"))
             : "";
    }

    private void UpdateFileSizeBitrateDisplay(OutputSettings settings)
    {
        if (settings.VideoMode != VideoMode.Reencode
            || Strategy.SelectedStrategy != EncodingStrategy.TargetFileSize
            || _mediaInfo is null)
        {
            Strategy.FileSize.VideoBitrateText = null;
            return;
        }

        if (_mediaInfo.DurationSeconds is null or <= 0)
        {
            Strategy.FileSize.VideoBitrateText = null;
            return;
        }

        var options = settings.TargetFileSize;
        var totalBudgetBits = (long)options.TargetSizeMb * 1024 * 1024 * 8;
        var overheadBits = options.EffectiveOverheadBits(totalBudgetBits);
        var usableBudgetBits = totalBudgetBits - overheadBits;

        var effectiveDuration = _trimmedDurationSeconds ?? _mediaInfo.DurationSeconds.Value;
        var audioBits = CompatibilityData.EstimateAudioBits(
            settings.AudioOptions.Mode,
            settings.AudioOptions.Bitrate,
            AudioTrackSelection.Apply(_mediaInfo, Audio.SelectedAudioStreamIndices).AudioStreams,
            effectiveDuration);

        if (audioBits is null)
        {
            Strategy.FileSize.VideoBitrateText = null;
            return;
        }

        var videoBudgetBits = usableBudgetBits - audioBits.Value;

        if (videoBudgetBits <= 0)
        {
            Strategy.FileSize.VideoBitrateText = null;
            return;
        }

        var videoBitrate = (int)(videoBudgetBits / effectiveDuration);
        var mbps = videoBitrate / 1_000_000.0;
        Strategy.FileSize.VideoBitrateText = $"~{mbps:F2} Mbps video bitrate";
    }

    private void UpdateBitrateFileSizeDisplay(OutputSettings settings)
    {
        if (settings.VideoMode != VideoMode.Reencode
            || Strategy.SelectedStrategy != EncodingStrategy.TargetBitrate
            || _mediaInfo is null)
        {
            Strategy.Bitrate.EstimatedFileSizeText = null;
            return;
        }

        if (_mediaInfo.DurationSeconds is null or <= 0)
        {
            Strategy.Bitrate.EstimatedFileSizeText = null;
            return;
        }

        var effectiveDuration = _trimmedDurationSeconds ?? _mediaInfo.DurationSeconds.Value;
        var videoBits = (long)settings.TargetBitrate.VideoBitrate * (long)effectiveDuration;

        var audioBits = CompatibilityData.EstimateAudioBits(
            settings.AudioOptions.Mode,
            settings.AudioOptions.Bitrate,
            AudioTrackSelection.Apply(_mediaInfo, Audio.SelectedAudioStreamIndices).AudioStreams,
            effectiveDuration);

        if (audioBits is null)
        {
            Strategy.Bitrate.EstimatedFileSizeText = null;
            return;
        }

        var totalBits = videoBits + audioBits.Value;
        var totalBytes = totalBits / 8.0;
        var totalMb = totalBytes / (1024.0 * 1024.0);

        Strategy.Bitrate.EstimatedFileSizeText = totalMb >= 1024
            ? $"~{totalMb / 1024.0:F2} GiB estimated"
            : $"~{totalMb:F0} MiB estimated";
    }

    private static VideoEncoderOptions BuildVideoEncoderOptions(VideoCodec codec, QualityViewModel quality) =>
        quality.ViewModelFor(codec).BuildEncoderOptions();

    private static QualityOptions BuildQualityOptions(VideoCodec codec, QualityViewModel quality) =>
        quality.ViewModelFor(codec).BuildQualityOptions();

    private void ApplyQualitySettings(QualityOptions? qualityOptions, VideoCodec codec)
    {
        Strategy.Quality.UpdateForCodec(codec);
        if (qualityOptions is not null)
            Strategy.Quality.ViewModelFor(codec).ApplyQuality(qualityOptions);
    }

    private void ApplyEncoderOptions(VideoEncoderOptions? encoderOptions)
    {
        if (encoderOptions is null)
            return;
        Strategy.Quality.ViewModelFor(encoderOptions.Codec).ApplyEncoder(encoderOptions);
    }

    private static readonly IReadOnlyList<KeyframeUnit> FramesOnly = [KeyframeUnit.Frames];
    private static readonly IReadOnlyList<KeyframeUnit> FramesAndSeconds = [KeyframeUnit.Frames, KeyframeUnit.Seconds];

    private void UpdateKeyframeUnitsForCodec(VideoCodec codec)
    {
        AvailableKeyframeUnits = codec == VideoCodec.Av1 ? FramesAndSeconds : FramesOnly;
        if (SelectedKeyframeUnit == KeyframeUnit.Seconds && codec != VideoCodec.Av1)
            SelectedKeyframeUnit = KeyframeUnit.Frames;
    }
}
