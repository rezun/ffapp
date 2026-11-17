using System.Collections.Immutable;
using FFApp.Core.Models;
using FFApp.Core.Services;

using static FFApp.Core.Validation.CompatibilityData;

namespace FFApp.Core.Validation;

public sealed class ValidationService(IFfmpegCapabilityService? capabilities = null) : IValidationService
{
    public IReadOnlyList<VideoCodec> AllowedVideoCodecs(OutputContainer container) =>
        CompatibilityData.AllowedVideoCodecs(container);

    public IReadOnlyList<AudioCodec> AllowedAudioCodecs(OutputContainer container) =>
        CompatibilityData.AllowedAudioCodecs(container);

    public Resolution? ResolveOutputResolution(OutputSettings settings, SourceMediaInfo mediaInfo)
    {
        var sourceResolution = SourceResolution(mediaInfo);
        if (sourceResolution is null)
            return null;

        if (settings.ResolutionPreset is ResolutionPreset.Original)
            return sourceResolution;

        var target = PresetResolutions[settings.ResolutionPreset];
        var isPortrait = sourceResolution.Height > sourceResolution.Width;
        return isPortrait ? new Resolution(target.Height, target.Width) : target;
    }

    public ValidationResult Validate(OutputSettings settings, SourceMediaInfo mediaInfo, double? trimmedDurationSeconds = null) =>
        Validate(settings, mediaInfo, trimmedDurationSeconds, capabilities?.Capabilities);

