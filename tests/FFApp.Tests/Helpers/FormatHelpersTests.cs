using FFApp.Core.Models;
using FFApp.Helpers;
using FFApp.Tests.Builders;
using FluentAssertions;

namespace FFApp.Tests.Helpers;

public class FormatHelpersTests
{

    [Fact]
    public void BuildJobDefinition_ForwardsSourceTrimCropAndAudioSelectionFromContext()
    {
        var media = new TestMediaInfoBuilder().Build();
        var context = new JobContext(media, new OutputSettings(Container: OutputContainer.Mkv),
            TrimStartSeconds: 5, TrimEndSeconds: 25, Crop: new CropRect(10, 20, 800, 600),
            SelectedAudioStreamIndices: [1, 5]);

        var job = FormatHelpers.BuildJobDefinition(context);

        job.SourcePath.Should().Be(media.SourcePath);
        job.Container.Should().Be(OutputContainer.Mkv);
        job.TrimStartSeconds.Should().Be(5);
        job.TrimEndSeconds.Should().Be(25);
        job.Crop.Should().Be(new CropRect(10, 20, 800, 600));
        job.SelectedAudioStreamIndices.GetValueOrDefault().Should().Equal(1, 5);
        FormatHelpers.BuildJobDefinition(context with { SelectedAudioStreamIndices = null })
            .SelectedAudioStreamIndices.Should().BeNull();
    }

    // --- BuildMediaSummaryText ---

    // --- Label functions ---

    [Theory]
    [InlineData(AudioCodec.Aac, "AAC")]
    [InlineData(AudioCodec.Mp3, "MP3")]
    [InlineData(AudioCodec.Vorbis, "Vorbis")]
    [InlineData(AudioCodec.Opus, "Opus")]
    public void AudioCodecLabel_AllValues_ReturnExpected(AudioCodec codec, string expected)
    {
        FormatHelpers.AudioCodecLabel(codec).Should().Be(expected);
    }

    [Theory]
    [InlineData(OutputContainer.Mp4, "MP4")]
    [InlineData(OutputContainer.Mkv, "MKV")]
    [InlineData(OutputContainer.Webm, "WEBM")]
    [InlineData(OutputContainer.Ogg, "OGG")]
    public void ContainerLabel_AllValues_ReturnExpected(OutputContainer container, string expected)
    {
        FormatHelpers.ContainerLabel(container).Should().Be(expected);
    }

    [Theory]
    [InlineData(ResolutionPreset.Original, "Original")]
    [InlineData(ResolutionPreset.P720, "720p")]
    [InlineData(ResolutionPreset.P1080, "1080p")]
    [InlineData(ResolutionPreset.P1440, "1440p")]
    [InlineData(ResolutionPreset.P4K, "4K")]
    public void ResolutionPresetLabel_AllValues_ReturnExpected(ResolutionPreset preset, string expected)
    {
        FormatHelpers.ResolutionPresetLabel(preset).Should().Be(expected);
    }

    // --- BuildJobDefinition ---

    [Fact]
    public void BuildJobDefinition_MapsAllFieldsFromSettings()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithContainer(OutputContainer.Mkv)
            .WithVideoCodec(VideoCodec.H265)
            .WithStrategy(EncodingStrategy.TargetBitrate)
            .WithResolution(ResolutionPreset.P720)
            .Build();

        var job = FormatHelpers.BuildJobDefinition("/src.mp4", "/out.mkv", settings);

        job.SourcePath.Should().Be("/src.mp4");
        job.OutputPath.Should().Be("/out.mkv");
        job.Container.Should().Be(OutputContainer.Mkv);
        job.VideoCodec.Should().Be(VideoCodec.H265);
        job.EncodingStrategy.Should().Be(EncodingStrategy.TargetBitrate);
        job.ResolutionPreset.Should().Be(ResolutionPreset.P720);
    }
}
