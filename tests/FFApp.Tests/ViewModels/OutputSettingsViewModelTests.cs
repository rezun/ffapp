using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Tests.Builders;
using FFApp.ViewModels;
using FluentAssertions;
using NSubstitute;

namespace FFApp.Tests.ViewModels;

public class OutputSettingsViewModelTests
{
    [Theory]
    [InlineData(EncodingStrategy.TargetQuality)]
    [InlineData(EncodingStrategy.TargetBitrate)]
    [InlineData(EncodingStrategy.TargetFileSize)]
    public void ApplyAndBuildSettings_Av1VarianceBoost_PreservesSettingsAndEdits(EncodingStrategy strategy)
    {
        var settings = new TestOutputSettingsBuilder().WithAv1()
            .WithAv1VarianceBoost(strength: 4, octile: 8).WithStrategy(strategy).Build();
        _sut.ApplySettings(settings);
        var vm = _sut.Strategy.Quality.Av1;
        vm.EnableVarianceBoostChecked.Should().BeTrue();
        vm.VarianceBoostStrength.Should().Be(4);
        vm.VarianceOctile.Should().Be(8);

        vm.VarianceBoostStrength = 1;
        vm.VarianceOctile = 3;
        var rebuilt = _sut.BuildSettings();
        var encoder = rebuilt.VideoEncoderOptions.Should().BeOfType<Av1EncoderOptions>().Subject;
        var quality = rebuilt.TargetQuality.Should().BeOfType<Av1QualityOptions>().Subject;
        encoder.VarianceBoostStrength.Should().Be(1);
        encoder.VarianceOctile.Should().Be(3);
        quality.VarianceBoostStrength.Should().Be(1);
        quality.VarianceOctile.Should().Be(3);
    }

    [Theory]
    [InlineData(EncodingStrategy.TargetQuality)]
    [InlineData(EncodingStrategy.TargetBitrate)]
    [InlineData(EncodingStrategy.TargetFileSize)]
    public void ApplyAndBuildSettings_Av1FilmGrain_PreservesSettingsAndControlEdits(EncodingStrategy strategy)
    {
        var settings = new TestOutputSettingsBuilder().WithAv1()
            .WithAv1FilmGrain(15, denoise: 1, adaptive: 0).WithStrategy(strategy).Build();

        _sut.ApplySettings(settings);

        var vm = _sut.Strategy.Quality.Av1;
        vm.FilmGrain.Should().Be(15);
        vm.FilmGrainDenoiseChecked.Should().BeTrue();
        vm.AdaptiveFilmGrainChecked.Should().BeFalse();

        vm.FilmGrain = 8;
        vm.FilmGrainDenoiseChecked = false;
        vm.AdaptiveFilmGrainChecked = true;
        var rebuilt = _sut.BuildSettings();

        var encoder = rebuilt.VideoEncoderOptions.Should().BeOfType<Av1EncoderOptions>().Subject;
        encoder.FilmGrain.Should().Be(8);
        encoder.FilmGrainDenoise.Should().Be(0);
        encoder.AdaptiveFilmGrain.Should().Be(1);
        var quality = rebuilt.TargetQuality.Should().BeOfType<Av1QualityOptions>().Subject;
        quality.FilmGrain.Should().Be(8);
        quality.FilmGrainDenoise.Should().Be(0);
        quality.AdaptiveFilmGrain.Should().Be(1);
    }

    [Fact]
    public void AudioTrackSelection_IsExcludedFromSettingsAndPresets_AndSurvivesApplyingSettings()
    {
        _sut.UpdateSourceInfo(new TestMediaInfoBuilder().Build());
        var settings = _sut.BuildSettings();
        var presetBefore = FFApp.Infrastructure.Serialization.SettingsSerializer.SerializeOutputSettings(settings).ToJsonString();
        _sut.Audio.AllAudioTracks = false;
        _sut.Audio.AudioTracks[0].IsSelected = false;
        _sut.BuildSettings().Should().Be(settings);
        FFApp.Infrastructure.Serialization.SettingsSerializer.SerializeOutputSettings(_sut.BuildSettings()).ToJsonString()
            .Should().Be(presetBefore);
        _sut.ApplySettings(settings);
        _sut.Audio.SelectedAudioStreamIndices.GetValueOrDefault().Should().BeEmpty();
        _sut.Audio.AllAudioTracks.Should().BeFalse();
    }

    [Fact]
    public void AudioTrackSelection_ValidationReceivesOnlySelectedTracks()
    {
        var media = new TestMediaInfoBuilder().WithAudioStreams(new AudioStreamInfo(2), new AudioStreamInfo(5)).Build();
        _sut.UpdateSourceInfo(media, [5]);
        _validationService.Received().Validate(Arg.Any<OutputSettings>(),
            Arg.Is<SourceMediaInfo>(m => m.AudioStreams.Length == 1 && m.AudioStreams[0].Index == 5), Arg.Any<double?>());
    }

