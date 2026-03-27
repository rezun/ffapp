using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core.Models;
using FFApp.Core.Services;

namespace FFApp.ViewModels;

public partial class OutputSettingsViewModel : ObservableObject
{
    private readonly IValidationService _validationService;
    private readonly IMessenger _messenger;
    private bool _isSuppressing;
    private SourceMediaInfo? _mediaInfo;

    public FormatViewModel Format { get; } = new();
    public AudioViewModel Audio { get; } = new();
    public EncodingStrategyViewModel Strategy { get; } = new();
    public StreamHandlingViewModel StreamHandling { get; } = new();

    [ObservableProperty]
    private ResolutionPreset _selectedResolution = ResolutionPreset.Original;

    [ObservableProperty]
    private FrameRatePreset _selectedFrameRate = FrameRatePreset.Original;

    public IReadOnlyList<ResolutionPreset> AvailableResolutions { get; } =
        Enum.GetValues<ResolutionPreset>();

    public IReadOnlyList<FrameRatePreset> AvailableFrameRates { get; } =
        Enum.GetValues<FrameRatePreset>();

    [ObservableProperty]
    private string _validationText = "";

    [ObservableProperty]
    private bool _hasValidationIssues;

    partial void OnValidationTextChanged(string value)
    {
        HasValidationIssues = !string.IsNullOrEmpty(value);
    }

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

    public OutputSettingsViewModel(IValidationService validationService, IMessenger messenger)
    {
        _validationService = validationService;
        _messenger = messenger;

        Format.PropertyChanged += OnChildPropertyChanged;
        Audio.PropertyChanged += OnChildPropertyChanged;
        Strategy.PropertyChanged += OnChildPropertyChanged;
        Strategy.FileSize.PropertyChanged += OnChildPropertyChanged;
        Strategy.Bitrate.PropertyChanged += OnChildPropertyChanged;
        Strategy.Quality.PropertyChanged += OnChildPropertyChanged;
        StreamHandling.PropertyChanged += OnChildPropertyChanged;

        Format.PropertyChanged += OnFormatPropertyChanged;

        Audio.AvailableAudioCodecs = Format.AvailableAudioCodecs;
    }

    public void UpdateSourceInfo(SourceMediaInfo mediaInfo)
    {
        _mediaInfo = mediaInfo;
        Audio.AvailableAudioCodecs = Format.AvailableAudioCodecs;
        RebuildAndNotify();
    }

    public void ApplySettings(OutputSettings settings)
    {
        _isSuppressing = true;
        try
        {
            Format.SelectedContainer = settings.Container;
            Format.SelectedVideoCodec = settings.VideoCodec;
            Strategy.SelectedStrategy = settings.EncodingStrategy;
            SelectedResolution = settings.ResolutionPreset;
            SelectedFrameRate = settings.FrameRatePreset;

            // Audio
            Audio.SelectedAudioMode = settings.AudioOptions.Mode;
            Audio.SelectedAudioCodec = settings.AudioOptions.Codec;
            Audio.AudioBitrate = settings.AudioOptions.Bitrate ?? 128000;
            Audio.RemixToStereo = settings.AudioOptions.RemixToStereo;

            // Stream handling
            StreamHandling.RemoveSubtitles = settings.StreamHandling.RemoveSubtitles;
            StreamHandling.RemoveData = settings.StreamHandling.RemoveData;

            // File size
            Strategy.FileSize.TargetSizeMb = settings.TargetFileSize.TargetSizeMb;
            Strategy.FileSize.TwoPass = settings.TargetFileSize.TwoPass;
            Strategy.FileSize.MuxingOverheadRatio = settings.TargetFileSize.MuxingOverheadRatio;

            // Bitrate
            Strategy.Bitrate.VideoBitrateKbps = settings.TargetBitrate.VideoBitrate / 1000;
            Strategy.Bitrate.TwoPass = settings.TargetBitrate.TwoPass;

            // Quality - apply codec-specific settings
            ApplyQualitySettings(settings.TargetQuality, settings.VideoCodec);

            // Encoder options
            ApplyEncoderOptions(settings.VideoEncoderOptions);
        }
        finally
        {
            _isSuppressing = false;
        }

        RebuildAndNotify();
    }

    public OutputSettings BuildSettings()
    {
        var codec = Format.SelectedVideoCodec;
        var quality = Strategy.Quality;

        return new OutputSettings(
            Container: Format.SelectedContainer,
            VideoCodec: codec,
            EncodingStrategy: Strategy.SelectedStrategy,
            ResolutionPreset: SelectedResolution,
            FrameRatePreset: SelectedFrameRate,
            VideoEncoderOptions: BuildVideoEncoderOptions(codec, quality),
            AudioOptions: new AudioOptions(
                Mode: Audio.SelectedAudioMode,
                Codec: Audio.SelectedAudioCodec,
                Bitrate: Audio.AudioBitrate,
                RemixToStereo: Audio.RemixToStereo),
            StreamHandling: new StreamHandlingOptions(
                RemoveSubtitles: StreamHandling.RemoveSubtitles,
                RemoveData: StreamHandling.RemoveData),
            TargetFileSize: new TargetFileSizeOptions(
                TargetSizeMb: (int)Strategy.FileSize.TargetSizeMb,
                TwoPass: Strategy.FileSize.TwoPass,
                MuxingOverheadRatio: Strategy.FileSize.MuxingOverheadRatio),
            TargetBitrate: new TargetBitrateOptions(
                VideoBitrate: (int)Strategy.Bitrate.VideoBitrateKbps * 1000,
                TwoPass: Strategy.Bitrate.TwoPass),
            TargetQuality: BuildQualityOptions(codec, quality));
    }

