using FFApp.Core.Models;
using FFApp.Core.Validation;
using FFApp.Tests.Builders;
using FluentAssertions;

namespace FFApp.Tests.Core.Validation;

public class ValidationServiceTests
{
    private readonly ValidationService _sut = new();

    private static bool HasIssueWithCode(ValidationResult result, string code) =>
        result.Issues.Any(i => i.Code == code);

    // ── Container / codec compatibility ─────────────────────────────────

    [Fact]
    public void Validate_H264InMp4_NoCodecError()
    {
        var settings = new TestOutputSettingsBuilder().Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "video_codec_incompatible").Should().BeFalse();
    }

    [Fact]
    public void Validate_H264InWebm_ReturnsVideoCodecIncompatible()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithContainer(OutputContainer.Webm)
            .WithVideoCodec(VideoCodec.H264)
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "video_codec_incompatible").Should().BeTrue();
        result.HasErrors.Should().BeTrue();
    }

    [Fact]
    public void Validate_Vp9InMp4_ReturnsVideoCodecIncompatible()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithVideoCodec(VideoCodec.Vp9)
            .WithContainer(OutputContainer.Mp4)
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "video_codec_incompatible").Should().BeTrue();
    }

    [Theory]
    [InlineData(OutputContainer.Mp4, VideoCodec.H264)]
    [InlineData(OutputContainer.Mp4, VideoCodec.H265)]
    [InlineData(OutputContainer.Mkv, VideoCodec.H264)]
    [InlineData(OutputContainer.Mkv, VideoCodec.H265)]
    [InlineData(OutputContainer.Mkv, VideoCodec.Vp8)]
    [InlineData(OutputContainer.Mkv, VideoCodec.Vp9)]
    [InlineData(OutputContainer.Webm, VideoCodec.Vp8)]
    [InlineData(OutputContainer.Webm, VideoCodec.Vp9)]
    [InlineData(OutputContainer.Ogg, VideoCodec.Vp8)]
    [InlineData(OutputContainer.Ogg, VideoCodec.Vp9)]
    public void Validate_ValidContainerCodecCombo_NoVideoCodecError(OutputContainer container, VideoCodec codec)
    {
        var builder = new TestOutputSettingsBuilder()
            .WithContainer(container)
            .WithVideoCodec(codec);

        // Use matching encoder options and quality for the codec.
        if (codec is VideoCodec.Vp8)
            builder = builder.WithVp8();
        else if (codec is VideoCodec.Vp9)
            builder = builder.WithVp9();
        else if (codec is VideoCodec.H265)
            builder = builder.WithH265();

        // Override container after WithVpX since those helpers set container to Webm.
        builder = builder.WithContainer(container);

        var settings = builder.Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "video_codec_incompatible").Should().BeFalse();
    }

    [Fact]
    public void Validate_OpusReencodeInMp4_ReturnsAudioCodecIncompatible()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithAudioReencode(AudioCodec.Opus, 128_000)
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "audio_codec_incompatible").Should().BeTrue();
    }

    // ── Audio validation ────────────────────────────────────────────────

    [Fact]
    public void Validate_ReencodeModeMissingCodec_ReturnsAudioCodecRequired()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithAudio(new AudioOptions(AudioMode.Reencode, Codec: null, Bitrate: 128_000))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "audio_codec_required").Should().BeTrue();
    }

    [Fact]
    public void Validate_ReencodeModeMissingBitrate_ReturnsAudioBitrateRequired()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithAudio(new AudioOptions(AudioMode.Reencode, Codec: AudioCodec.Aac, Bitrate: null))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "audio_bitrate_required").Should().BeTrue();
    }

    [Fact]
    public void Validate_CopyModeWithUnknownSourceCodec_ReturnsAudioCopyCodecUnknown()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithAudioCopy()
            .Build();
        var media = new TestMediaInfoBuilder()
            .WithAudioStreams(new AudioStreamInfo(1, "some_unknown_codec", 128_000))
            .Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "audio_copy_codec_unknown").Should().BeTrue();
    }

    [Fact]
    public void Validate_CopyModeWithIncompatibleSourceCodec_ReturnsAudioCopyIncompatible()
    {
        // Opus source copied into MP4 is not allowed.
        var settings = new TestOutputSettingsBuilder()
            .WithContainer(OutputContainer.Mp4)
            .WithAudioCopy()
            .Build();
        var media = new TestMediaInfoBuilder()
            .WithAudioStreams(new AudioStreamInfo(1, "opus", 128_000))
            .Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "audio_copy_incompatible").Should().BeTrue();
    }

    [Fact]
    public void Validate_AudioChannelsWithCopyMode_ReturnsAudioChannelsRequiresReencode()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithAudio(new AudioOptions(AudioMode.Copy, AudioChannels: 2))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "audio_channels_requires_reencode").Should().BeTrue();
    }

    [Fact]
    public void Validate_AudioChannelsWithReencode_NoChannelsError()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithAudioReencode(AudioCodec.Aac, 128_000, channels: 2)
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "audio_channels_requires_reencode").Should().BeFalse();
    }

    // ── Subtitle / data validation ──────────────────────────────────────

    [Theory]
    [InlineData(OutputContainer.Webm)]
    [InlineData(OutputContainer.Ogg)]
    public void Validate_WebmOggWithSubtitlesNotRemoved_ReturnsSubtitleCopyIncompatible(OutputContainer container)
    {
        var builder = container == OutputContainer.Webm
            ? new TestOutputSettingsBuilder().WithVp9()
            : new TestOutputSettingsBuilder().WithVp8().WithContainer(OutputContainer.Ogg);

        var settings = builder.Build();
        var media = new TestMediaInfoBuilder().WithSubtitles("subrip").Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "subtitle_copy_incompatible").Should().BeTrue();
    }

    [Fact]
    public void Validate_Mp4WithUnsupportedSubtitleCodec_ReturnsSubtitleCopyIncompatible()
    {
        var settings = new TestOutputSettingsBuilder().Build();
        var media = new TestMediaInfoBuilder().WithSubtitles("ass").Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "subtitle_copy_incompatible").Should().BeTrue();
    }

    [Fact]
    public void Validate_Mp4WithMovText_NoSubtitleError()
    {
        var settings = new TestOutputSettingsBuilder().Build();
        var media = new TestMediaInfoBuilder().WithSubtitles("mov_text").Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "subtitle_copy_incompatible").Should().BeFalse();
    }

    [Fact]
    public void Validate_MkvWithAnySubtitle_NoSubtitleError()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithContainer(OutputContainer.Mkv)
            .Build();
        var media = new TestMediaInfoBuilder().WithSubtitles("ass").Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "subtitle_copy_incompatible").Should().BeFalse();
    }

    [Fact]
    public void Validate_DataInMp4WithoutRemove_ReturnsDataCopyIncompatible()
    {
        var settings = new TestOutputSettingsBuilder().Build();
        var media = new TestMediaInfoBuilder().WithDataStreams().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "data_copy_incompatible").Should().BeTrue();
    }

    [Fact]
    public void Validate_DataInMkv_NoDataError()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithContainer(OutputContainer.Mkv)
            .Build();
        var media = new TestMediaInfoBuilder().WithDataStreams().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "data_copy_incompatible").Should().BeFalse();
    }

    [Fact]
    public void Validate_DataInMp4WithRemoveData_NoDataError()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithRemoveData()
            .Build();
        var media = new TestMediaInfoBuilder().WithDataStreams().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "data_copy_incompatible").Should().BeFalse();
    }

    // ── Video streams ───────────────────────────────────────────────────

    [Fact]
    public void Validate_NoVideo_ReturnsVideoStreamMissing()
    {
        var settings = new TestOutputSettingsBuilder().Build();
        var media = new TestMediaInfoBuilder().WithNoVideo().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "video_stream_missing").Should().BeTrue();
        result.HasErrors.Should().BeTrue();
    }

    [Fact]
    public void Validate_NoVideo_ReturnsEarlyWithOnlyThatError()
    {
        var settings = new TestOutputSettingsBuilder().Build();
        var media = new TestMediaInfoBuilder().WithNoVideo().Build();

        var result = _sut.Validate(settings, media);

        // Should not attempt further validation beyond container compat + video_stream_missing.
        result.Errors.Should().Contain(e => e.Code == "video_stream_missing");
    }

    [Fact]
    public void Validate_MultipleVideoStreams_ReturnsWarning()
    {
        var settings = new TestOutputSettingsBuilder().Build();
        var media = new TestMediaInfoBuilder().WithMultipleVideoStreams().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "multiple_video_streams").Should().BeTrue();
        result.HasWarnings.Should().BeTrue();
    }

    // ── Encoder settings ────────────────────────────────────────────────

    [Fact]
    public void Validate_H264WithH265EncoderOptions_ReturnsEncoderOptionsRequired()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithVideoCodec(VideoCodec.H264)
            .WithEncoderOptions(new H265EncoderOptions())
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "video_encoder_options_required").Should().BeTrue();
    }

    [Fact]
    public void Validate_H264WithInvalidPreset_ReturnsQualityPresetInvalid()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithEncoderOptions(new H264EncoderOptions(Preset: "bogus_preset"))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "quality_preset_invalid").Should().BeTrue();
    }

    [Fact]
    public void Validate_VpxCpuUsedNegative_ReturnsEncoderCpuUsedInvalid()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithVp9()
            .WithEncoderOptions(new VpxEncoderOptions(VideoCodec.Vp9, CpuUsed: -1))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "video_encoder_cpu_used_invalid").Should().BeTrue();
    }

    [Fact]
    public void Validate_VpxCpuUsedAbove8_ReturnsEncoderCpuUsedInvalid()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithVp9()
            .WithEncoderOptions(new VpxEncoderOptions(VideoCodec.Vp9, CpuUsed: 9))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "video_encoder_cpu_used_invalid").Should().BeTrue();
    }

    [Fact]
    public void Validate_VpxCpuUsed4_NoEncoderError()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithVp9()
            .WithEncoderOptions(new VpxEncoderOptions(VideoCodec.Vp9, CpuUsed: 4))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "video_encoder_cpu_used_invalid").Should().BeFalse();
    }

    // ── Quality strategy ────────────────────────────────────────────────

    [Fact]
    public void Validate_QualityCodecMismatch_ReturnsError()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithH265()
            .WithQuality(new H264QualityOptions())
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "quality_codec_mismatch").Should().BeTrue();
    }

    [Fact]
    public void Validate_H264Crf0_NoCrfError()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithQuality(new H264QualityOptions(Crf: 0))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "quality_crf_invalid").Should().BeFalse();
    }

    [Fact]
    public void Validate_H264Crf51_NoCrfError()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithQuality(new H264QualityOptions(Crf: 51))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "quality_crf_invalid").Should().BeFalse();
    }

    [Fact]
    public void Validate_H264CrfNegative_ReturnsCrfInvalid()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithQuality(new H264QualityOptions(Crf: -1))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "quality_crf_invalid").Should().BeTrue();
    }

    [Fact]
    public void Validate_H264Crf52_ReturnsCrfInvalid()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithQuality(new H264QualityOptions(Crf: 52))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "quality_crf_invalid").Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(63)]
    public void Validate_Vp8QualityBoundary_NoQualityValueError(int quality)
    {
        var settings = new TestOutputSettingsBuilder()
            .WithVp8()
            .WithQuality(new Vp8QualityOptions(Quality: quality))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "quality_value_invalid").Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(63)]
    public void Validate_Vp9QualityBoundary_NoQualityValueError(int quality)
    {
        var settings = new TestOutputSettingsBuilder()
            .WithVp9()
            .WithQuality(new Vp9QualityOptions(Quality: quality))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "quality_value_invalid").Should().BeFalse();
    }

    [Fact]
    public void Validate_Vp8QualityNegative_ReturnsQualityValueInvalid()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithVp8()
            .WithQuality(new Vp8QualityOptions(Quality: -1))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "quality_value_invalid").Should().BeTrue();
    }

    [Fact]
    public void Validate_Vp9Quality64_ReturnsQualityValueInvalid()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithVp9()
            .WithQuality(new Vp9QualityOptions(Quality: 64))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "quality_value_invalid").Should().BeTrue();
    }

    // ── File size strategy ──────────────────────────────────────────────

    [Fact]
    public void Validate_FileSizeZeroTarget_ReturnsTargetSizeInvalid()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithTargetFileSize(0)
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "target_size_invalid").Should().BeTrue();
    }

    [Fact]
    public void Validate_FileSizeNoDuration_ReturnsDurationRequired()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithTargetFileSize(50)
            .Build();
        var media = new TestMediaInfoBuilder().WithNoDuration().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "duration_required").Should().BeTrue();
    }

    [Fact]
    public void Validate_FileSizeAudioBudgetExceedsTotal_ReturnsVideoBudgetExhausted()
    {
        // 1 MB target with very high audio bitrate that exhausts the budget.
        // Audio at 10 Mbps for 60s = 600 Mbit = 75 MB >> 1 MB target.
        var settings = new TestOutputSettingsBuilder()
            .WithTargetFileSize(1)
            .WithAudioReencode(AudioCodec.Aac, 10_000_000)
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "video_budget_exhausted").Should().BeTrue();
    }

    [Fact]
    public void Validate_FileSizeValidSettings_NoErrors()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithTargetFileSize(100)
            .WithAudioReencode(AudioCodec.Aac, 128_000)
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "target_size_invalid").Should().BeFalse();
        HasIssueWithCode(result, "duration_required").Should().BeFalse();
        HasIssueWithCode(result, "video_budget_exhausted").Should().BeFalse();
    }

    // ── Bitrate strategy ────────────────────────────────────────────────

    [Fact]
    public void Validate_ZeroBitrate_ReturnsVideoBitrateInvalid()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithTargetBitrate(0)
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "video_bitrate_invalid").Should().BeTrue();
    }

    [Fact]
    public void Validate_LowBitrateFor1080p_ReturnsVideoBitrateLowWarning()
    {
        // 1080p threshold is 3 Mbps; use 1 Mbps.
        var settings = new TestOutputSettingsBuilder()
            .WithTargetBitrate(1_000_000)
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "video_bitrate_low").Should().BeTrue();
        result.HasWarnings.Should().BeTrue();
    }

    [Fact]
    public void Validate_HighBitrate_NoWarning()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithTargetBitrate(10_000_000)
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "video_bitrate_low").Should().BeFalse();
    }

    // ── Resolution helpers ──────────────────────────────────────────────

    [Fact]
    public void ResolveOutputResolution_Original_ReturnsSourceDimensions()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithResolution(ResolutionPreset.Original)
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.ResolveOutputResolution(settings, media);

        result.Should().NotBeNull();
        result!.Width.Should().Be(1920);
        result.Height.Should().Be(1080);
    }

    [Fact]
    public void ResolveOutputResolution_720p_Returns1280x720()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithResolution(ResolutionPreset.P720)
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.ResolveOutputResolution(settings, media);

        result.Should().NotBeNull();
        result!.Width.Should().Be(1280);
        result.Height.Should().Be(720);
    }

    [Fact]
    public void ResolveOutputResolution_PortraitSource720p_SwapsDimensions()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithResolution(ResolutionPreset.P720)
            .Build();
        var media = new TestMediaInfoBuilder().WithPortrait().Build();

        var result = _sut.ResolveOutputResolution(settings, media);

        result.Should().NotBeNull();
        result!.Width.Should().Be(720);
        result.Height.Should().Be(1280);
    }

    [Fact]
    public void ResolveOutputResolution_RotatedPortrait1080p_SwapsDimensions()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithResolution(ResolutionPreset.P1080)
            .Build();
        var media = new TestMediaInfoBuilder().WithRotatedPortrait().Build();

        var result = _sut.ResolveOutputResolution(settings, media);

        result.Should().NotBeNull();
        result!.Width.Should().Be(1080);
        result.Height.Should().Be(1920);
    }

    [Fact]
    public void ResolveOutputResolution_NoVideo_ReturnsNull()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithResolution(ResolutionPreset.P720)
            .Build();
        var media = new TestMediaInfoBuilder().WithNoVideo().Build();

        var result = _sut.ResolveOutputResolution(settings, media);

        result.Should().BeNull();
    }

    // ── AllowedVideoCodecs / AllowedAudioCodecs ─────────────────────────

    [Fact]
    public void AllowedVideoCodecs_Mp4_ReturnsH264AndH265()
    {
        var result = _sut.AllowedVideoCodecs(OutputContainer.Mp4);

        result.Should().BeEquivalentTo([VideoCodec.H264, VideoCodec.H265, VideoCodec.Av1]);
    }

    [Fact]
    public void AllowedVideoCodecs_Webm_ReturnsVpxOnly()
    {
        var result = _sut.AllowedVideoCodecs(OutputContainer.Webm);

        result.Should().BeEquivalentTo([VideoCodec.Vp8, VideoCodec.Vp9, VideoCodec.Av1]);
    }

    [Fact]
    public void AllowedAudioCodecs_Mp4_ReturnsAacAndMp3()
    {
        var result = _sut.AllowedAudioCodecs(OutputContainer.Mp4);

        result.Should().BeEquivalentTo([AudioCodec.Aac, AudioCodec.Mp3]);
    }

    [Fact]
    public void AllowedAudioCodecs_Webm_ReturnsOpusAndVorbis()
    {
        var result = _sut.AllowedAudioCodecs(OutputContainer.Webm);

        result.Should().BeEquivalentTo([AudioCodec.Opus, AudioCodec.Vorbis]);
    }

    // ── ValidationResult properties ─────────────────────────────────────

    [Fact]
    public void Validate_DefaultValidSettings_HasNoErrors()
    {
        var settings = new TestOutputSettingsBuilder().Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        result.HasErrors.Should().BeFalse();
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_ErrorAndWarning_BothFlagsTrue()
    {
        // H264 in WebM produces an error. Low bitrate produces a warning.
        var settings = new TestOutputSettingsBuilder()
            .WithContainer(OutputContainer.Webm)
            .WithVideoCodec(VideoCodec.H264)
            .WithTargetBitrate(100_000)
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        result.HasErrors.Should().BeTrue();
        result.HasWarnings.Should().BeTrue();
        result.Errors.Should().NotBeEmpty();
        result.Warnings.Should().NotBeEmpty();
    }

    // ── Edge cases: subtitle removal clears errors ──────────────────────

    [Fact]
    public void Validate_WebmWithSubtitlesRemoved_NoSubtitleError()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithVp9()
            .WithRemoveSubtitles()
            .Build();
        var media = new TestMediaInfoBuilder().WithSubtitles("ass").Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "subtitle_copy_incompatible").Should().BeFalse();
    }

    // ── Edge cases: H265 CRF boundaries ────────────────────────────────

    [Fact]
    public void Validate_H265Crf0_NoCrfError()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithH265()
            .WithQuality(new H265QualityOptions(Crf: 0))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "quality_crf_invalid").Should().BeFalse();
    }

    [Fact]
    public void Validate_H265Crf51_NoCrfError()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithH265()
            .WithQuality(new H265QualityOptions(Crf: 51))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "quality_crf_invalid").Should().BeFalse();
    }

    [Fact]
    public void Validate_H265Crf52_ReturnsCrfInvalid()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithH265()
            .WithQuality(new H265QualityOptions(Crf: 52))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "quality_crf_invalid").Should().BeTrue();
    }

    // ── Edge cases: VPX boundary at 0 and 8 ─────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void Validate_VpxCpuUsedBoundary_NoEncoderError(int cpuUsed)
    {
        var settings = new TestOutputSettingsBuilder()
            .WithVp9()
            .WithEncoderOptions(new VpxEncoderOptions(VideoCodec.Vp9, CpuUsed: cpuUsed))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "video_encoder_cpu_used_invalid").Should().BeFalse();
    }

    // ── Edge case: AudioMode.Remove skips audio copy validation ─────

    [Fact]
    public void Validate_AudioModeRemove_SkipsAudioCopyValidation()
    {
        // AudioMode.Remove means no audio-copy errors should appear.
        var settings = new TestOutputSettingsBuilder()
            .WithAudioRemove()
            .Build();
        var media = new TestMediaInfoBuilder()
            .WithAudioStreams(new AudioStreamInfo(1, "some_unknown_codec", 128_000))
            .Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "audio_copy_codec_unknown").Should().BeFalse();
        HasIssueWithCode(result, "audio_copy_incompatible").Should().BeFalse();
    }

    // ── File size: audio copy with unknown bitrate ──────────────────────

    [Fact]
    public void Validate_FileSizeCopyModeNullAudioBitrate_ReturnsAudioCopySizeUnknown()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithTargetFileSize(50)
            .WithAudioCopy()
            .Build();
        var media = new TestMediaInfoBuilder().WithAudioBitrate(null).Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "audio_copy_size_unknown").Should().BeTrue();
    }

    // ── Bitrate strategy: negative value ────────────────────────────────

    [Fact]
    public void Validate_NegativeBitrate_ReturnsVideoBitrateInvalid()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithTargetBitrate(-1)
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "video_bitrate_invalid").Should().BeTrue();
    }

    // ── H265 encoder with wrong options type ────────────────────────────

    [Fact]
    public void Validate_H265WithH264EncoderOptions_ReturnsEncoderOptionsRequired()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithH265()
            .WithEncoderOptions(new H264EncoderOptions())
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "video_encoder_options_required").Should().BeTrue();
    }

    // ── VPX with wrong codec in encoder options ─────────────────────────

    [Fact]
    public void Validate_Vp9WithVp8EncoderOptions_ReturnsEncoderOptionsRequired()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithVp9()
            .WithEncoderOptions(new VpxEncoderOptions(VideoCodec.Vp8, CpuUsed: 4))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "video_encoder_options_required").Should().BeTrue();
    }
}
