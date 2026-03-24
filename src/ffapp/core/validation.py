from __future__ import annotations

from collections.abc import Sequence

from ffapp.core.models import (
    AudioCodec,
    AudioMode,
    H264QualityOptions,
    H265QualityOptions,
    OutputContainer,
    OutputSettings,
    Resolution,
    ResolutionPreset,
    SourceMediaInfo,
    ValidationIssue,
    ValidationResult,
    ValidationSeverity,
    VideoCodec,
    Vp8QualityOptions,
    Vp9QualityOptions,
)
from ffapp.core.services import CompatibilityService, ValidationService

H26X_PRESET_OPTIONS: tuple[str, ...] = (
    "ultrafast",
    "superfast",
    "veryfast",
    "faster",
    "fast",
    "medium",
    "slow",
    "slower",
    "veryslow",
)
H264_PROFILE_OPTIONS: tuple[str, ...] = ("baseline", "main", "high")
H265_PROFILE_OPTIONS: tuple[str, ...] = ("main", "main10")
VPX_SPEED_RANGE = range(0, 9)

_VIDEO_CODEC_MATRIX: dict[OutputContainer, tuple[VideoCodec, ...]] = {
    OutputContainer.MP4: (VideoCodec.H264, VideoCodec.H265),
    OutputContainer.MKV: (VideoCodec.H264, VideoCodec.H265, VideoCodec.VP8, VideoCodec.VP9),
    OutputContainer.OGG: (VideoCodec.VP8, VideoCodec.VP9),
}
_AUDIO_CODEC_MATRIX: dict[OutputContainer, tuple[AudioCodec, ...]] = {
    OutputContainer.MP4: (AudioCodec.AAC, AudioCodec.MP3),
    OutputContainer.MKV: (AudioCodec.MP3, AudioCodec.AAC, AudioCodec.VORBIS, AudioCodec.OPUS),
    OutputContainer.OGG: (AudioCodec.VORBIS, AudioCodec.OPUS),
}
_PRESET_RESOLUTIONS: dict[ResolutionPreset, Resolution] = {
    ResolutionPreset.P720: Resolution(width=1280, height=720),
    ResolutionPreset.P1080: Resolution(width=1920, height=1080),
    ResolutionPreset.P1440: Resolution(width=2560, height=1440),
    ResolutionPreset.P4K: Resolution(width=3840, height=2160),
}
_AUDIO_CODEC_NAMES: dict[str, AudioCodec] = {
    "aac": AudioCodec.AAC,
    "libfdk_aac": AudioCodec.AAC,
    "mp3": AudioCodec.MP3,
    "libmp3lame": AudioCodec.MP3,
    "opus": AudioCodec.OPUS,
    "libopus": AudioCodec.OPUS,
    "vorbis": AudioCodec.VORBIS,
    "libvorbis": AudioCodec.VORBIS,
}
_MP4_SUBTITLE_CODECS = {"mov_text", "tx3g"}


class DefaultCompatibilityService(CompatibilityService):
    def allowed_video_codecs(self, container: OutputContainer) -> Sequence[VideoCodec]:
        return _VIDEO_CODEC_MATRIX[container]

    def allowed_audio_codecs(self, container: OutputContainer) -> Sequence[AudioCodec]:
        return _AUDIO_CODEC_MATRIX[container]

    def validate(self, settings: OutputSettings, media_info: SourceMediaInfo) -> ValidationResult:
        issues: list[ValidationIssue] = []

        if settings.video_codec not in _VIDEO_CODEC_MATRIX[settings.container]:
            issues.append(
                ValidationIssue(
                    severity=ValidationSeverity.ERROR,
                    code="video_codec_incompatible",
                    message=(
                        f"{_video_codec_label(settings.video_codec)} is not supported in "
                        f"{settings.container.value.upper()}."
                    ),
                    field_name="video_codec",
                )
            )

        if _effective_audio_mode(settings) is AudioMode.REENCODE:
            if settings.audio_options.codec not in _AUDIO_CODEC_MATRIX[settings.container]:
                issues.append(
                    ValidationIssue(
                        severity=ValidationSeverity.ERROR,
                        code="audio_codec_incompatible",
                        message=(
                            "The selected audio codec is not supported in the chosen container."
                        ),
                        field_name="audio_codec",
                    )
                )
        elif _effective_audio_mode(settings) is AudioMode.COPY:
            issues.extend(_validate_audio_copy_compatibility(settings, media_info))

        issues.extend(_validate_subtitle_copy_compatibility(settings, media_info))
        issues.extend(_validate_data_copy_compatibility(settings, media_info))
        return ValidationResult(issues=tuple(issues))


