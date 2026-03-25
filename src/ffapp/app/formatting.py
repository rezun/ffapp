from __future__ import annotations

from collections.abc import Iterable
from pathlib import Path
from urllib.parse import unquote, urlparse

from ffapp.core.models import (
    AudioCodec,
    AudioMode,
    DependencyStatus,
    EncodingStrategy,
    ExecutableValidation,
    GeneratedCommand,
    H264QualityOptions,
    H265QualityOptions,
    JobDefinition,
    JobProgress,
    JobResult,
    JobState,
    OutputContainer,
    OutputSettings,
    QualityOptions,
    QueueItem,
    ResolutionPreset,
    SourceMediaInfo,
    ValidationResult,
    VideoCodec,
    Vp8QualityOptions,
    Vp9QualityOptions,
    VpxEncoderOptions,
)
from ffapp.core.validation import DefaultValidationService


def _build_media_summary_text(media_info: SourceMediaInfo) -> str:
    primary_video = media_info.primary_video_stream
    lines = [
        f"Source: {media_info.source_path}",
        f"Format: {media_info.container_format or 'Unknown'}",
        f"Duration: {_format_duration(media_info.duration_seconds)}",
        f"Overall bitrate: {_format_bitrate(media_info.bit_rate)}",
        "",
        f"Video streams: {len(media_info.video_streams)}",
        f"Audio streams: {len(media_info.audio_streams)}",
        f"Subtitle streams: {len(media_info.subtitle_streams)}",
        f"Data streams: {len(media_info.data_streams)}",
    ]

    if primary_video is not None:
        lines.extend(
            [
                "",
                "Primary video stream",
                f"Codec: {primary_video.codec_name or 'Unknown'}",
                f"Resolution: {_format_dimensions(primary_video.width, primary_video.height)}",
                f"Frame rate: {_format_frame_rate(primary_video.frame_rate)}",
                f"Bitrate: {_format_bitrate(primary_video.bit_rate)}",
            ]
        )

    if media_info.audio_streams:
        lines.extend(["", "Audio streams detail"])
        for stream in media_info.audio_streams:
            lines.extend(
                [
                    f"Track {stream.index}",
                    f"Codec: {stream.codec_name or 'Unknown'}",
                    f"Language: {_format_language(stream.language)}",
                    f"Channels: {_format_channels(stream.channels)}",
                    f"Bitrate: {_format_audio_bitrate(stream.bit_rate)}",
                    "",
                ]
            )
        lines.pop()

    if media_info.video_streams and len(media_info.video_streams) > 1:
        lines.extend(
            [
                "",
                "Warning: multiple video streams detected. "
                "MVP uses the first stream only.",
            ]
        )

    return "\n".join(lines)


def _format_dependency_status(status: DependencyStatus) -> str:
    return "\n".join(
        [
            _format_single_dependency(status.ffmpeg),
            _format_single_dependency(status.ffprobe),
        ]
    )


def _format_single_dependency(validation: object) -> str:
    if not isinstance(validation, ExecutableValidation):
        return "Unknown dependency state"

    if validation.is_valid:
        version = (
            validation.version_output.splitlines()[0]
            if validation.version_output
            else "Validated"
        )
        return f"{validation.name}: ready at {validation.path}\n{version}"
    return f"{validation.name}: {validation.error or 'Not configured'}"


def _extract_path_from_mime_data(mime_data: object) -> Path | None:
    if mime_data is None:
        return None

    urls_getter = getattr(mime_data, "urls", None)
    if callable(urls_getter):
        urls = urls_getter()
        if urls:
            for url in urls:
                if getattr(url, "isLocalFile", lambda: False)():
                    local_file = getattr(url, "toLocalFile", lambda: "")()
                    if local_file:
                        return Path(local_file)

    text_getter = getattr(mime_data, "text", None)
    if callable(text_getter):
        raw_text = text_getter().strip()
        if not raw_text:
            return None
        if raw_text.startswith("file://"):
            parsed = urlparse(raw_text)
            return Path(unquote(parsed.path))
        return Path(raw_text)

    return None


def _format_duration(duration_seconds: float | None) -> str:
    if duration_seconds is None:
        return "Unknown"
    total_seconds = int(duration_seconds)
    hours, remainder = divmod(total_seconds, 3600)
    minutes, seconds = divmod(remainder, 60)
    return f"{hours:02d}:{minutes:02d}:{seconds:02d}"


def _format_bitrate(bit_rate: int | None) -> str:
    if bit_rate is None:
        return "Unknown"
    return f"{bit_rate / 1_000_000:.2f} Mbps"


def _format_audio_bitrate(bit_rate: int | None) -> str:
    if bit_rate is None:
        return "Unknown"
    return f"{bit_rate / 1_000:.0f} kbps"


def _format_dimensions(width: int | None, height: int | None) -> str:
    if width is None or height is None:
        return "Unknown"
    return f"{width}x{height}"


def _format_frame_rate(frame_rate: float | None) -> str:
    if frame_rate is None:
        return "Unknown"
    return f"{frame_rate:.2f} fps"


