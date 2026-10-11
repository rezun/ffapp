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

    [Theory]
    [InlineData(OutputContainer.Mp4, VideoCodec.H264)]
    [InlineData(OutputContainer.Mp4, VideoCodec.H265)]
    [InlineData(OutputContainer.Mp4, VideoCodec.Vp9)]
    [InlineData(OutputContainer.Mkv, VideoCodec.H264)]
    [InlineData(OutputContainer.Mkv, VideoCodec.H265)]
    [InlineData(OutputContainer.Mkv, VideoCodec.Vp9)]
    [InlineData(OutputContainer.Webm, VideoCodec.Vp9)]
    [InlineData(OutputContainer.Ogg, VideoCodec.Vp9)]
    public void Validate_ValidContainerCodecCombo_NoVideoCodecError(OutputContainer container, VideoCodec codec)
    {
        var builder = new TestOutputSettingsBuilder()
            .WithContainer(container)
            .WithVideoCodec(codec);

        if (codec is VideoCodec.Vp9)
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
    public void Validate_VorbisReencodeInMp4_ReturnsAudioCodecIncompatible()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithAudioReencode(AudioCodec.Vorbis, 128_000)
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "audio_codec_incompatible").Should().BeTrue();
    }

    // ── Audio validation ────────────────────────────────────────────────

    [Theory]
    [InlineData(AudioCodec.Opus, 44100)]
    [InlineData(AudioCodec.Opus, 22050)]
    [InlineData(AudioCodec.Opus, 96000)]
    [InlineData(AudioCodec.Mp3, 96000)]
    [InlineData(AudioCodec.Aac, 12345)]
    [InlineData(AudioCodec.Aac, 0)]
    [InlineData(AudioCodec.Vorbis, -1)]
    [InlineData(AudioCodec.Vorbis, 200001)]
    public void Validate_UnsupportedAudioSampleRate_ReturnsFieldError(AudioCodec codec, int rate)
    {
        var settings = new TestOutputSettingsBuilder().WithContainer(OutputContainer.Mkv)
            .WithAudioReencode(codec, 128_000, sampleRate: rate).Build();

        var result = _sut.Validate(settings, new TestMediaInfoBuilder().Build());

        var issue = result.Errors.Should().ContainSingle(issue => issue.Code == "audio_sample_rate_unsupported").Subject;
        issue.FieldName.Should().Be("audio_sample_rate");
        issue.Message.Should().Contain(rate.ToString()).And.Contain(codec.ToString()).And.Contain("Auto");
    }

    [Theory]
    [InlineData(AudioCodec.Opus, 8000)]
    [InlineData(AudioCodec.Opus, 12000)]
    [InlineData(AudioCodec.Opus, 16000)]
    [InlineData(AudioCodec.Opus, 24000)]
    [InlineData(AudioCodec.Opus, 48000)]
    [InlineData(AudioCodec.Mp3, 44100)]
    [InlineData(AudioCodec.Aac, 96000)]
    [InlineData(AudioCodec.Vorbis, 12345)]
    [InlineData(AudioCodec.Vorbis, 200000)]
    [InlineData(AudioCodec.Opus, null)]
    public void Validate_SupportedOrAutomaticAudioSampleRate_NoRateError(AudioCodec codec, int? rate)
    {
        var settings = new TestOutputSettingsBuilder().WithContainer(OutputContainer.Mkv)
            .WithAudioReencode(codec, 128_000, sampleRate: rate).Build();

        var result = _sut.Validate(settings, new TestMediaInfoBuilder().Build());

        HasIssueWithCode(result, "audio_sample_rate_unsupported").Should().BeFalse();
    }

    [Theory]
    [InlineData(AudioMode.Copy)]
    [InlineData(AudioMode.Remove)]
    public void Validate_NonReencodeMode_DoesNotCheckEncoderSampleRates(AudioMode mode)
    {
        var settings = new TestOutputSettingsBuilder()
            .WithAudio(new AudioOptions(mode, AudioCodec.Opus, SampleRate: 44100)).Build();

        var result = _sut.Validate(settings, new TestMediaInfoBuilder().Build());

        HasIssueWithCode(result, "audio_sample_rate_unsupported").Should().BeFalse();
    }

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
        // Vorbis source copied into MP4 is not allowed.
        var settings = new TestOutputSettingsBuilder()
            .WithContainer(OutputContainer.Mp4)
            .WithAudioCopy()
            .Build();
        var media = new TestMediaInfoBuilder()
            .WithAudioStreams(new AudioStreamInfo(1, "vorbis", 128_000))
            .Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "audio_copy_incompatible").Should().BeTrue();
    }

    [Fact]
    public void Validate_CopyModeWithMp4Ac3_NoAudioCopyError()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithContainer(OutputContainer.Mp4)
            .WithAudioCopy()
            .Build();
        var media = new TestMediaInfoBuilder()
            .WithAudioStreams(new AudioStreamInfo(1, "ac3", 384_000))
            .Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "audio_copy_codec_unknown").Should().BeFalse();
        HasIssueWithCode(result, "audio_copy_incompatible").Should().BeFalse();
    }

    [Fact]
    public void Validate_AudioOnlyMkaCopyWithUnknownSourceCodec_NoAudioCopyError()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithContainer(OutputContainer.Mka)
            .WithVideoMode(VideoMode.Remove)
            .WithAudioCopy()
            .Build();
        var media = new TestMediaInfoBuilder()
            .WithAudioStreams(new AudioStreamInfo(1, "some_unknown_codec", 128_000))
            .Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "audio_copy_codec_unknown").Should().BeFalse();
    }

    [Fact]
    public void Validate_AudioOnlyM4aCopyWithUnknownSourceCodec_ReturnsAudioCopyCodecUnknown()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithContainer(OutputContainer.M4a)
            .WithVideoMode(VideoMode.Remove)
            .WithAudioCopy()
            .Build();
        var media = new TestMediaInfoBuilder()
            .WithAudioStreams(new AudioStreamInfo(1, "some_unknown_codec", 128_000))
            .Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "audio_copy_codec_unknown").Should().BeTrue();
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

    [Fact]
    public void Validate_AudioChannelsWithRemoveMode_NoChannelsError()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithAudio(new AudioOptions(AudioMode.Remove, AudioChannels: 2))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "audio_channels_requires_reencode").Should().BeFalse();
    }

    [Fact]
    public void Validate_SampleRateWithRemoveMode_NoSampleRateError()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithAudio(new AudioOptions(AudioMode.Remove, SampleRate: 44100))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "audio_sample_rate_requires_reencode").Should().BeFalse();
    }

    // ── Subtitle / data validation ──────────────────────────────────────

    [Fact]
    public void Validate_WebmWithTextSubtitles_NoSubtitleError()
    {
        var settings = new TestOutputSettingsBuilder().WithVp9().Build();
        var media = new TestMediaInfoBuilder().WithSubtitles("subrip").Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "subtitle_copy_incompatible").Should().BeFalse();
    }

    [Fact]
    public void Validate_OggWithSubtitlesNotRemoved_ReturnsSubtitleCopyIncompatible()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithVp9()
            .WithContainer(OutputContainer.Ogg)
            .Build();
        var media = new TestMediaInfoBuilder().WithSubtitles("subrip").Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "subtitle_copy_incompatible").Should().BeTrue();
    }

    [Fact]
    public void Validate_Mp4WithUnsupportedSubtitleCodec_ReturnsSubtitleCopyIncompatible()
    {
        var settings = new TestOutputSettingsBuilder().Build();
        var media = new TestMediaInfoBuilder().WithSubtitles("hdmv_pgs_subtitle").Build();

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

        HasIssueWithCode(result, "data_streams_not_preserved").Should().BeTrue();
        result.Warnings.Should().Contain(i => i.Code == "data_streams_not_preserved");
        result.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Validate_DataInMkv_NoDataError()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithContainer(OutputContainer.Mkv)
            .Build();
        var media = new TestMediaInfoBuilder().WithDataStreams().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "data_streams_not_preserved").Should().BeFalse();
    }

    [Fact]
    public void Validate_DataInMp4WithRemoveData_NoDataError()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithRemoveData()
            .Build();
        var media = new TestMediaInfoBuilder().WithDataStreams().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "data_streams_not_preserved").Should().BeFalse();
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
            .WithEncoderOptions(H26xEncoderOptions.ForCodec(VideoCodec.H265))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "video_encoder_options_required").Should().BeTrue();
    }

    [Fact]
    public void Validate_H264WithInvalidPreset_ReturnsQualityPresetInvalid()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithEncoderOptions(new H26xEncoderOptions(VideoCodec.H264, Preset: "bogus_preset"))
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
            .WithQuality(H26xQualityOptions.ForCodec(VideoCodec.H264))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "quality_codec_mismatch").Should().BeTrue();
    }

    [Fact]
    public void Validate_H264Crf0_NoCrfError()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithQuality(new H26xQualityOptions(VideoCodec.H264, Crf: 0))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "quality_crf_invalid").Should().BeFalse();
    }

    [Fact]
    public void Validate_H264Crf51_NoCrfError()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithQuality(new H26xQualityOptions(VideoCodec.H264, Crf: 51))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "quality_crf_invalid").Should().BeFalse();
    }

    [Fact]
    public void Validate_H264CrfNegative_ReturnsCrfInvalid()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithQuality(new H26xQualityOptions(VideoCodec.H264, Crf: -1))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "quality_crf_invalid").Should().BeTrue();
    }

    [Fact]
    public void Validate_H264Crf52_ReturnsCrfInvalid()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithQuality(new H26xQualityOptions(VideoCodec.H264, Crf: 52))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "quality_crf_invalid").Should().BeTrue();
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
    public void Validate_Vp9QualityNegative_ReturnsQualityValueInvalid()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithVp9()
            .WithQuality(new Vp9QualityOptions(Quality: -1))
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
    public void AllowedVideoCodecs_Mp4_ReturnsExpectedCodecs()
    {
        var result = _sut.AllowedVideoCodecs(OutputContainer.Mp4);

        result.Should().BeEquivalentTo([VideoCodec.H264, VideoCodec.H265, VideoCodec.Av1, VideoCodec.Vp9]);
    }

    [Fact]
    public void AllowedVideoCodecs_Webm_ReturnsVpxOnly()
    {
        var result = _sut.AllowedVideoCodecs(OutputContainer.Webm);

        result.Should().BeEquivalentTo([VideoCodec.Vp9, VideoCodec.Av1]);
    }

    [Fact]
    public void AllowedAudioCodecs_Mp4_ReturnsAacMp3AndOpus()
    {
        var result = _sut.AllowedAudioCodecs(OutputContainer.Mp4);

        result.Should().BeEquivalentTo([AudioCodec.Aac, AudioCodec.Mp3, AudioCodec.Opus]);
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
            .WithQuality(new H26xQualityOptions(VideoCodec.H265, Crf: 0))
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
            .WithQuality(new H26xQualityOptions(VideoCodec.H265, Crf: 51))
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
            .WithQuality(new H26xQualityOptions(VideoCodec.H265, Crf: 52))
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
            .WithEncoderOptions(H26xEncoderOptions.ForCodec(VideoCodec.H264))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "video_encoder_options_required").Should().BeTrue();
    }

    // ── VPX with wrong codec in encoder options ─────────────────────────


    // ── HDR validation ─────────────────────────────────────────────────

    [Fact]
    public void Validate_HdrSourceH264Preserve_ReturnsHdrCodecIncompatible()
    {
        var settings = new TestOutputSettingsBuilder().WithHdrPreserve().Build();
        var media = new TestMediaInfoBuilder().WithHdr10().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "hdr_codec_incompatible").Should().BeTrue();
    }

    [Fact]
    public void Validate_HdrSourceVp9Preserve_ReturnsHdrCodecIncompatible()
    {
        var settings = new TestOutputSettingsBuilder().WithVp9().WithHdrPreserve().Build();
        var media = new TestMediaInfoBuilder().WithHdr10().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "hdr_codec_incompatible").Should().BeTrue();
    }

    [Fact]
    public void Validate_HdrSourceH265Preserve_NoHdrCodecError()
    {
        var settings = new TestOutputSettingsBuilder().WithH265().WithHdrPreserve().Build();
        var media = new TestMediaInfoBuilder().WithHdr10().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "hdr_codec_incompatible").Should().BeFalse();
    }

    [Fact]
    public void Validate_HdrSourceAv1Preserve_NoHdrCodecError()
    {
        var settings = new TestOutputSettingsBuilder().WithAv1().WithHdrPreserve().Build();
        var media = new TestMediaInfoBuilder().WithHdr10().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "hdr_codec_incompatible").Should().BeFalse();
    }

    [Fact]
    public void Validate_HdrSourceH265PreserveMainProfile_ReturnsProfileOverrideWarning()
    {
        var settings = new TestOutputSettingsBuilder().WithH265().WithHdrPreserve().Build();
        var media = new TestMediaInfoBuilder().WithHdr10().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "hdr_profile_override").Should().BeTrue();
    }

    [Fact]
    public void Validate_HdrSourceH265PreserveMain10Profile_NoProfileWarning()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithH265()
            .WithHdrPreserve()
            .WithEncoderOptions(new H26xEncoderOptions(VideoCodec.H265, Profile: "main10"))
            .WithQuality(new H26xQualityOptions(VideoCodec.H265, Profile: "main10"))
            .Build();
        var media = new TestMediaInfoBuilder().WithHdr10().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "hdr_profile_override").Should().BeFalse();
    }

    [Fact]
    public void Validate_SdrSourceTonemap_ReturnsSdrTonemapWarning()
    {
        var settings = new TestOutputSettingsBuilder().WithToneMapToSdr().Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "tonemap_sdr_source").Should().BeTrue();
    }

    [Fact]
    public void Validate_SdrSourcePreserve_NoHdrIssues()
    {
        var settings = new TestOutputSettingsBuilder().WithHdrPreserve().Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "hdr_codec_incompatible").Should().BeFalse();
        HasIssueWithCode(result, "hdr_profile_override").Should().BeFalse();
        HasIssueWithCode(result, "tonemap_sdr_source").Should().BeFalse();
    }

    [Fact]
    public void Validate_HdrSourceH264ToneMap_NoHdrCodecError()
    {
        var settings = new TestOutputSettingsBuilder().WithToneMapToSdr().Build();
        var media = new TestMediaInfoBuilder().WithHdr10().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "hdr_codec_incompatible").Should().BeFalse();
    }
}