class DefaultValidationService(ValidationService):
    def __init__(self, compatibility_service: CompatibilityService | None = None) -> None:
        self._compatibility_service = compatibility_service or DefaultCompatibilityService()

    def resolve_output_resolution(
        self,
        settings: OutputSettings,
        media_info: SourceMediaInfo,
    ) -> Resolution | None:
        source_resolution = _source_resolution(media_info)
        if source_resolution is None:
            return None

        if settings.resolution_preset is ResolutionPreset.ORIGINAL:
            return source_resolution

        target = _PRESET_RESOLUTIONS[settings.resolution_preset]
        is_portrait = source_resolution.height > source_resolution.width
        if is_portrait:
            return Resolution(width=target.height, height=target.width)
        return target

    def validate(self, settings: OutputSettings, media_info: SourceMediaInfo) -> ValidationResult:
        issues = list(self._compatibility_service.validate(settings, media_info).issues)

        if not media_info.video_streams:
            issues.append(
                ValidationIssue(
                    severity=ValidationSeverity.ERROR,
                    code="video_stream_missing",
                    message="The source file must contain at least one video stream.",
                    field_name="source",
                )
            )
            return ValidationResult(issues=tuple(issues))

        if len(media_info.video_streams) > 1:
            issues.append(
                ValidationIssue(
                    severity=ValidationSeverity.WARNING,
                    code="multiple_video_streams",
                    message="Multiple video streams detected. MVP uses the first stream only.",
                    field_name="source",
                )
            )

        issues.extend(self._validate_audio_settings(settings))

        if (
            settings.resolution_preset is not ResolutionPreset.ORIGINAL
            and self.resolve_output_resolution(settings, media_info) is None
        ):
            issues.append(
                ValidationIssue(
                    severity=ValidationSeverity.ERROR,
                    code="resolution_unavailable",
                    message=(
                        "The selected resolution preset needs source width and height from ffprobe."
                    ),
                    field_name="resolution_preset",
                )
            )

        if settings.encoding_strategy.value == "target_file_size":
            issues.extend(self._validate_file_size_strategy(settings, media_info))
        elif settings.encoding_strategy.value == "target_bitrate":
            issues.extend(self._validate_target_bitrate_strategy(settings, media_info))
        else:
            issues.extend(self._validate_quality_strategy(settings))

        return ValidationResult(issues=tuple(issues))

    def _validate_audio_settings(self, settings: OutputSettings) -> list[ValidationIssue]:
        issues: list[ValidationIssue] = []
        mode = _effective_audio_mode(settings)

        if settings.audio_options.remix_to_stereo and mode is not AudioMode.REENCODE:
            issues.append(
                ValidationIssue(
                    severity=ValidationSeverity.ERROR,
                    code="audio_remix_requires_reencode",
                    message="Stereo remix requires audio re-encoding.",
                    field_name="audio_mode",
                )
            )

        if mode is AudioMode.REENCODE:
            if settings.audio_options.codec is None:
                issues.append(
                    ValidationIssue(
                        severity=ValidationSeverity.ERROR,
                        code="audio_codec_required",
                        message="Choose an audio codec for audio re-encoding.",
                        field_name="audio_codec",
                    )
                )
            if settings.audio_options.bitrate is None or settings.audio_options.bitrate <= 0:
                issues.append(
                    ValidationIssue(
                        severity=ValidationSeverity.ERROR,
                        code="audio_bitrate_required",
                        message="Choose a positive audio bitrate for audio re-encoding.",
                        field_name="audio_bitrate",
                    )
                )

        return issues

    def _validate_file_size_strategy(
        self,
        settings: OutputSettings,
        media_info: SourceMediaInfo,
    ) -> list[ValidationIssue]:
        issues: list[ValidationIssue] = []
        options = settings.target_file_size

        if options.target_size_mb <= 0:
            issues.append(
                ValidationIssue(
                    severity=ValidationSeverity.ERROR,
                    code="target_size_invalid",
                    message="Target size must be greater than 0 MB.",
                    field_name="target_file_size",
                )
            )
            return issues

        if media_info.duration_seconds is None or media_info.duration_seconds <= 0:
            issues.append(
                ValidationIssue(
                    severity=ValidationSeverity.ERROR,
                    code="duration_required",
                    message="Target file size mode requires a valid source duration from ffprobe.",
                    field_name="target_file_size",
                )
            )
            return issues

        estimated_audio_bits = _estimate_audio_bits(settings, media_info)
        if estimated_audio_bits is None:
            issues.append(
                ValidationIssue(
                    severity=ValidationSeverity.ERROR,
                    code="audio_copy_size_unknown",
                    message=(
                        "Target file size mode requires audio re-encoding when copied audio "
                        "bitrates are unavailable."
                    ),
                    field_name="audio_mode",
                )
            )
            return issues

        total_budget_bits = options.target_size_mb * 1024 * 1024 * 8
        usable_budget_bits = int(total_budget_bits * (1 - options.muxing_overhead_ratio))
        video_budget_bits = usable_budget_bits - estimated_audio_bits
        if video_budget_bits <= 0:
            issues.append(
                ValidationIssue(
                    severity=ValidationSeverity.ERROR,
                    code="video_budget_exhausted",
                    message="The target size leaves no remaining bitrate budget for video.",
                    field_name="target_file_size",
                )
            )
            return issues

        video_bitrate = int(video_budget_bits / media_info.duration_seconds)
        issues.extend(
            _video_bitrate_warning_issues(
                bitrate=video_bitrate,
                settings=settings,
                media_info=media_info,
                field_name="target_file_size",
            )
        )
        return issues

    def _validate_target_bitrate_strategy(
        self,
        settings: OutputSettings,
        media_info: SourceMediaInfo,
    ) -> list[ValidationIssue]:
        options = settings.target_bitrate
        if options.video_bitrate <= 0:
            return [
                ValidationIssue(
                    severity=ValidationSeverity.ERROR,
                    code="video_bitrate_invalid",
                    message="Video bitrate must be greater than 0 bps.",
                    field_name="target_bitrate",
                )
            ]

        return _video_bitrate_warning_issues(
            bitrate=options.video_bitrate,
            settings=settings,
            media_info=media_info,
            field_name="target_bitrate",
        )

    def _validate_quality_strategy(self, settings: OutputSettings) -> list[ValidationIssue]:
        quality = settings.target_quality

        if quality.codec is not settings.video_codec:
            return [
                ValidationIssue(
                    severity=ValidationSeverity.ERROR,
                    code="quality_codec_mismatch",
                    message="Quality settings must match the selected video codec.",
                    field_name="target_quality",
                )
            ]

        if isinstance(quality, H264QualityOptions):
            return _validate_h26x_quality(
                crf=quality.crf,
                preset=quality.preset,
                profile=quality.profile,
                preset_options=H26X_PRESET_OPTIONS,
                profile_options=H264_PROFILE_OPTIONS,
                field_name="target_quality",
            )

        if isinstance(quality, H265QualityOptions):
            return _validate_h26x_quality(
                crf=quality.crf,
                preset=quality.preset,
                profile=quality.profile,
                preset_options=H26X_PRESET_OPTIONS,
                profile_options=H265_PROFILE_OPTIONS,
                field_name="target_quality",
            )

        if isinstance(quality, (Vp8QualityOptions, Vp9QualityOptions)):
            issues: list[ValidationIssue] = []
            if not 0 <= quality.quality <= 63:
                issues.append(
                    ValidationIssue(
                        severity=ValidationSeverity.ERROR,
                        code="quality_value_invalid",
                        message="VP8/VP9 quality must be between 0 and 63.",
                        field_name="target_quality",
                    )
                )
            if quality.speed not in VPX_SPEED_RANGE:
                issues.append(
                    ValidationIssue(
                        severity=ValidationSeverity.ERROR,
                        code="quality_speed_invalid",
                        message="VP8/VP9 speed must be between 0 and 8.",
                        field_name="target_quality",
                    )
                )
            return issues

        return []


