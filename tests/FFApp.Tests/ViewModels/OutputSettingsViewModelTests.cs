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