    public ValidationResult Validate(
        OutputSettings settings, SourceMediaInfo mediaInfo, double? trimmedDurationSeconds, FfmpegCapabilities? ffmpegCapabilities)
    {
        var issues = ImmutableArray.CreateBuilder<ValidationIssue>();
        if (ffmpegCapabilities is { } available)
            issues.AddRange(FfmpegFeatureValidation.Validate(settings, mediaInfo, available));

        // Cannot remove both video and audio.
        if (settings.VideoMode == VideoMode.Remove && settings.AudioOptions.Mode == AudioMode.Remove)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "no_streams_remaining",
                "Cannot remove both video and audio — no streams would remain.",
                "video_mode"));
            return new ValidationResult { Issues = issues.ToImmutable() };
        }

        // Audio-only container requires video removal.
        if (CompatibilityData.IsAudioOnlyContainer(settings.Container)
            && settings.VideoMode != VideoMode.Remove)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "audio_container_requires_video_remove",
                "Audio-only containers require Video mode to be set to Remove.",
                "video_mode"));
        }

        // Container / codec compatibility (only for video containers with video).
        if (settings.VideoMode != VideoMode.Remove)
            ValidateContainerCompatibility(settings, issues);

        // Audio settings (always validated).
        ValidateAudioSettings(settings, issues);

        // Audio codec compatibility with container.
        ValidateAudioContainerCompatibility(settings, issues);

        if (settings.AudioOptions.Mode is AudioMode.Copy)
            ValidateAudioCopyCompatibility(settings, mediaInfo, issues);

        ValidateSubtitleCopyCompatibility(settings, mediaInfo, issues);
        ValidateDataCopyCompatibility(settings, mediaInfo, issues);
        if (!settings.StreamHandling.RemoveImages
            && mediaInfo.AttachedPictureStreams.Any(stream =>
                !StreamCompatibilityPolicy.CanCopyAttachedPicture(settings.Container, stream)))
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                "image_streams_not_preserved",
                $"{settings.Container.ToString().ToUpperInvariant()} output cannot preserve some attached images. "
                    + "Unsupported images will be removed automatically.",
                "remove_images"));
        }

        if (settings.VideoMode == VideoMode.Remove)
        {
            // Audio-only: no video validation needed, just audio and streams.
            if (mediaInfo.AudioStreams.Length == 0)
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "audio_stream_missing",
                    "The source file has no audio streams to extract.",
                    "source"));
            }

            return new ValidationResult { Issues = issues.ToImmutable() };
        }

        // Video stream presence (required for copy and reencode).
        if (mediaInfo.VideoStreams.Length == 0)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "video_stream_missing",
                "The source file must contain at least one video stream.",
                "source"));
            return new ValidationResult { Issues = issues.ToImmutable() };
        }

        if (mediaInfo.VideoStreams.Length > 1)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                "multiple_video_streams",
                "Multiple video streams detected. Only the first stream will be used.",
                "source"));
        }

        // Video copy mode: no encoder/quality/resolution validation needed.
        if (settings.VideoMode == VideoMode.Copy)
        {
            return new ValidationResult { Issues = issues.ToImmutable() };
        }

        // Video re-encode: full validation.
        ValidateVideoEncoderSettings(settings, issues);
        ValidateHdrSettings(settings, mediaInfo, issues);
        ValidateColorMetadata(settings, mediaInfo, issues);

        // Resolution.
        if (settings.ResolutionPreset is not ResolutionPreset.Original
            && ResolveOutputResolution(settings, mediaInfo) is null)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "resolution_unavailable",
                "The selected resolution preset needs source width and height from ffprobe.",
                "resolution_preset"));
        }

        // Encoding strategy.
        switch (settings.EncodingStrategy)
        {
            case EncodingStrategy.TargetFileSize:
                ValidateFileSizeStrategy(settings, mediaInfo, trimmedDurationSeconds, issues);
                break;
            case EncodingStrategy.TargetBitrate:
                ValidateTargetBitrateStrategy(settings, mediaInfo, issues);
                break;
            default:
                ValidateQualityStrategy(settings, issues);
                break;
        }

        return new ValidationResult { Issues = issues.ToImmutable() };
    }

    private static void ValidateColorMetadata(
        OutputSettings settings, SourceMediaInfo mediaInfo,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        var color = settings.ColorMetadata;
        if (!Enum.IsDefined(color.Mode)
            || (color.Mode == ColorMetadataMode.Custom
                && !ColorMetadataResolver.HasHdrSignal(mediaInfo.PrimaryVideoStream)
                && (!ColorPrimariesOptions.Contains(color.Primaries)
                    || !ColorTransferOptions.Contains(color.Transfer)
                    || !ColorMatrixOptions.Contains(color.Matrix)
                    || !ColorRangeOptions.Contains(color.Range))))
            issues.Add(new ValidationIssue(ValidationSeverity.Error, "invalid_color_metadata",
                "Select valid color metadata values.", "color_metadata"));
    }

    // ── Container / codec compatibility ─────────────────────────────────

    private static void ValidateContainerCompatibility(
        OutputSettings settings,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        // Video codec (only for video containers).
        var videoCodecs = CompatibilityData.AllowedVideoCodecs(settings.Container);
        if (videoCodecs.Count > 0 && !videoCodecs.Contains(settings.VideoCodec))
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "video_codec_incompatible",
                $"{VideoCodecLabel(settings.VideoCodec)} is not supported in "
                    + $"{settings.Container.ToString().ToUpperInvariant()}.",
                "video_codec"));
        }

    }

    private static void ValidateAudioContainerCompatibility(
        OutputSettings settings,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        var effectiveMode = settings.AudioOptions.Mode;
        if (effectiveMode is AudioMode.Reencode
            && settings.AudioOptions.Codec is { } codec
            && !AudioCodecMatrix[settings.Container].Contains(codec))
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "audio_codec_incompatible",
                "The selected audio codec is not supported in the chosen container.",
                "audio_codec"));
        }
    }

    private static void ValidateAudioCopyCompatibility(
        OutputSettings settings,
        SourceMediaInfo mediaInfo,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        var containerLabel = settings.Container.ToString().ToUpperInvariant();

        foreach (var stream in mediaInfo.AudioStreams)
        {
            if (StreamCompatibilityPolicy.CanCopyAudioCodec(settings.Container, stream.CodecName))
                continue;

            if (!StreamCompatibilityPolicy.IsKnownAudioCopyCodecName(stream.CodecName))
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "audio_copy_codec_unknown",
                    $"Audio stream {stream.Index} uses codec "
                        + $"{stream.CodecName ?? "unknown"}, which cannot be validated for "
                        + $"{containerLabel} copy mode.",
                    "audio_mode"));
                continue;
            }

            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "audio_copy_incompatible",
                $"Audio stream {stream.Index} uses {stream.CodecName}, "
                    + $"which is not supported for copy into {containerLabel}.",
                "audio_mode"));
        }
    }

    private static void ValidateSubtitleCopyCompatibility(
        OutputSettings settings,
        SourceMediaInfo mediaInfo,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        var containerLabel = settings.Container.ToString().ToUpperInvariant();
        var policy = StreamCompatibilityPolicy.ResolveSubtitlePolicy(
            settings.Container,
            mediaInfo.SubtitleStreams,
            settings.StreamHandling.RemoveSubtitles);

        if (policy.Mode is not SubtitleStreamMode.Unsupported)
            return;

        if (policy.UnsupportedStream is not { } stream)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "subtitle_copy_incompatible",
                $"{containerLabel} output does not preserve subtitle streams. "
                    + "Remove subtitle streams to continue.",
                "remove_subtitles"));
            return;
        }

        issues.Add(new ValidationIssue(
            ValidationSeverity.Error,
            "subtitle_copy_incompatible",
            $"Subtitle stream {stream.Index} uses {stream.CodecName ?? "unknown"}, "
                + $"which cannot be copied or converted for {containerLabel} output.",
            "remove_subtitles"));
    }

    private static void ValidateDataCopyCompatibility(
        OutputSettings settings,
        SourceMediaInfo mediaInfo,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        if (!StreamCompatibilityPolicy.ShouldWarnDataStreamsNotPreserved(
            settings.Container,
            mediaInfo.DataStreams,
            settings.StreamHandling.RemoveData))
        {
            return;
        }

        issues.Add(new ValidationIssue(
            ValidationSeverity.Warning,
            "data_streams_not_preserved",
            $"{settings.Container.ToString().ToUpperInvariant()} output does not preserve data streams. "
                + "They will be removed automatically.",
            "remove_data"));
    }

    // ── Video encoder settings ──────────────────────────────────────────

    private static void ValidateVideoEncoderSettings(
        OutputSettings settings,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        switch (settings.VideoCodec)
        {
            case VideoCodec.H264:
            case VideoCodec.H265:
            {
                var encoder = settings.VideoEncoderOptions
                    ?? H26xEncoderOptions.ForCodec(settings.VideoCodec);
                if (encoder is not H26xEncoderOptions h26x || h26x.Codec != settings.VideoCodec)
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        "video_encoder_options_required",
                        $"Choose a valid {VideoCodecLabel(settings.VideoCodec)} preset and profile.",
                        "video_encoder"));
                    return;
                }
                ValidateH26xEncoderOptions(
                    h26x.Preset, h26x.Profile,
                    H26xPresetOptions,
                    settings.VideoCodec == VideoCodec.H264 ? H264ProfileOptions : H265ProfileOptions,
                    "video_encoder", issues);
                break;
            }
            case VideoCodec.Av1:
            {
                var encoder = settings.VideoEncoderOptions ?? new Av1EncoderOptions();
                if (encoder is not Av1EncoderOptions av1)
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        "video_encoder_options_required",
                        "Choose a valid AV1 preset.",
                        "video_encoder"));
                    return;
                }
                if (av1.Preset < CompatibilityData.Av1PresetMin || av1.Preset > CompatibilityData.Av1PresetMax)
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        "video_encoder_preset_invalid",
                        $"AV1 preset must be between {CompatibilityData.Av1PresetMin} and {CompatibilityData.Av1PresetMax}.",
                        "video_encoder"));
                }
                ValidateAv1FilmGrain(
                    av1.FilmGrain, av1.FilmGrainDenoise, av1.AdaptiveFilmGrain,
                    "video_encoder", issues);
                break;
            }
            default: // VP9
            {
                var encoder = settings.VideoEncoderOptions
                    ?? new VpxEncoderOptions(settings.VideoCodec);
                if (encoder is not VpxEncoderOptions vpx || vpx.Codec != settings.VideoCodec)
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        "video_encoder_options_required",
                        "Choose a valid VP9 CPU used setting.",
                        "video_encoder"));
                    return;
                }
                if (vpx.CpuUsed < VpxSpeedMin || vpx.CpuUsed > VpxSpeedMax)
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        "video_encoder_cpu_used_invalid",
                        "VP9 CPU used must be between 0 and 8.",
                        "video_encoder"));
                }
                if (vpx.TileColumns < VpxTileColumnsMin || vpx.TileColumns > VpxTileColumnsMax)
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        "video_encoder_tile_columns_invalid",
                        $"VP9 tile columns must be between {VpxTileColumnsMin} and {VpxTileColumnsMax}.",
                        "video_encoder"));
                }
                if (vpx.TileRows < VpxTileRowsMin || vpx.TileRows > VpxTileRowsMax)
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        "video_encoder_tile_rows_invalid",
                        $"VP9 tile rows must be between {VpxTileRowsMin} and {VpxTileRowsMax}.",
                        "video_encoder"));
                }
                break;
            }
        }
    }

    private static void ValidateH26xEncoderOptions(
        string preset,
        string profile,
        IReadOnlyList<string> presetOptions,
        IReadOnlyList<string> profileOptions,
        string fieldName,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        if (!presetOptions.Contains(preset))
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "quality_preset_invalid",
                "The selected encoder preset is not valid for this codec.",
                fieldName));
        }
        if (!profileOptions.Contains(profile))
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "quality_profile_invalid",
                "The selected encoder profile is not valid for this codec.",
                fieldName));
        }
    }

    // ── Audio settings ──────────────────────────────────────────────────

    private static void ValidateAudioSettings(
        OutputSettings settings,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        var mode = settings.AudioOptions.Mode;

        if (settings.AudioOptions.AudioChannels is not null && mode is not AudioMode.Reencode and not AudioMode.Remove)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "audio_channels_requires_reencode",
                "Changing audio channels requires audio re-encoding.",
                "audio_mode"));
        }

        if (!string.IsNullOrWhiteSpace(settings.AudioOptions.AudioFilter) && mode is not AudioMode.Reencode and not AudioMode.Remove)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "audio_filter_requires_reencode",
                "An audio filter requires audio re-encoding.",
                "audio_mode"));
        }

        if (settings.AudioOptions.SampleRate is not null && mode is not AudioMode.Reencode and not AudioMode.Remove)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "audio_sample_rate_requires_reencode",
                "Changing sample rate requires audio re-encoding.",
                "audio_mode"));
        }

        if (mode is AudioMode.Reencode)
        {
            if (settings.AudioOptions.Codec is { } codec
                && settings.AudioOptions.SampleRate is { } sampleRate
                && !IsAudioSampleRateSupported(codec, sampleRate))
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "audio_sample_rate_unsupported",
                    $"Sample rate {sampleRate} Hz is not supported by {codec}. Choose Auto or a supported sample rate.",
                    "audio_sample_rate"));
            }
            if (settings.AudioOptions.Codec is null)
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "audio_codec_required",
                    "Choose an audio codec for audio re-encoding.",
                    "audio_codec"));
            }
            if (settings.AudioOptions.Bitrate is null or <= 0)
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Error,
                    "audio_bitrate_required",
                    "Choose a positive audio bitrate for audio re-encoding.",
                    "audio_bitrate"));
            }
        }
    }

    // ── File size strategy ──────────────────────────────────────────────

    private void ValidateFileSizeStrategy(
        OutputSettings settings,
        SourceMediaInfo mediaInfo,
        double? trimmedDurationSeconds,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        var options = settings.TargetFileSize;

        if (options.TargetSizeMb <= 0)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "target_size_invalid",
                "Target size must be greater than 0 MB.",
                "target_file_size"));
            return;
        }

        if (mediaInfo.DurationSeconds is null or <= 0)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "duration_required",
                "Target file size mode requires a valid source duration from ffprobe.",
                "target_file_size"));
            return;
        }

        var effectiveDuration = trimmedDurationSeconds ?? mediaInfo.DurationSeconds.Value;
        var estimatedAudioBits = EstimateAudioBitsForSettings(settings, mediaInfo, effectiveDuration);
        if (estimatedAudioBits is null)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "audio_copy_size_unknown",
                "Target file size mode requires audio re-encoding when copied audio "
                    + "bitrates are unavailable.",
                "audio_mode"));
            return;
        }

        var totalBudgetBits = (long)options.TargetSizeMb * 1024 * 1024 * 8;
        var usableBudgetBits = totalBudgetBits - options.EffectiveOverheadBits(totalBudgetBits);
        var videoBudgetBits = usableBudgetBits - estimatedAudioBits.Value;

        if (videoBudgetBits <= 0)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "video_budget_exhausted",
                "The target size leaves no remaining bitrate budget for video.",
                "target_file_size"));
            return;
        }

        var videoBitrate = (int)(videoBudgetBits / effectiveDuration);
        AddBitrateWarningIssues(videoBitrate, settings, mediaInfo, "target_file_size", issues);
    }

    // ── Target bitrate strategy ─────────────────────────────────────────

    private void ValidateTargetBitrateStrategy(
        OutputSettings settings,
        SourceMediaInfo mediaInfo,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        var options = settings.TargetBitrate;

        if (options.VideoBitrate <= 0)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "video_bitrate_invalid",
                "Video bitrate must be greater than 0 bps.",
                "target_bitrate"));
            return;
        }

        AddBitrateWarningIssues(options.VideoBitrate, settings, mediaInfo, "target_bitrate", issues);
    }

    // ── Quality strategy ────────────────────────────────────────────────

    private static void ValidateQualityStrategy(
        OutputSettings settings,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        var quality = settings.TargetQuality;

        if (quality.Codec != settings.VideoCodec)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "quality_codec_mismatch",
                "Quality settings must match the selected video codec.",
                "target_quality"));
            return;
        }

        switch (quality)
        {
            case H26xQualityOptions h26x:
                ValidateH26xQuality(
                    h26x.Crf, h26x.Preset, h26x.Profile,
                    H26xPresetOptions,
                    h26x.Codec == VideoCodec.H264 ? H264ProfileOptions : H265ProfileOptions,
                    "target_quality", issues);
                break;
            case Vp9QualityOptions vp9:
                if (vp9.Quality is < 0 or > 63)
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        "quality_value_invalid",
                        "VP9 quality must be between 0 and 63.",
                        "target_quality"));
                }
                break;
            case Av1QualityOptions av1:
                if (av1.Crf < Av1CrfMin || av1.Crf > Av1CrfMax)
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        "quality_crf_invalid",
                        $"AV1 CRF must be between {Av1CrfMin} and {Av1CrfMax}.",
                        "target_quality"));
                }
                if (av1.Preset < Av1PresetMin || av1.Preset > Av1PresetMax)
                {
                    issues.Add(new ValidationIssue(
                        ValidationSeverity.Error,
                        "quality_preset_invalid",
                        $"AV1 preset must be between {Av1PresetMin} and {Av1PresetMax}.",
                        "target_quality"));
                }
                ValidateAv1FilmGrain(
                    av1.FilmGrain, av1.FilmGrainDenoise, av1.AdaptiveFilmGrain,
                    "target_quality", issues);
                break;
        }
    }

    private static void ValidateAv1FilmGrain(
        int filmGrain,
        int filmGrainDenoise,
        int adaptiveFilmGrain,
        string field,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        if (filmGrain < Av1FilmGrainMin || filmGrain > Av1FilmGrainMax)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "av1_film_grain_invalid",
                $"AV1 film grain must be between {Av1FilmGrainMin} and {Av1FilmGrainMax}.",
                field));
        }
        if (!Av1FilmGrainToggleOptions.Contains(filmGrainDenoise))
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "av1_film_grain_denoise_invalid",
                "AV1 film grain denoise must be off (0) or on (1).",
                field));
        }
        if (!Av1FilmGrainToggleOptions.Contains(adaptiveFilmGrain))
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "av1_adaptive_film_grain_invalid",
                "AV1 adaptive film grain must be off (0) or on (1).",
                field));
        }
    }

    private static void ValidateH26xQuality(
        int crf,
        string preset,
        string profile,
        IReadOnlyList<string> presetOptions,
        IReadOnlyList<string> profileOptions,
        string fieldName,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        if (crf is < 0 or > 51)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "quality_crf_invalid",
                "CRF must be between 0 and 51.",
                fieldName));
        }
        if (!presetOptions.Contains(preset))
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "quality_preset_invalid",
                "The selected encoder preset is not valid for this codec.",
                fieldName));
        }
        if (!profileOptions.Contains(profile))
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "quality_profile_invalid",
                "The selected encoder profile is not valid for this codec.",
                fieldName));
        }
    }

    // ── HDR settings ──────────────────────────────────────────────────

    private static void ValidateHdrSettings(
        OutputSettings settings,
        SourceMediaInfo mediaInfo,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        var isHdrSource = mediaInfo.PrimaryVideoStream?.IsHdr == true;

        if (isHdrSource
            && settings.HdrMode == HdrMode.Preserve
            && !CompatibilityData.SupportsHdrOutput(settings.VideoCodec))
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Error,
                "hdr_codec_incompatible",
                $"{VideoCodecLabel(settings.VideoCodec)} does not support HDR. "
                    + "Use H.265 or AV1, or convert to SDR.",
                "hdr_mode"));
        }

        if (isHdrSource
            && settings.HdrMode == HdrMode.Preserve
            && settings.VideoCodec == VideoCodec.H265)
        {
            var profile = (settings.VideoEncoderOptions as H26xEncoderOptions)?.Profile
                       ?? (settings.TargetQuality as H26xQualityOptions)?.Profile;
            if (profile is not null && profile != "main10")
            {
                issues.Add(new ValidationIssue(
                    ValidationSeverity.Warning,
                    "hdr_profile_override",
                    "HDR output requires the main10 profile. It will be set automatically.",
                    "hdr_mode"));
            }
        }

        if (!isHdrSource && settings.HdrMode == HdrMode.TonemapToSdr)
        {
            issues.Add(new ValidationIssue(
                ValidationSeverity.Warning,
                "tonemap_sdr_source",
                "Source is not HDR. Tone mapping settings will be ignored.",
                "hdr_mode"));
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static Resolution? SourceResolution(SourceMediaInfo mediaInfo)
    {
        var primary = mediaInfo.PrimaryVideoStream;
        if (primary is null || primary.DisplayWidth is null || primary.DisplayHeight is null)
            return null;
        return new Resolution(primary.DisplayWidth.Value, primary.DisplayHeight.Value);
    }

    private static long? EstimateAudioBitsForSettings(OutputSettings settings, SourceMediaInfo mediaInfo, double? effectiveDuration = null) =>
        CompatibilityData.EstimateAudioBits(
            settings.AudioOptions.Mode,
            settings.AudioOptions.Bitrate,
            mediaInfo.AudioStreams,
            effectiveDuration ?? mediaInfo.DurationSeconds);

    private void AddBitrateWarningIssues(
        int bitrate,
        OutputSettings settings,
        SourceMediaInfo mediaInfo,
        string fieldName,
        ImmutableArray<ValidationIssue>.Builder issues)
    {
        var threshold = BitrateWarningThreshold(ResolveOutputResolution(settings, mediaInfo));
        if (threshold is null || bitrate >= threshold)
            return;

        issues.Add(new ValidationIssue(
            ValidationSeverity.Warning,
            "video_bitrate_low",
            $"Calculated video bitrate {FormatMbps(bitrate)} is low for the selected "
                + $"resolution. Recommended minimum is {FormatMbps(threshold.Value)}.",
            fieldName));
    }

    private static string FormatMbps(int bitrate) =>
        $"{bitrate / 1_000_000.0:F2} Mbps";

}
