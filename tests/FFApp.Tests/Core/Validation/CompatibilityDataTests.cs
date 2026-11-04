using System.Collections.Immutable;
using FFApp.Core.Models;
using FFApp.Core.Validation;
using FluentAssertions;

namespace FFApp.Tests.Core.Validation;

public class CompatibilityDataTests
{
    // --- Container codec lists ---

    [Fact]
    public void EveryContainer_OffersCodecsForItsStreams()
    {
        Enum.GetValues<OutputContainer>()
            .Where(container => CompatibilityData.AllowedAudioCodecs(container).Count == 0)
            .Should().BeEmpty("every container must offer at least one audio codec");
        CompatibilityData.VideoContainers
            .Where(container => CompatibilityData.AllowedVideoCodecs(container).Count == 0)
            .Should().BeEmpty("validation treats an empty video codec list as unrestricted");
    }

    // --- EstimateAudioBits ---

    [Fact]
    public void EstimateAudioBits_RemoveMode_ReturnsZero()
    {
        var result = CompatibilityData.EstimateAudioBits(
            AudioMode.Remove, null, [new AudioStreamInfo(1, "aac", 128_000)], 60.0);
        result.Should().Be(0);
    }

    [Fact]
    public void EstimateAudioBits_NoStreams_ReturnsZero()
    {
        var result = CompatibilityData.EstimateAudioBits(
            AudioMode.Copy, null, [], 60.0);
        result.Should().Be(0);
    }

    [Fact]
    public void EstimateAudioBits_NullDuration_ReturnsNull()
    {
        var result = CompatibilityData.EstimateAudioBits(
            AudioMode.Reencode, 128_000, [new AudioStreamInfo(1, "aac", 128_000)], null);
        result.Should().BeNull();
    }

    [Fact]
    public void EstimateAudioBits_ReencodeMode_CalculatesCorrectly()
    {
        var result = CompatibilityData.EstimateAudioBits(
            AudioMode.Reencode, 128_000, [new AudioStreamInfo(1, "aac", 128_000)], 60.0);
        result.Should().Be(128_000 * 60);
    }

    [Fact]
    public void EstimateAudioBits_ReencodeMode_NullBitrate_ReturnsNull()
    {
        var result = CompatibilityData.EstimateAudioBits(
            AudioMode.Reencode, null, [new AudioStreamInfo(1, "aac", 128_000)], 60.0);
        result.Should().BeNull();
    }

    [Fact]
    public void EstimateAudioBits_ReencodeMode_MultipleStreams_MultipliesByStreamCount()
    {
        ImmutableArray<AudioStreamInfo> streams =
        [
            new(1, "aac", 128_000),
            new(2, "aac", 128_000),
        ];
        var result = CompatibilityData.EstimateAudioBits(AudioMode.Reencode, 128_000, streams, 60.0);
        result.Should().Be(128_000 * 60 * 2);
    }

    [Fact]
    public void EstimateAudioBits_CopyMode_SumsBitrates()
    {
        ImmutableArray<AudioStreamInfo> streams =
        [
            new(1, "aac", 128_000),
            new(2, "aac", 192_000),
        ];
        var result = CompatibilityData.EstimateAudioBits(AudioMode.Copy, null, streams, 60.0);
        result.Should().Be((128_000 + 192_000) * 60);
    }

    [Fact]
    public void EstimateAudioBits_CopyMode_NullStreamBitrate_ReturnsNull()
    {
        ImmutableArray<AudioStreamInfo> streams = [new(1, "aac", null)];
        var result = CompatibilityData.EstimateAudioBits(AudioMode.Copy, null, streams, 60.0);
        result.Should().BeNull();
    }

    // --- BitrateWarningThreshold ---

    [Theory]
    [InlineData(2160, 3840, 12_000_000)]
    [InlineData(2560, 1440, 6_000_000)]
    [InlineData(1920, 1080, 3_000_000)]
    [InlineData(1280, 720, 1_500_000)]
    [InlineData(640, 480, 800_000)]
    public void BitrateWarningThreshold_UsesLongEdgeTier(int width, int height, int expected)
    {
        CompatibilityData.BitrateWarningThreshold(new Resolution(width, height)).Should().Be(expected);
    }

    [Fact]
    public void BitrateWarningThreshold_Null_ReturnsNull()
    {
        CompatibilityData.BitrateWarningThreshold(null).Should().BeNull();
    }

    // --- QualityPresetOptions / QualityProfileOptions ---

    [Theory]
    [InlineData(VideoCodec.H264)]
    [InlineData(VideoCodec.H265)]
    public void QualityOptions_OfferTheH26xDefaults(VideoCodec codec)
    {
        var encoder = H26xEncoderOptions.ForCodec(codec);
        var quality = H26xQualityOptions.ForCodec(codec);

        CompatibilityData.QualityPresetOptions(codec).Should().Contain([encoder.Preset, quality.Preset]);
        CompatibilityData.QualityProfileOptions(codec).Should().Contain([encoder.Profile, quality.Profile]);
    }

    // --- VideoCodecLabel ---

    [Fact]
    public void VideoCodecLabel_EveryCodecHasDisplayLabel()
    {
        Enum.GetValues<VideoCodec>()
            .Where(codec => CompatibilityData.VideoCodecLabel(codec) == codec.ToString())
            .Should().BeEmpty("codecs without an explicit label fall back to the enum name");
    }
}
