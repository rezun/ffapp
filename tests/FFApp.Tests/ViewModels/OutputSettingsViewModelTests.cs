using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Tests.Builders;
using FFApp.ViewModels;
using FluentAssertions;
using NSubstitute;

namespace FFApp.Tests.ViewModels;

public class OutputSettingsViewModelTests
{
    private readonly IValidationService _validationService = Substitute.For<IValidationService>();
    private readonly IMessenger _messenger = Substitute.For<IMessenger>();
    private readonly OutputSettingsViewModel _sut;

    public OutputSettingsViewModelTests()
    {
        _validationService.Validate(Arg.Any<OutputSettings>(), Arg.Any<SourceMediaInfo>())
            .Returns(new ValidationResult());

        _sut = new OutputSettingsViewModel(_validationService, _messenger);
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
    }

    [Fact]
    public void BuildSettings_AfterContainerChange_ReflectsNewContainer()
    {
        _sut.Format.SelectedContainer = OutputContainer.Webm;

        var settings = _sut.BuildSettings();

        settings.Container.Should().Be(OutputContainer.Webm);
        settings.VideoCodec.Should().Be(VideoCodec.Vp8);
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

        _sut.Strategy.Quality.IsH26xCodec.Should().BeTrue();
        _sut.Strategy.Quality.Crf.Should().Be(28);
        _sut.Strategy.Quality.H26xProfile.Should().Be("main");
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
        var sut = new OutputSettingsViewModel(_validationService, realMessenger);

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
}
