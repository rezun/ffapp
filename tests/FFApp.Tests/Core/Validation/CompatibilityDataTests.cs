using System.Collections.Immutable;
using FFApp.Core.Models;
using FFApp.Core.Validation;
using FluentAssertions;

namespace FFApp.Tests.Core.Validation;

public class CompatibilityDataTests
{
    [Fact]
    public void VideoCodecMatrix_Mp4_ContainsExpectedCodecs()
    {
        CompatibilityData.VideoCodecMatrix[OutputContainer.Mp4]
            .Should().BeEquivalentTo([VideoCodec.H264, VideoCodec.H265, VideoCodec.Av1, VideoCodec.Vp9]);
    }

    [Fact]
    public void VideoCodecMatrix_Mkv_ContainsExpectedCodecs()
    {
        CompatibilityData.VideoCodecMatrix[OutputContainer.Mkv]
            .Should().HaveCount(4)
            .And.Contain([VideoCodec.H264, VideoCodec.H265, VideoCodec.Vp9, VideoCodec.Av1]);
    }

    [Fact]
    public void VideoCodecMatrix_Webm_ContainsOnlyVpxCodecs()
    {
        CompatibilityData.VideoCodecMatrix[OutputContainer.Webm]
            .Should().BeEquivalentTo([VideoCodec.Vp9, VideoCodec.Av1]);
    }

    [Fact]
    public void VideoCodecMatrix_Ogg_ContainsOnlyVpxCodecs()
    {
        CompatibilityData.VideoCodecMatrix[OutputContainer.Ogg]
            .Should().BeEquivalentTo([VideoCodec.Vp9]);
    }

    [Fact]
    public void AudioCodecMatrix_Mp4_ContainsAacMp3AndOpus()
    {
        CompatibilityData.AudioCodecMatrix[OutputContainer.Mp4]
            .Should().BeEquivalentTo([AudioCodec.Aac, AudioCodec.Mp3, AudioCodec.Opus]);
    }

    [Fact]
    public void AudioCodecMatrix_Mkv_ContainsAllFourCodecs()
    {
        CompatibilityData.AudioCodecMatrix[OutputContainer.Mkv]
            .Should().HaveCount(4);
    }

    [Fact]
    public void AudioCodecMatrix_Webm_ContainsOpusAndVorbis()
    {
        CompatibilityData.AudioCodecMatrix[OutputContainer.Webm]
            .Should().BeEquivalentTo([AudioCodec.Opus, AudioCodec.Vorbis]);
    }

    [Fact]
    public void PresetResolutions_720p_Returns1280x720()
    {
        CompatibilityData.PresetResolutions[ResolutionPreset.P720].Should().Be(new Resolution(1280, 720));
    }

    [Fact]
    public void PresetResolutions_1080p_Returns1920x1080()
    {
        CompatibilityData.PresetResolutions[ResolutionPreset.P1080].Should().Be(new Resolution(1920, 1080));
    }

    [Fact]
    public void PresetResolutions_4K_Returns3840x2160()
    {
        CompatibilityData.PresetResolutions[ResolutionPreset.P4K].Should().Be(new Resolution(3840, 2160));
    }

    [Fact]
    public void PresetResolutions_DoesNotContainOriginal()
    {
        CompatibilityData.PresetResolutions.ContainsKey(ResolutionPreset.Original).Should().BeFalse();
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

    [Fact]
    public void BitrateWarningThreshold_4K_Returns12Mbps()
    {
        CompatibilityData.BitrateWarningThreshold(new Resolution(3840, 2160)).Should().Be(12_000_000);
    }

    [Fact]
    public void BitrateWarningThreshold_1440p_Returns6Mbps()
    {
        CompatibilityData.BitrateWarningThreshold(new Resolution(2560, 1440)).Should().Be(6_000_000);
    }

    [Fact]
    public void BitrateWarningThreshold_1080p_Returns3Mbps()
    {
        CompatibilityData.BitrateWarningThreshold(new Resolution(1920, 1080)).Should().Be(3_000_000);
    }

    [Fact]
    public void BitrateWarningThreshold_720p_Returns1_5Mbps()
    {
        CompatibilityData.BitrateWarningThreshold(new Resolution(1280, 720)).Should().Be(1_500_000);
    }

    [Fact]
    public void BitrateWarningThreshold_480p_Returns800Kbps()
    {
        CompatibilityData.BitrateWarningThreshold(new Resolution(640, 480)).Should().Be(800_000);
    }

    [Fact]
    public void BitrateWarningThreshold_Null_ReturnsNull()
    {
        CompatibilityData.BitrateWarningThreshold(null).Should().BeNull();
    }

    [Fact]
    public void BitrateWarningThreshold_Portrait4K_UsesLongEdge()
    {
        CompatibilityData.BitrateWarningThreshold(new Resolution(2160, 3840)).Should().Be(12_000_000);
    }

    // --- QualityPresetOptions / QualityProfileOptions ---

    [Fact]
    public void QualityPresetOptions_H264_ReturnsH26xPresets()
    {
        CompatibilityData.QualityPresetOptions(VideoCodec.H264).Should().BeSameAs(CompatibilityData.H26xPresetOptions);
    }

    [Fact]
    public void QualityPresetOptions_Vp9_ReturnsEmpty()
    {
        CompatibilityData.QualityPresetOptions(VideoCodec.Vp9).Should().BeEmpty();
    }

    [Fact]
    public void QualityProfileOptions_H264_Returns3Profiles()
    {
        CompatibilityData.QualityProfileOptions(VideoCodec.H264).Should().HaveCount(3);
    }

    [Fact]
    public void QualityProfileOptions_H265_Returns2Profiles()
    {
        CompatibilityData.QualityProfileOptions(VideoCodec.H265).Should().HaveCount(2);
    }

    [Fact]
    public void QualityProfileOptions_Vp9_ReturnsEmpty()
    {
        CompatibilityData.QualityProfileOptions(VideoCodec.Vp9).Should().BeEmpty();
    }

    // --- VideoCodecLabel ---

    [Theory]
    [InlineData(VideoCodec.H264, "H.264")]
    [InlineData(VideoCodec.H265, "H.265")]
    [InlineData(VideoCodec.Vp9, "VP9")]
    public void VideoCodecLabel_ReturnsExpected(VideoCodec codec, string expected)
    {
        CompatibilityData.VideoCodecLabel(codec).Should().Be(expected);
    }
}
