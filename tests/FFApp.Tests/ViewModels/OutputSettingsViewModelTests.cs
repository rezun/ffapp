using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Core.Validation;
using FFApp.Tests.Builders;
using FFApp.ViewModels;
using FluentAssertions;
using NSubstitute;

namespace FFApp.Tests.ViewModels;

public class OutputSettingsViewModelTests
{
    [Fact]
    public void ApplyAndBuildSettings_Av1QualityControls_RoundTripAndReflectEdits()
    {
        var encoder = new Av1EncoderOptions(EnableVarianceBoost: 1, VarianceBoostStrength: 4, VarianceOctile: 8,
            FilmGrain: 15, FilmGrainDenoise: 1, AdaptiveFilmGrain: 0);
        var quality = new Av1QualityOptions(EnableVarianceBoost: 1, VarianceBoostStrength: 4, VarianceOctile: 8,
            FilmGrain: 15, FilmGrainDenoise: 1, AdaptiveFilmGrain: 0);
        _sut.ApplySettings(new TestOutputSettingsBuilder().WithAv1().WithEncoderOptions(encoder).WithQuality(quality).Build());
        var built = _sut.BuildSettings();
        built.VideoEncoderOptions.Should().Be(encoder);
        built.TargetQuality.Should().Be(quality);

        var vm = _sut.Strategy.Quality.Av1;
        vm.VarianceBoostStrength = 1;
        vm.VarianceOctile = 3;
        vm.FilmGrain = 8;
        vm.FilmGrainDenoiseChecked = false;
        vm.AdaptiveFilmGrainChecked = true;
        var rebuilt = _sut.BuildSettings();

        rebuilt.VideoEncoderOptions.Should().Be(encoder with
        {
            VarianceBoostStrength = 1, VarianceOctile = 3, FilmGrain = 8, FilmGrainDenoise = 0, AdaptiveFilmGrain = 1,
        });
        rebuilt.TargetQuality.Should().Be(quality with
        {
            VarianceBoostStrength = 1, VarianceOctile = 3, FilmGrain = 8, FilmGrainDenoise = 0, AdaptiveFilmGrain = 1,
        });
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

    [Fact]
    public void SubtitleTrackSelection_IsExcludedFromSettings_AndValidationReceivesOnlySelectedTracks()
    {
        var media = new TestMediaInfoBuilder().Build() with
        {
            SubtitleStreams = [new SubtitleStreamInfo(2, "hdmv_pgs_subtitle"), new SubtitleStreamInfo(4, "subrip")],
        };
        _sut.UpdateSourceInfo(media, selectedSubtitleStreamIndices: [4]);
        _sut.StreamHandling.AllSubtitleTracks.Should().BeFalse();
        _sut.StreamHandling.SubtitleTracks.Select(t => t.IsSelected).Should().Equal(false, true);
        _validationService.Received().Validate(Arg.Any<OutputSettings>(),
            Arg.Is<SourceMediaInfo>(m => m.SubtitleStreams.Length == 1 && m.SubtitleStreams[0].Index == 4), Arg.Any<double?>());

        var settings = _sut.BuildSettings();
        _sut.StreamHandling.SubtitleTracks[0].IsSelected = true;
        _sut.BuildSettings().Should().Be(settings);
        _sut.StreamHandling.SelectedSubtitleStreamIndices.GetValueOrDefault().Should().Equal(2, 4);
    }

    [Fact]
    public void SubtitleTrackSelection_IsHiddenWhenSubtitlesAreRemoved()
    {
        _sut.UpdateSourceInfo(new TestMediaInfoBuilder().WithSubtitles().Build());
        _sut.StreamHandling.IsSubtitleTrackSelectionVisible.Should().BeTrue();
        _sut.StreamHandling.RemoveSubtitles = true;
        _sut.StreamHandling.IsSubtitleTrackSelectionVisible.Should().BeFalse();
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
    public void ColorSettings_AreRestoredAndEditsNotifyTheWorkspace()
    {
        var messenger = new WeakReferenceMessenger();
        var sut = new OutputSettingsViewModel(_validationService, messenger, _capabilityService, _pathsConfig);
        sut.UpdateSourceInfo(new TestMediaInfoBuilder().Build());
        var colors = new ColorMetadataSettings(ColorMetadataMode.Custom, "bt470bg", "gamma28", "bt470bg", "pc");
        sut.ApplySettings(new OutputSettings(ColorMetadata: colors));
        sut.BuildSettings().ColorMetadata.Should().Be(colors);
        OutputSettings? notified = null;
        var recipient = new object();
        messenger.Register<OutputSettingsChangedMessage>(recipient, (_, message) => notified = message.Value);
        sut.Color.SelectedRange = "tv";
        notified.Should().NotBeNull();
        notified?.ColorMetadata.Should().Be(colors with { Range = "tv" });
        sut.Format.SelectedVideoCodec = VideoCodec.Av1;
        sut.BuildSettings().ColorMetadata.Should().Be(colors with { Range = "tv" });
    }


    [Fact]
    public void ColorSummary_UpdatesForSourceAssumptionsHdrAndCopy()
    {
        _sut.UpdateSourceInfo(new TestMediaInfoBuilder().Build());
        _sut.Color.SelectedMode.Should().Be(ColorMetadataMode.Automatic);
        _sut.Color.OutputSummary.Should().Contain("unspecified values remain unspecified");
        _sut.Color.SelectedMode = ColorMetadataMode.AssumeBt709;
        _sut.Color.OutputSummary.Should().Contain("assumed").And.Contain("Limited");
        _sut.UpdateSourceInfo(new TestMediaInfoBuilder().WithHdr10().Build());
        _sut.Color.IsEditable.Should().BeFalse();
        _sut.Color.OutputSummary.Should().Contain("HDR settings");
        _sut.Hdr.SelectedHdrMode = HdrMode.TonemapToSdr;
        _sut.Color.OutputSummary.Should().Contain("bt709");
        _sut.UpdateSourceInfo(new TestMediaInfoBuilder().Build());
        _sut.Color.IsEditable.Should().BeTrue();
        _sut.Format.SelectedVideoMode = VideoMode.Copy;
        _sut.Color.IsEditable.Should().BeFalse();
        _sut.Color.OutputSummary.Should().Contain("unchanged");
    }


    [Fact]
    public void ApplySettings_ThenBuildSettings_RoundTripsEveryOption()
    {
        var edited = new TestOutputSettingsBuilder()
            .WithContainer(OutputContainer.Mkv)
            .WithH265()
            .WithStrategy(EncodingStrategy.TargetBitrate)
            .WithResolution(ResolutionPreset.P1080)
            .WithAudioReencode(AudioCodec.Mp3, 192_000, channels: 2)
            .WithRemoveAudioTrackTitles()
            .WithRemoveImages()
            .Build();
        var defaults = new OutputSettings(VideoEncoderOptions: H26xEncoderOptions.ForCodec(VideoCodec.H264));

        foreach (var settings in new[] { edited, defaults })
        {
            _sut.ApplySettings(settings);
            _sut.BuildSettings().Should().Be(settings);
        }
    }

    [Fact]
    public void ApplySettings_LegacyInvalidSampleRate_BuildsAutoInstead()
    {
        _sut.UpdateSourceInfo(new TestMediaInfoBuilder().WithAudioStreams(new AudioStreamInfo(1, "aac", SampleRate: 96000)).Build());
        var preset = new TestOutputSettingsBuilder().WithContainer(OutputContainer.Mkv)
            .WithAudioReencode(AudioCodec.Opus, 128_000, sampleRate: 44100).Build();
        var json = FFApp.Infrastructure.Serialization.SettingsSerializer.SerializeOutputSettings(preset);
        using var document = System.Text.Json.JsonDocument.Parse(json.ToJsonString());
        var restored = FFApp.Infrastructure.Serialization.SettingsSerializer.DeserializeOutputSettings(document.RootElement);

        _sut.ApplySettings(restored);

        _sut.Audio.SelectedSampleRate.Should().Be(0);
        _sut.BuildSettings().AudioOptions.SampleRate.Should().BeNull();
    }

    [Fact]
    public void ApplySettings_ReencodeChannelsAndSampleRate_SurviveLoadingASmallerSource()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithAudioReencode(AudioCodec.Opus, 320_000, channels: 6, sampleRate: 48000).Build();

        _sut.ApplySettings(settings);
        _sut.UpdateSourceInfo(new TestMediaInfoBuilder()
            .WithAudioStreams(new AudioStreamInfo(1, "aac", Channels: 2, SampleRate: 44100)).Build());

        _sut.BuildSettings().AudioOptions.Should().Be(settings.AudioOptions);
    }

    [Fact]
    public void ContainerChange_UpdatesAudioCodecsAndReplacesAnUnsupportedSelection()
    {
        _sut.Format.SelectedContainer = OutputContainer.Webm;

        _sut.Audio.AvailableAudioCodecs.Should().Equal(CompatibilityData.AllowedAudioCodecs(OutputContainer.Webm));
        _sut.Audio.SelectedAudioCodec.Should().Be(CompatibilityData.AllowedAudioCodecs(OutputContainer.Webm)[0]);
    }

    [Theory]
    [InlineData(OutputContainer.Mkv, true, true, true)]
    [InlineData(OutputContainer.Mkv, false, true, false)]
    [InlineData(OutputContainer.Mp4, true, false, false)]
    public void MatroskaPostProcessing_RequiresMatroskaContainerAndMkvpropedit(
        OutputContainer container, bool hasMkvpropedit, bool expectedVisible, bool expectedAvailable)
    {
        if (hasMkvpropedit)
            _pathsConfig.Configure(new ExecutablePaths(Mkvpropedit: "/tools/mkvpropedit"));
        _sut.Format.SelectedContainer = container;
        _sut.RecalculateMatroskaStatistics = true;

        _sut.IsMatroskaPostProcessingVisible.Should().Be(expectedVisible);
        _sut.IsMatroskaPostProcessingAvailable.Should().Be(expectedAvailable);
        _sut.BuildSettings().PostProcessing.RecalculateMatroskaStatistics.Should().Be(expectedAvailable);
    }
}