    private readonly IValidationService _validationService = Substitute.For<IValidationService>();
    private readonly IMessenger _messenger = Substitute.For<IMessenger>();
    private readonly IFfmpegCapabilityService _capabilityService = Substitute.For<IFfmpegCapabilityService>();
    private readonly ExecutablePathsConfig _pathsConfig = new();
    private readonly OutputSettingsViewModel _sut;

    public OutputSettingsViewModelTests()
    {
        _validationService.Validate(Arg.Any<OutputSettings>(), Arg.Any<SourceMediaInfo>())
            .Returns(new ValidationResult());

        _sut = new OutputSettingsViewModel(
            _validationService, _messenger, _capabilityService, _pathsConfig);
    }

    [Fact]
    public void BuildSettings_ReturnsDefaults()
    {
        var settings = _sut.BuildSettings();

        settings.Container.Should().Be(OutputContainer.Mp4);
        settings.VideoCodec.Should().Be(VideoCodec.H264);
        settings.EncodingStrategy.Should().Be(EncodingStrategy.TargetQuality);
        settings.ResolutionPreset.Should().Be(ResolutionPreset.Original);
        settings.AudioOptions.Mode.Should().Be(AudioMode.Copy);
        settings.AudioOptions.Codec.Should().BeNull();
        settings.AudioOptions.Bitrate.Should().BeNull();
        settings.AudioOptions.AudioChannels.Should().BeNull();
        settings.AudioOptions.SampleRate.Should().BeNull();
        settings.AudioOptions.RemoveTrackTitles.Should().BeFalse();
    }

    [Theory]
    [InlineData(AudioMode.Copy)]
    [InlineData(AudioMode.Reencode)]
    [InlineData(AudioMode.Remove)]
    public void RemoveAudioTrackTitles_BuildAndApplyPreserveAndResetChoice(AudioMode mode)
    {
        _sut.Audio.SelectedAudioMode = mode;
        _sut.Audio.RemoveTrackTitles = true;
        var settings = _sut.BuildSettings();
        settings.AudioOptions.RemoveTrackTitles.Should().BeTrue();

        _sut.Audio.RemoveTrackTitles = false;
        _sut.ApplySettings(settings);
        _sut.Audio.RemoveTrackTitles.Should().BeTrue();

        _sut.ApplySettings(new OutputSettings());
        _sut.Audio.RemoveTrackTitles.Should().BeFalse();
    }

    [Theory]
    [InlineData(AudioCodec.Opus, 44100)]
    [InlineData(AudioCodec.Mp3, 96000)]
    public void ApplySettings_LegacyInvalidSampleRate_BuildsAutoInstead(AudioCodec codec, int rate)
    {
        _sut.UpdateSourceInfo(new TestMediaInfoBuilder().WithAudioStreams(new AudioStreamInfo(1, "aac", SampleRate: 96000)).Build());
        var preset = new TestOutputSettingsBuilder().WithContainer(OutputContainer.Mkv)
            .WithAudioReencode(codec, 128_000, sampleRate: rate).Build();
        var json = FFApp.Infrastructure.Serialization.SettingsSerializer.SerializeOutputSettings(preset);
        using var document = System.Text.Json.JsonDocument.Parse(json.ToJsonString());
        var restored = FFApp.Infrastructure.Serialization.SettingsSerializer.DeserializeOutputSettings(document.RootElement);

        _sut.ApplySettings(restored);

        _sut.Audio.SelectedSampleRate.Should().Be(0);
        _sut.Audio.AvailableSampleRateOptions.Should().NotContain(rate);
        _sut.BuildSettings().AudioOptions.SampleRate.Should().BeNull();
    }

    [Fact]
    public void ApplySettings_ValidOpusRate_PreservesSelectionAndOutputSettings()
    {
        _sut.UpdateSourceInfo(new TestMediaInfoBuilder().Build());
        var settings = new TestOutputSettingsBuilder().WithAudioReencode(AudioCodec.Opus, 128_000, sampleRate: 24000).Build();

        _sut.ApplySettings(settings);

        _sut.Audio.SelectedSampleRate.Should().Be(24000);
        _sut.BuildSettings().AudioOptions.SampleRate.Should().Be(24000);
    }

    [Fact]
    public void BuildSettings_AfterContainerChange_ReflectsNewContainer()
    {
        _sut.Format.SelectedContainer = OutputContainer.Webm;

        var settings = _sut.BuildSettings();

        settings.Container.Should().Be(OutputContainer.Webm);
        settings.VideoCodec.Should().Be(VideoCodec.Av1);
    }