def quality_preset_options(video_codec: VideoCodec) -> tuple[str, ...]:
    if video_codec in {VideoCodec.H264, VideoCodec.H265}:
        return H26X_PRESET_OPTIONS
    return ()


def quality_profile_options(video_codec: VideoCodec) -> tuple[str, ...]:
    if video_codec is VideoCodec.H264:
        return H264_PROFILE_OPTIONS
    if video_codec is VideoCodec.H265:
        return H265_PROFILE_OPTIONS
    return ()


def bitrate_warning_threshold(resolution: Resolution | None) -> int | None:
    if resolution is None:
        return None

    long_edge = max(resolution.width, resolution.height)
    if long_edge >= 3840:
        return 12_000_000
    if long_edge >= 2560:
        return 6_000_000
    if long_edge >= 1920:
        return 3_000_000
    if long_edge >= 1280:
        return 1_500_000
    return 800_000


def _validate_audio_copy_compatibility(
    settings: OutputSettings,
    media_info: SourceMediaInfo,
) -> list[ValidationIssue]:
    issues: list[ValidationIssue] = []
    allowed_codecs = set(_AUDIO_CODEC_MATRIX[settings.container])

    for stream in media_info.audio_streams:
        codec_name = (stream.codec_name or "").lower()
        codec = _AUDIO_CODEC_NAMES.get(codec_name)
        if codec is None:
            issues.append(
                ValidationIssue(
                    severity=ValidationSeverity.ERROR,
                    code="audio_copy_codec_unknown",
                    message=(
                        f"Audio stream {stream.index} uses codec "
                        f"{stream.codec_name or 'unknown'}, which the MVP cannot validate for "
                        f"{settings.container.value.upper()} copy mode."
                    ),
                    field_name="audio_mode",
                )
            )
            continue
        if codec not in allowed_codecs:
            issues.append(
                ValidationIssue(
                    severity=ValidationSeverity.ERROR,
                    code="audio_copy_incompatible",
                    message=(
                        f"Audio stream {stream.index} uses {codec.value}, which is not supported "
                        f"for copy into {settings.container.value.upper()}."
                    ),
                    field_name="audio_mode",
                )
            )

    return issues