def _format_channels(channels: int | None) -> str:
    if channels is None:
        return "Unknown"
    if channels == 1:
        return "1 channel"
    return f"{channels} channels"


def _format_language(language: str | None) -> str:
    if language is None:
        return "Unknown"
    return language


def _video_codec_label(codec: object) -> str:
    labels = {
        VideoCodec.H264: "H.264",
        VideoCodec.H265: "H.265",
        VideoCodec.VP8: "VP8",
        VideoCodec.VP9: "VP9",
    }
    if isinstance(codec, VideoCodec):
        return labels[codec]
    return str(codec)


def _audio_codec_label(codec: object) -> str:
    labels = {
        AudioCodec.AAC: "AAC",
        AudioCodec.MP3: "MP3",
        AudioCodec.VORBIS: "Vorbis",
        AudioCodec.OPUS: "Opus",
    }
    if isinstance(codec, AudioCodec):
        return labels[codec]
    return str(codec)


def _audio_mode_label(mode: AudioMode) -> str:
    labels = {
        AudioMode.COPY: "Copy All Audio Streams",
        AudioMode.REENCODE: "Re-encode All Audio Streams",
        AudioMode.REMOVE: "Remove All Audio Streams",
    }
    return labels[mode]


def _resolution_preset_label(preset: ResolutionPreset) -> str:
    labels = {
        ResolutionPreset.ORIGINAL: "Original Source Resolution",
        ResolutionPreset.P720: "720p",
        ResolutionPreset.P1080: "1080p",
        ResolutionPreset.P1440: "1440p",
        ResolutionPreset.P4K: "4K",
    }
    return labels[preset]


def _format_output_overview(
    settings: OutputSettings,
    media_info: SourceMediaInfo | None,
    validation_service: DefaultValidationService,
) -> str:
    resolution_text = _resolution_preset_label(settings.resolution_preset)
    if media_info is not None:
        resolved = validation_service.resolve_output_resolution(settings, media_info)
        if resolved is not None:
            resolution_text = f"{resolved.width}x{resolved.height}"

    audio_text = "Copy all audio streams"
    if settings.audio_options.mode is AudioMode.REMOVE:
        audio_text = "Remove all audio streams"
    elif settings.audio_options.mode is AudioMode.REENCODE:
        bitrate_kbps = (
            f"{settings.audio_options.bitrate // 1_000} kbps"
            if settings.audio_options.bitrate is not None
            else "custom bitrate"
        )
        remix_text = " stereo remix" if settings.audio_options.remix_to_stereo else ""
        audio_text = (
            f"{_audio_codec_label(settings.audio_options.codec or AudioCodec.AAC)} "
            f"{bitrate_kbps}{remix_text}"
        )

    strategy_text = {
        EncodingStrategy.TARGET_FILE_SIZE: (
            f"Target size {settings.target_file_size.target_size_mb} MB"
            f"{' • 2-pass' if settings.target_file_size.two_pass else ''}"
        ),
        EncodingStrategy.TARGET_BITRATE: (
            f"Target bitrate {settings.target_bitrate.video_bitrate // 1_000} kbps"
            f"{' • 2-pass' if settings.target_bitrate.two_pass else ''}"
        ),
        EncodingStrategy.TARGET_QUALITY: _format_quality_summary(
            settings.target_quality,
            settings.video_encoder_options,
        ),
    }[settings.encoding_strategy]

    return " | ".join(
        (
            f"Container: {settings.container.upper()}",
            f"Video: {_video_codec_label(settings.video_codec)}",
            f"Resolution: {resolution_text}",
            f"Audio: {audio_text}",
            strategy_text,
        )
    )


def _format_quality_summary(
    options: QualityOptions,
    video_encoder_options: object,
) -> str:
    if isinstance(options, H264QualityOptions | H265QualityOptions):
        return (
            f"Quality: CRF {options.crf} • Preset {options.preset} • Profile {options.profile}"
        )
    cpu_used = "default"
    if isinstance(video_encoder_options, VpxEncoderOptions):
        cpu_used = str(video_encoder_options.cpu_used)
    suffix = " • 2-pass" if options.two_pass else ""
    return f"Quality: {options.quality} • CPU Used {cpu_used}{suffix}"


def _format_validation_result(result: ValidationResult) -> str:
    if not result.issues:
        return "Current settings are valid for the loaded source."

    lines: list[str] = []
    if result.errors:
        lines.append("Errors")
        lines.extend(f"- {issue.message}" for issue in result.errors)
    if result.warnings:
        if lines:
            lines.append("")
        lines.append("Warnings")
        lines.extend(f"- {issue.message}" for issue in result.warnings)
    return "\n".join(lines)


def _format_command_preview_text(
    commands: Iterable[GeneratedCommand],
    *,
    single_command: bool,
) -> str:
    command_list = tuple(commands)
    if not command_list:
        return ""

    if len(command_list) == 1:
        return command_list[0].display_string

    if single_command:
        return " && ".join(command.display_string for command in command_list)

    sections: list[str] = []
    for index, command in enumerate(command_list, start=1):
        sections.append(f"Pass {index}")
        sections.append(command.display_string)
    return "\n\n".join(sections)