    [Fact]
    public void ApplySettings_SetsFormatAndAudioAndStrategy()
    {
        var target = new TestOutputSettingsBuilder()
            .WithContainer(OutputContainer.Mkv)
            .WithVideoCodec(VideoCodec.H265)
            .WithStrategy(EncodingStrategy.TargetBitrate)
            .WithResolution(ResolutionPreset.P1080)
            .WithAudioReencode(AudioCodec.Mp3, 192_000, channels: 2)
            .Build();

        _sut.ApplySettings(target);

        _sut.Format.SelectedContainer.Should().Be(OutputContainer.Mkv);
        _sut.Format.SelectedVideoCodec.Should().Be(VideoCodec.H265);
        _sut.Strategy.SelectedStrategy.Should().Be(EncodingStrategy.TargetBitrate);
        _sut.SelectedResolution.Should().Be(ResolutionPreset.P1080);
        _sut.Audio.SelectedAudioMode.Should().Be(AudioMode.Reencode);
        _sut.Audio.SelectedAudioCodec.Should().Be(AudioCodec.Mp3);
        _sut.Audio.AudioBitrate.Should().Be(192_000);
        _sut.Audio.SelectedAudioChannels.Should().Be(2);
    }

    [Fact]
    public void ApplySettings_WithH265_QualityGetsH265Defaults()
    {
        var target = new TestOutputSettingsBuilder()
            .WithH265()
            .Build();

        _sut.ApplySettings(target);

        _sut.Strategy.Quality.Active.Should().BeSameAs(_sut.Strategy.Quality.H265);
        _sut.Strategy.Quality.H265.Crf.Should().Be(28);
        _sut.Strategy.Quality.H265.Profile.Should().Be("main");
    }

    [Fact]
    public void UpdateSourceInfo_TriggersValidation()
    {
        var mediaInfo = new TestMediaInfoBuilder().Build();

        _sut.UpdateSourceInfo(mediaInfo);

        _validationService.Received().Validate(Arg.Any<OutputSettings>(), mediaInfo);
    }

    [Fact]
    public void ChildPropertyChange_SendsOutputSettingsChangedMessage()
    {
        // Use a real messenger so extension methods work naturally
        var realMessenger = new WeakReferenceMessenger();
        var sut = new OutputSettingsViewModel(
            _validationService, realMessenger, _capabilityService, _pathsConfig);

        OutputSettings? received = null;
        realMessenger.Register<OutputSettingsChangedMessage>(this, (_, m) => received = m.Value);

        sut.Format.SelectedVideoCodec = VideoCodec.H265;

        received.Should().NotBeNull();
        received!.VideoCodec.Should().Be(VideoCodec.H265);
    }

    [Fact]
    public void ContainerChange_UpdatesAvailableAudioCodecsOnAudioVM()
    {
        _sut.Format.SelectedContainer = OutputContainer.Webm;

        _sut.Audio.AvailableAudioCodecs.Should().Contain(AudioCodec.Opus);
        _sut.Audio.AvailableAudioCodecs.Should().NotContain(AudioCodec.Aac);
    }

    [Fact]
    public void BuildSettings_MkvpropeditAvailableAndEnabled_PreservesPostProcessing()
    {
        _pathsConfig.Configure(new ExecutablePaths(Mkvpropedit: "/tools/mkvpropedit"));
        var sut = new OutputSettingsViewModel(
            _validationService, _messenger, _capabilityService, _pathsConfig);
        sut.Format.SelectedContainer = OutputContainer.Mkv;
        sut.RecalculateMatroskaStatistics = true;

        var settings = sut.BuildSettings();

        sut.IsMatroskaPostProcessingAvailable.Should().BeTrue();
        settings.PostProcessing.RecalculateMatroskaStatistics.Should().BeTrue();
    }

    [Fact]
    public void BuildSettings_MkvpropeditUnavailable_DisablesPostProcessing()
    {
        _sut.Format.SelectedContainer = OutputContainer.Mkv;
        _sut.RecalculateMatroskaStatistics = true;

        var settings = _sut.BuildSettings();

        _sut.IsMatroskaPostProcessingAvailable.Should().BeFalse();
        settings.PostProcessing.RecalculateMatroskaStatistics.Should().BeFalse();
    }

    [Fact]
    public void MatroskaPostProcessing_Mp4_IsNotVisibleOrAvailable()
    {
        _pathsConfig.Configure(new ExecutablePaths(Mkvpropedit: "/tools/mkvpropedit"));
        var sut = new OutputSettingsViewModel(
            _validationService, _messenger, _capabilityService, _pathsConfig);

        sut.IsMatroskaPostProcessingVisible.Should().BeFalse();
        sut.IsMatroskaPostProcessingAvailable.Should().BeFalse();
    }
}