def _validate_subtitle_copy_compatibility(
    settings: OutputSettings,
    media_info: SourceMediaInfo,
) -> list[ValidationIssue]:
    if settings.stream_handling.remove_subtitles:
        return []

    issues: list[ValidationIssue] = []
    if settings.container is OutputContainer.OGG and media_info.subtitle_streams:
        issues.append(
            ValidationIssue(
                severity=ValidationSeverity.ERROR,
                code="subtitle_copy_incompatible",
                message="OGG output requires subtitle streams to be removed in MVP.",
                field_name="remove_subtitles",
            )
        )
        return issues

    if settings.container is OutputContainer.MP4:
        for stream in media_info.subtitle_streams:
            codec_name = (stream.codec_name or "").lower()
            if codec_name not in _MP4_SUBTITLE_CODECS:
                issues.append(
                    ValidationIssue(
                        severity=ValidationSeverity.ERROR,
                        code="subtitle_copy_incompatible",
                        message=(
                            f"Subtitle stream {stream.index} uses "
                            f"{stream.codec_name or 'unknown'}, which is not safe to copy into MP4."
                        ),
                        field_name="remove_subtitles",
                    )
                )

    return issues


def _validate_data_copy_compatibility(
    settings: OutputSettings,
    media_info: SourceMediaInfo,
) -> list[ValidationIssue]:
    if settings.stream_handling.remove_data or not media_info.data_streams:
        return []

    if settings.container is OutputContainer.MKV:
        return []

    return [
        ValidationIssue(
            severity=ValidationSeverity.ERROR,
            code="data_copy_incompatible",
            message=(
                f"{settings.container.value.upper()} output requires data streams to be removed "
                "in MVP."
            ),
            field_name="remove_data",
        )
    ]


