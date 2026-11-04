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

    // Settings with encoder and quality options that match the codec.
    private static TestOutputSettingsBuilder SettingsFor(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => new TestOutputSettingsBuilder(),
        VideoCodec.H265 => new TestOutputSettingsBuilder().WithH265(),
        VideoCodec.Vp9 => new TestOutputSettingsBuilder().WithVp9(),
        VideoCodec.Av1 => new TestOutputSettingsBuilder().WithAv1(),
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, null),
    };

    // ── Container / codec compatibility ─────────────────────────────────

    [Theory]
    [InlineData(OutputContainer.Webm, VideoCodec.H264, true)]
    [InlineData(OutputContainer.Mkv, VideoCodec.H265, false)]
    [InlineData(OutputContainer.Ogg, VideoCodec.Vp9, false)]
    public void Validate_ContainerCodecCombo_FlagsOnlyUnsupportedPairs(
        OutputContainer container, VideoCodec codec, bool expected)
    {
        var settings = SettingsFor(codec).WithContainer(container).Build();

        var result = _sut.Validate(settings, new TestMediaInfoBuilder().Build());

        result.Errors.Any(i => i.Code == "video_codec_incompatible").Should().Be(expected);
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
    [InlineData(AudioCodec.Aac, 12345)]
    [InlineData(AudioCodec.Vorbis, 0)]
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
    [InlineData(AudioCodec.Vorbis, 1)]
    [InlineData(AudioCodec.Vorbis, 200000)]
    [InlineData(AudioCodec.Opus, null)]
    public void Validate_SupportedOrAutomaticAudioSampleRate_NoRateError(AudioCodec codec, int? rate)
    {
        var settings = new TestOutputSettingsBuilder().WithContainer(OutputContainer.Mkv)
            .WithAudioReencode(codec, 128_000, sampleRate: rate).Build();

        var result = _sut.Validate(settings, new TestMediaInfoBuilder().Build());

        HasIssueWithCode(result, "audio_sample_rate_unsupported").Should().BeFalse();
    }

    [Fact]
    public void Validate_EveryOfferedAudioSampleRate_NoRateError()
    {
        var media = new TestMediaInfoBuilder().Build();

        var rejected = CompatibilityData.AudioSampleRateOptions
            .SelectMany(entry => entry.Value.Select(rate => (Codec: entry.Key, Rate: rate)))
            .Where(option => HasIssueWithCode(
                _sut.Validate(
                    new TestOutputSettingsBuilder().WithContainer(OutputContainer.Mkv)
                        .WithAudioReencode(option.Codec, 128_000, sampleRate: option.Rate).Build(),
                    media),
                "audio_sample_rate_unsupported"));

        rejected.Should().BeEmpty();
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

    [Theory]
    [InlineData(AudioMode.Copy, true)]
    [InlineData(AudioMode.Reencode, false)]
    [InlineData(AudioMode.Remove, false)]
    public void Validate_ChannelsOrSampleRateChange_RequiresReencodeOnlyForCopy(AudioMode mode, bool expected)
    {
        var settings = new TestOutputSettingsBuilder()
            .WithAudio(new AudioOptions(mode, AudioCodec.Aac, 128_000, AudioChannels: 2, SampleRate: 48000))
            .Build();

        var result = _sut.Validate(settings, new TestMediaInfoBuilder().Build());

        HasIssueWithCode(result, "audio_channels_requires_reencode").Should().Be(expected);
        HasIssueWithCode(result, "audio_sample_rate_requires_reencode").Should().Be(expected);
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

    [Theory]
    [InlineData(OutputContainer.Mp4, "some_unknown_codec", "audio_copy_codec_unknown")]
    [InlineData(OutputContainer.Mp4, "vorbis", "audio_copy_incompatible")]
    [InlineData(OutputContainer.Mp4, "ac3", null)]
    [InlineData(OutputContainer.Mkv, "some_unknown_codec", null)]
    public void Validate_AudioCopy_ChecksSourceCodecAgainstContainer(
        OutputContainer container, string codecName, string? expectedCode)
    {
        string[] copyCodes = ["audio_copy_codec_unknown", "audio_copy_incompatible"];
        var settings = new TestOutputSettingsBuilder()
            .WithContainer(container)
            .WithAudioCopy()
            .Build();
        var media = new TestMediaInfoBuilder()
            .WithAudioStreams(new AudioStreamInfo(1, codecName, 128_000))
            .Build();

        var result = _sut.Validate(settings, media);

        result.Errors.Select(i => i.Code).Intersect(copyCodes)
            .Should().Equal(expectedCode is null ? [] : [expectedCode]);
    }

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

    // ── Subtitle / data validation ──────────────────────────────────────

    [Theory]
    [InlineData(OutputContainer.Mp4, "mov_text", false, false)]
    [InlineData(OutputContainer.Mp4, "hdmv_pgs_subtitle", false, true)]
    [InlineData(OutputContainer.Webm, "subrip", false, false)]
    [InlineData(OutputContainer.Webm, "ass", true, false)]
    [InlineData(OutputContainer.Ogg, "subrip", false, true)]
    [InlineData(OutputContainer.Mkv, "ass", false, false)]
    public void Validate_SubtitleCopy_FlagsStreamsTheContainerCannotKeep(
        OutputContainer container, string codecName, bool removeSubtitles, bool expected)
    {
        var builder = SettingsFor(VideoCodec.Vp9).WithContainer(container);
        if (removeSubtitles)
            builder = builder.WithRemoveSubtitles();
        var media = new TestMediaInfoBuilder().WithSubtitles(codecName).Build();

        var result = _sut.Validate(builder.Build(), media);

        HasIssueWithCode(result, "subtitle_copy_incompatible").Should().Be(expected);
    }

    [Theory]
    [InlineData(OutputContainer.Mp4, false, true)]
    [InlineData(OutputContainer.Mp4, true, false)]
    [InlineData(OutputContainer.Mkv, false, false)]
    public void Validate_DataStreams_WarnsWhenContainerDropsThem(
        OutputContainer container, bool removeData, bool expected)
    {
        var builder = new TestOutputSettingsBuilder().WithContainer(container);
        if (removeData)
            builder = builder.WithRemoveData();
        var media = new TestMediaInfoBuilder().WithDataStreams().Build();

        var result = _sut.Validate(builder.Build(), media);

        result.Warnings.Any(i => i.Code == "data_streams_not_preserved").Should().Be(expected);
        result.HasErrors.Should().BeFalse();
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
    public void Validate_MultipleVideoStreams_ReturnsWarning()
    {
        var settings = new TestOutputSettingsBuilder().Build();
        var media = new TestMediaInfoBuilder().WithMultipleVideoStreams().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "multiple_video_streams").Should().BeTrue();
        result.HasWarnings.Should().BeTrue();
    }

    // ── Encoder settings ────────────────────────────────────────────────

    [Theory]
    [InlineData(VideoCodec.H264, VideoCodec.H265)]
    [InlineData(VideoCodec.H265, VideoCodec.H264)]
    [InlineData(VideoCodec.Vp9, VideoCodec.H264)]
    public void Validate_EncoderOptionsForOtherCodec_ReturnsEncoderOptionsRequired(
        VideoCodec codec, VideoCodec optionsCodec)
    {
        var settings = SettingsFor(codec)
            .WithEncoderOptions(H26xEncoderOptions.ForCodec(optionsCodec))
            .Build();

        var result = _sut.Validate(settings, new TestMediaInfoBuilder().Build());

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

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(8, false)]
    [InlineData(9, true)]
    public void Validate_VpxCpuUsed_AcceptsOnlyZeroToEight(int cpuUsed, bool expected)
    {
        var settings = new TestOutputSettingsBuilder()
            .WithVp9()
            .WithEncoderOptions(new VpxEncoderOptions(VideoCodec.Vp9, CpuUsed: cpuUsed))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "video_encoder_cpu_used_invalid").Should().Be(expected);
    }

    [Theory]
    [InlineData(EncodingStrategy.TargetQuality, -1, 0, 1, "av1_film_grain_invalid")]
    [InlineData(EncodingStrategy.TargetQuality, 51, 0, 1, "av1_film_grain_invalid")]
    [InlineData(EncodingStrategy.TargetQuality, 8, 2, 1, "av1_film_grain_denoise_invalid")]
    [InlineData(EncodingStrategy.TargetQuality, 8, 0, -1, "av1_adaptive_film_grain_invalid")]
    [InlineData(EncodingStrategy.TargetBitrate, 51, 0, 1, "av1_film_grain_invalid")]
    public void Validate_InvalidAv1FilmGrain_ReturnsError(
        EncodingStrategy strategy, int strength, int denoise, int adaptive, string code)
    {
        var builder = new TestOutputSettingsBuilder().WithAv1();
        if (strategy == EncodingStrategy.TargetQuality)
            builder.WithQuality(new Av1QualityOptions(
                FilmGrain: strength, FilmGrainDenoise: denoise, AdaptiveFilmGrain: adaptive));
        else
            builder.WithEncoderOptions(new Av1EncoderOptions(
                FilmGrain: strength, FilmGrainDenoise: denoise, AdaptiveFilmGrain: adaptive));
        var settings = builder.WithStrategy(strategy).Build();

        var result = _sut.Validate(settings, new TestMediaInfoBuilder().Build());

        HasIssueWithCode(result, code).Should().BeTrue();
        result.HasErrors.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(1, 1, 0)]
    [InlineData(50, 1, 1)]
    public void Validate_ValidAv1FilmGrain_AcceptsBoundaries(int strength, int denoise, int adaptive)
    {
        var settings = new TestOutputSettingsBuilder().WithAv1()
            .WithAv1FilmGrain(strength, denoise, adaptive).Build();

        var result = _sut.Validate(settings, new TestMediaInfoBuilder().Build());

        result.HasErrors.Should().BeFalse();
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

    [Theory]
    [InlineData(VideoCodec.H264, -1, true)]
    [InlineData(VideoCodec.H264, 0, false)]
    [InlineData(VideoCodec.H265, 51, false)]
    [InlineData(VideoCodec.H265, 52, true)]
    public void Validate_H26xCrf_AcceptsOnlyZeroToFiftyOne(VideoCodec codec, int crf, bool expected)
    {
        var settings = SettingsFor(codec)
            .WithQuality(new H26xQualityOptions(codec, Crf: crf))
            .Build();

        var result = _sut.Validate(settings, new TestMediaInfoBuilder().Build());

        HasIssueWithCode(result, "quality_crf_invalid").Should().Be(expected);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(63, false)]
    [InlineData(64, true)]
    public void Validate_Vp9Quality_AcceptsOnlyZeroToSixtyThree(int quality, bool expected)
    {
        var settings = new TestOutputSettingsBuilder()
            .WithVp9()
            .WithQuality(new Vp9QualityOptions(Quality: quality))
            .Build();
        var media = new TestMediaInfoBuilder().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "quality_value_invalid").Should().Be(expected);
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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_NonPositiveBitrate_ReturnsVideoBitrateInvalid(int bitrate)
    {
        var settings = new TestOutputSettingsBuilder()
            .WithTargetBitrate(bitrate)
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

    // ── HDR validation ─────────────────────────────────────────────────

    [Theory]
    [InlineData(VideoCodec.H264, true)]
    [InlineData(VideoCodec.Vp9, true)]
    [InlineData(VideoCodec.H265, false)]
    [InlineData(VideoCodec.Av1, false)]
    public void Validate_HdrSourcePreserve_RequiresHdrCapableCodec(VideoCodec codec, bool expected)
    {
        var settings = SettingsFor(codec).WithHdrPreserve().Build();
        var media = new TestMediaInfoBuilder().WithHdr10().Build();

        var result = _sut.Validate(settings, media);

        HasIssueWithCode(result, "hdr_codec_incompatible").Should().Be(expected);
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