def _job_state_label(state: JobState) -> str:
    labels = {
        JobState.PENDING: "Pending",
        JobState.RUNNING: "Running",
        JobState.COMPLETED: "Completed",
        JobState.FAILED: "Failed",
        JobState.CANCELED: "Canceled",
    }
    return labels[state]


def _format_job_progress(progress: JobProgress) -> str:
    details = [
        f"State: {_job_state_label(progress.state)}",
        f"Pass: {progress.pass_index} of {progress.pass_count}",
        f"Elapsed: {_format_precise_duration(progress.elapsed_seconds)}",
        f"Processed: {_format_precise_duration(progress.processed_seconds)}",
        f"Progress: {_format_percentage(progress.percentage)}",
        f"Speed: {_format_speed(progress.speed)}",
    ]
    return " | ".join(details)


def _format_job_result_summary(result: JobResult) -> str:
    return (
        f"State: {_job_state_label(result.state)} | "
        f"Elapsed: {_format_precise_duration(result.elapsed_seconds)} | "
        f"Commands Run: {len(result.process_results)} of {len(result.commands)}"
    )


def _format_precise_duration(seconds: float | None) -> str:
    if seconds is None:
        return "Unknown"
    total_seconds = int(seconds)
    tenths = int((seconds - total_seconds) * 10)
    hours, remainder = divmod(total_seconds, 3600)
    minutes, remaining_seconds = divmod(remainder, 60)
    return f"{hours:02d}:{minutes:02d}:{remaining_seconds:02d}.{tenths}"


def _format_percentage(value: float | None) -> str:
    if value is None:
        return "Unknown"
    return f"{value:.1f}%"


def _format_speed(value: float | None) -> str:
    if value is None:
        return "Unknown"
    return f"{value:.2f}x"


def _format_queue_summary(items: Iterable[QueueItem]) -> str:
    item_list = tuple(items)
    if not item_list:
        return "Queue is empty."

    counts = {
        state: sum(1 for item in item_list if item.state is state)
        for state in JobState
    }
    return (
        f"Items: {len(item_list)} | "
        f"Pending: {counts[JobState.PENDING]} | "
        f"Running: {counts[JobState.RUNNING]} | "
        f"Completed: {counts[JobState.COMPLETED]} | "
        f"Failed: {counts[JobState.FAILED]} | "
        f"Canceled: {counts[JobState.CANCELED]}"
    )


def _format_queue_item_label(item: QueueItem) -> str:
    progress = ""
    if item.progress is not None and item.progress.percentage is not None:
        progress = f" | {_format_percentage(item.progress.percentage)}"
    return (
        f"{item.queued_job.job.source_path.name} -> "
        f"{item.queued_job.job.output_path.name} | "
        f"{_job_state_label(item.state)}{progress}"
    )


def _format_queue_item_details(item: QueueItem) -> str:
    lines = [
        f"Source: {item.queued_job.job.source_path}",
        f"Output: {item.queued_job.job.output_path}",
        f"State: {_job_state_label(item.state)}",
        f"Passes: {len(item.queued_job.commands)}",
    ]
    if item.progress is not None:
        lines.append(_format_job_progress(item.progress))
    elif item.result is not None:
        lines.append(_format_job_result_summary(item.result))

    if item.queued_job.validation.has_warnings:
        lines.append(f"Warnings: {len(item.queued_job.validation.warnings)}")
    if item.failure_message:
        lines.append(f"Failure: {item.failure_message}")
    return "\n".join(lines)


def _suggest_queue_output_path(
    source_path: Path,
    container: OutputContainer,
    *,
    reserved_outputs: set[Path],
) -> Path:
    candidate = source_path.with_suffix(f".{container.value}")
    if not candidate.exists() and candidate.resolve() not in reserved_outputs:
        return candidate

    counter = 1
    while True:
        numbered_candidate = candidate.with_name(
            f"{candidate.stem}-{counter}{candidate.suffix}"
        )
        if not numbered_candidate.exists() and numbered_candidate.resolve() not in reserved_outputs:
            return numbered_candidate
        counter += 1


def _build_job_definition(
    *,
    source_path: Path,
    output_path: Path,
    settings: OutputSettings,
) -> JobDefinition:
    return JobDefinition(
        source_path=source_path,
        output_path=output_path,
        container=settings.container,
        video_codec=settings.video_codec,
        encoding_strategy=settings.encoding_strategy,
        video_encoder_options=settings.video_encoder_options,
        audio_options=settings.audio_options,
        stream_handling=settings.stream_handling,
        resolution_preset=settings.resolution_preset,
        target_file_size=settings.target_file_size,
        target_bitrate=settings.target_bitrate,
        target_quality=settings.target_quality,
    )


__all__ = [
    "_audio_codec_label",
    "_audio_mode_label",
    "_build_job_definition",
    "_build_media_summary_text",
    "_extract_path_from_mime_data",
    "_format_command_preview_text",
    "_format_dependency_status",
    "_format_output_overview",
    "_format_queue_summary",
    "_format_validation_result",
    "_job_state_label",
    "_resolution_preset_label",
    "_suggest_queue_output_path",
    "_video_codec_label",
]