def _estimate_audio_bits(settings: OutputSettings, media_info: SourceMediaInfo) -> int | None:
    mode = _effective_audio_mode(settings)
    duration_seconds = media_info.duration_seconds
    if duration_seconds is None:
        return None

    if mode is AudioMode.REMOVE or not media_info.audio_streams:
        return 0

    if mode is AudioMode.REENCODE:
        if settings.audio_options.bitrate is None:
            return None
        return int(
            settings.audio_options.bitrate * duration_seconds * len(media_info.audio_streams)
        )

    total_bitrate = 0
    for stream in media_info.audio_streams:
        if stream.bit_rate is None:
            return None
        total_bitrate += stream.bit_rate
    return int(total_bitrate * duration_seconds)


def _effective_audio_mode(settings: OutputSettings) -> AudioMode:
    if settings.stream_handling.remove_audio:
        return AudioMode.REMOVE
    return settings.audio_options.mode


def _source_resolution(media_info: SourceMediaInfo) -> Resolution | None:
    primary_video = media_info.primary_video_stream
    if primary_video is None or primary_video.width is None or primary_video.height is None:
        return None
    return Resolution(width=primary_video.width, height=primary_video.height)


def _video_bitrate_warning_issues(
    bitrate: int,
    settings: OutputSettings,
    media_info: SourceMediaInfo,
    field_name: str,
) -> list[ValidationIssue]:
    threshold = bitrate_warning_threshold(
        DefaultValidationService().resolve_output_resolution(settings, media_info)
    )
    if threshold is None or bitrate >= threshold:
        return []

    return [
        ValidationIssue(
            severity=ValidationSeverity.WARNING,
            code="video_bitrate_low",
            message=(
                f"Calculated video bitrate {_format_mbps(bitrate)} is low for the selected "
                f"resolution. Recommended minimum is {_format_mbps(threshold)}."
            ),
            field_name=field_name,
        )
    ]


def _validate_h26x_quality(
    *,
    crf: int,
    preset: str,
    profile: str,
    preset_options: Sequence[str],
    profile_options: Sequence[str],
    field_name: str,
) -> list[ValidationIssue]:
    issues: list[ValidationIssue] = []
    if not 0 <= crf <= 51:
        issues.append(
            ValidationIssue(
                severity=ValidationSeverity.ERROR,
                code="quality_crf_invalid",
                message="CRF must be between 0 and 51.",
                field_name=field_name,
            )
        )
    if preset not in preset_options:
        issues.append(
            ValidationIssue(
                severity=ValidationSeverity.ERROR,
                code="quality_preset_invalid",
                message="The selected encoder preset is not valid for this codec.",
                field_name=field_name,
            )
        )
    if profile not in profile_options:
        issues.append(
            ValidationIssue(
                severity=ValidationSeverity.ERROR,
                code="quality_profile_invalid",
                message="The selected encoder profile is not valid for this codec.",
                field_name=field_name,
            )
        )
    return issues


def _video_codec_label(codec: VideoCodec) -> str:
    labels = {
        VideoCodec.H264: "H.264",
        VideoCodec.H265: "H.265",
        VideoCodec.VP8: "VP8",
        VideoCodec.VP9: "VP9",
    }
    return labels[codec]


def _format_mbps(bit_rate: int) -> str:
    return f"{bit_rate / 1_000_000:.2f} Mbps"
