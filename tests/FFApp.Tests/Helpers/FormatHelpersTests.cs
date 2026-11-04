using FFApp.Core.Models;
using FFApp.Helpers;
using FFApp.Tests.Builders;
using FluentAssertions;

namespace FFApp.Tests.Helpers;

public class FormatHelpersTests
{

    [Fact]
    public void BuildJobDefinition_PassesAudioSelectionFromContext()
    {
        var context = new JobContext(new TestMediaInfoBuilder().Build(), new OutputSettings(), SelectedAudioStreamIndices: [1, 5]);
        var job = FormatHelpers.BuildJobDefinition(context);
        job.SelectedAudioStreamIndices.GetValueOrDefault().Should().Equal(1, 5);
        FormatHelpers.BuildJobDefinition(context with { SelectedAudioStreamIndices = null })
            .SelectedAudioStreamIndices.Should().BeNull();
    }

    // --- BuildMediaSummaryText ---

    [Fact]
    public void BuildMediaSummaryText_WithFullInfo_ContainsAllSections()
    {
        var media = new TestMediaInfoBuilder().Build();
        var text = FormatHelpers.BuildMediaSummaryText(media);

        text.Should().Contain("Source:");
        text.Should().Contain("Format:");
        text.Should().Contain("Duration:");
        text.Should().Contain("Primary video stream");
        text.Should().Contain("Audio streams detail");
    }

    [Fact]
    public void BuildMediaSummaryText_NoPrimaryVideo_SkipsVideoSection()
    {
        var media = new TestMediaInfoBuilder().WithNoVideo().Build();
        var text = FormatHelpers.BuildMediaSummaryText(media);

        text.Should().NotContain("Primary video stream");
    }

    [Fact]
    public void BuildMediaSummaryText_MultipleVideoStreams_IncludesWarning()
    {
        var media = new TestMediaInfoBuilder().WithMultipleVideoStreams().Build();
        var text = FormatHelpers.BuildMediaSummaryText(media);

        text.Should().Contain("Warning: multiple video streams");
    }

    [Fact]
    public void BuildMediaSummaryText_NoAudio_SkipsAudioDetail()
    {
        var media = new TestMediaInfoBuilder().WithNoAudio().Build();
        var text = FormatHelpers.BuildMediaSummaryText(media);

        text.Should().NotContain("Audio streams detail");
    }

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