    private void OnChildPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isSuppressing)
            return;

        RebuildAndNotify();
    }

    private void OnFormatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isSuppressing)
            return;

        if (e.PropertyName == nameof(FormatViewModel.SelectedVideoCodec))
        {
            Strategy.Quality.UpdateForCodec(Format.SelectedVideoCodec);
        }

        if (e.PropertyName is nameof(FormatViewModel.AvailableAudioCodecs)
                             or nameof(FormatViewModel.SelectedContainer))
        {
            Audio.AvailableAudioCodecs = Format.AvailableAudioCodecs;
            if (Audio.SelectedAudioCodec is null || !Audio.AvailableAudioCodecs.Contains(Audio.SelectedAudioCodec.Value))
                Audio.SelectedAudioCodec = Audio.AvailableAudioCodecs.Count > 0 ? Audio.AvailableAudioCodecs[0] : null;
        }
    }

    private void RebuildAndNotify()
    {
        var settings = BuildSettings();

        if (_mediaInfo is not null)
        {
            var result = _validationService.Validate(settings, _mediaInfo);
            ValidationText = result.Issues.Length > 0
                ? string.Join(Environment.NewLine, result.Issues.Select(i => $"{i.Severity}: {i.Message}"))
                : "";
        }

        _messenger.Send(new OutputSettingsChangedMessage(settings));
    }

    private static VideoEncoderOptions BuildVideoEncoderOptions(VideoCodec codec, QualityViewModel quality) =>
        codec switch
        {
            VideoCodec.H264 => new H264EncoderOptions(
                Preset: quality.H26xPreset,
                Profile: quality.H26xProfile),
            VideoCodec.H265 => new H265EncoderOptions(
                Preset: quality.H26xPreset,
                Profile: quality.H26xProfile),
            VideoCodec.Vp8 or VideoCodec.Vp9 => new VpxEncoderOptions(
                Codec: codec,
                CpuUsed: quality.VpxCpuUsed,
                TileColumns: quality.VpxTileColumns,
                TileRows: quality.VpxTileRows),
            _ => new H264EncoderOptions(),
        };

    private static QualityOptions BuildQualityOptions(VideoCodec codec, QualityViewModel quality) =>
        codec switch
        {
            VideoCodec.H264 => new H264QualityOptions(
                Crf: quality.Crf,
                Preset: quality.H26xPreset,
                Profile: quality.H26xProfile),
            VideoCodec.H265 => new H265QualityOptions(
                Crf: quality.Crf,
                Preset: quality.H26xPreset,
                Profile: quality.H26xProfile),
            VideoCodec.Vp8 => new Vp8QualityOptions(
                Quality: quality.VpxQuality,
                TwoPass: quality.VpxTwoPass),
            VideoCodec.Vp9 => new Vp9QualityOptions(
                Quality: quality.VpxQuality,
                TwoPass: quality.VpxTwoPass),
            _ => new H264QualityOptions(),
        };

    private void ApplyQualitySettings(QualityOptions? qualityOptions, VideoCodec codec)
    {
        Strategy.Quality.UpdateForCodec(codec);

        switch (qualityOptions)
        {
            case H264QualityOptions h264:
                Strategy.Quality.Crf = h264.Crf;
                Strategy.Quality.H26xPreset = h264.Preset;
                Strategy.Quality.H26xProfile = h264.Profile;
                break;
            case H265QualityOptions h265:
                Strategy.Quality.Crf = h265.Crf;
                Strategy.Quality.H26xPreset = h265.Preset;
                Strategy.Quality.H26xProfile = h265.Profile;
                break;
            case Vp8QualityOptions vp8:
                Strategy.Quality.VpxQuality = vp8.Quality;
                Strategy.Quality.VpxTwoPass = vp8.TwoPass;
                break;
            case Vp9QualityOptions vp9:
                Strategy.Quality.VpxQuality = vp9.Quality;
                Strategy.Quality.VpxTwoPass = vp9.TwoPass;
                break;
        }
    }

    private void ApplyEncoderOptions(VideoEncoderOptions? encoderOptions)
    {
        switch (encoderOptions)
        {
            case H264EncoderOptions h264:
                Strategy.Quality.H26xPreset = h264.Preset;
                Strategy.Quality.H26xProfile = h264.Profile;
                break;
            case H265EncoderOptions h265:
                Strategy.Quality.H26xPreset = h265.Preset;
                Strategy.Quality.H26xProfile = h265.Profile;
                break;
            case VpxEncoderOptions vpx:
                Strategy.Quality.VpxCpuUsed = vpx.CpuUsed;
                Strategy.Quality.VpxTileColumns = vpx.TileColumns;
                Strategy.Quality.VpxTileRows = vpx.TileRows;
                break;
        }
    }
}
