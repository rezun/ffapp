from __future__ import annotations

import os
import shlex
from collections.abc import Sequence
from pathlib import Path

from ffapp.core.models import (
    AudioCodec,
    AudioMode,
    EncodingStrategy,
    GeneratedCommand,
    H264EncoderOptions,
    H264QualityOptions,
    H265EncoderOptions,
    H265QualityOptions,
    JobDefinition,
    OutputContainer,
    Resolution,
    ResolutionPreset,
    SourceMediaInfo,
    VideoCodec,
    Vp8QualityOptions,
    Vp9QualityOptions,
    VpxEncoderOptions,
    default_vpx_cpu_used,
)
from ffapp.core.services import CommandBuilder
from ffapp.core.validation import PRESET_RESOLUTIONS, estimate_audio_bits
_NULL_OUTPUT_PATH = "NUL" if os.name == "nt" else "/dev/null"


class CommandBuildError(ValueError):
    """Raised when a command cannot be generated from the current job."""


class DefaultCommandBuilder(CommandBuilder):
    def __init__(self, executable_path: Path) -> None:
        self._executable_path = executable_path

    def build(
        self,
        job: JobDefinition,
        media_info: SourceMediaInfo,
    ) -> Sequence[GeneratedCommand]:
        input_args = ["-hide_banner", "-y", "-i", str(job.source_path)]
        mapping_args = _build_mapping_arguments(job, media_info)
        stream_args = _build_stream_arguments(job, media_info)

        if _uses_two_pass(job):
            bitrate = None
            if job.encoding_strategy is not EncodingStrategy.TARGET_QUALITY:
                bitrate = _resolve_video_bitrate(job, media_info)
            pass_log_path = _pass_log_path(job.output_path)
            first_pass_args = [
                *input_args,
                *mapping_args,
                *_build_video_codec_arguments(job, media_info, bitrate=bitrate, pass_index=1),
                "-pass",
                "1",
                "-passlogfile",
                str(pass_log_path),
                "-an",
                "-sn",
                "-dn",
                "-f",
                "null",
                _NULL_OUTPUT_PATH,
            ]
            second_pass_args = [
                *input_args,
                *mapping_args,
                *stream_args,
                *_build_video_codec_arguments(job, media_info, bitrate=bitrate, pass_index=2),
                "-pass",
                "2",
                "-passlogfile",
                str(pass_log_path),
                str(job.output_path),
            ]
            return (
                _create_generated_command(self._executable_path, first_pass_args),
                _create_generated_command(self._executable_path, second_pass_args),
            )

        arguments = [
            *input_args,
            *mapping_args,
            *stream_args,
            *_build_video_codec_arguments(job, media_info),
            str(job.output_path),
        ]
        return (_create_generated_command(self._executable_path, arguments),)


def suggest_output_path(source_path: Path, container: OutputContainer) -> Path:
    candidate = source_path.with_suffix(f".{container.value}")
    if not candidate.exists():
        return candidate

    counter = 1
    while True:
        numbered_candidate = candidate.with_name(
            f"{candidate.stem}-{counter}{candidate.suffix}"
        )
        if not numbered_candidate.exists():
            return numbered_candidate
        counter += 1


def _create_generated_command(executable_path: Path, arguments: list[str]) -> GeneratedCommand:
    command_parts = [str(executable_path), *arguments]
    return GeneratedCommand(
        executable_path=executable_path,
        arguments=tuple(arguments),
        display_string=shlex.join(command_parts),
    )


def _build_mapping_arguments(
    job: JobDefinition,
    media_info: SourceMediaInfo,
) -> list[str]:
    arguments = ["-map", "0"]
    for stream in media_info.video_streams[1:]:
        arguments.extend(["-map", f"-0:{stream.index}"])

    if _effective_audio_mode(job) is AudioMode.REMOVE:
        arguments.extend(["-map", "-0:a?"])
    if job.stream_handling.remove_subtitles:
        arguments.extend(["-map", "-0:s?"])
    if job.stream_handling.remove_data:
        arguments.extend(["-map", "-0:d?"])
    return arguments


def _build_stream_arguments(job: JobDefinition, media_info: SourceMediaInfo) -> list[str]:
    arguments: list[str] = []

    audio_mode = _effective_audio_mode(job)
    if audio_mode is AudioMode.COPY:
        arguments.extend(["-c:a", "copy"])
    elif audio_mode is AudioMode.REENCODE:
        arguments.extend(_build_audio_reencode_arguments(job))

    if not job.stream_handling.remove_subtitles and media_info.subtitle_streams:
        arguments.extend(["-c:s", "copy"])
    if not job.stream_handling.remove_data and media_info.data_streams:
        arguments.extend(["-c:d", "copy"])
    return arguments


def _build_audio_reencode_arguments(job: JobDefinition) -> list[str]:
    codec = job.audio_options.codec
    bitrate = job.audio_options.bitrate
    if codec is None or bitrate is None or bitrate <= 0:
        raise CommandBuildError("Audio re-encode mode requires a codec and positive bitrate.")

    arguments = [
        "-c:a",
        _audio_encoder_name(codec),
        "-b:a",
        str(bitrate),
    ]
    if job.audio_options.remix_to_stereo:
        arguments.extend(["-ac", "2"])
    return arguments


def _build_video_codec_arguments(
    job: JobDefinition,
    media_info: SourceMediaInfo,
    *,
    bitrate: int | None = None,
    pass_index: int | None = None,
) -> list[str]:
    arguments = ["-c:v", _video_encoder_name(job.video_codec)]

    scale_filter = _video_scale_filter(job, media_info)
    if scale_filter is not None:
        arguments.extend(["-vf", scale_filter])

    if job.encoding_strategy is EncodingStrategy.TARGET_QUALITY:
        arguments.extend(_quality_arguments(job, pass_index=pass_index))
        return arguments

    arguments.extend(_video_encoder_tuning_arguments(job, pass_index=pass_index))
    resolved_bitrate = bitrate
    if resolved_bitrate is None:
        resolved_bitrate = _resolve_video_bitrate(job, media_info)
    arguments.extend(["-b:v", _format_video_bitrate_argument(resolved_bitrate)])
    return arguments


def _quality_arguments(
    job: JobDefinition,
    *,
    pass_index: int | None = None,
) -> list[str]:
    quality = job.target_quality
    if quality is None:
        raise CommandBuildError("Quality encoding requires codec-specific quality settings.")

    if isinstance(quality, H264QualityOptions):
        return [
            "-crf",
            str(quality.crf),
            "-preset",
            quality.preset,
            "-profile:v",
            quality.profile,
        ]

    if isinstance(quality, H265QualityOptions):
        return [
            "-crf",
            str(quality.crf),
            "-preset",
            quality.preset,
            "-profile:v",
            quality.profile,
        ]

    if isinstance(quality, Vp8QualityOptions | Vp9QualityOptions):
        return [
            "-crf",
            str(quality.quality),
            "-b:v",
            "0",
            *_vpx_encoder_tuning_arguments(
                job,
                _resolve_vpx_encoder_options(job.video_codec, job.video_encoder_options),
                pass_index=pass_index,
            ),
        ]

    raise CommandBuildError("Unsupported quality settings were provided.")


def _video_encoder_tuning_arguments(
    job: JobDefinition,
    *,
    pass_index: int | None = None,
) -> list[str]:
    if job.video_codec is VideoCodec.H264:
        encoder = job.video_encoder_options or H264EncoderOptions()
        if not isinstance(encoder, H264EncoderOptions):
            raise CommandBuildError("H.264 bitrate-based encoding requires preset and profile.")
        return [
            "-preset",
            encoder.preset,
            "-profile:v",
            encoder.profile,
        ]

    if job.video_codec is VideoCodec.H265:
        encoder = job.video_encoder_options or H265EncoderOptions()
        if not isinstance(encoder, H265EncoderOptions):
            raise CommandBuildError("H.265 bitrate-based encoding requires preset and profile.")
        return [
            "-preset",
            encoder.preset,
            "-profile:v",
            encoder.profile,
        ]

    return _vpx_encoder_tuning_arguments(
        job,
        _resolve_vpx_encoder_options(job.video_codec, job.video_encoder_options),
        pass_index=pass_index,
    )


def _resolve_vpx_encoder_options(
    video_codec: VideoCodec,
    configured_options: object,
) -> VpxEncoderOptions:
    if isinstance(configured_options, VpxEncoderOptions):
        if configured_options.codec is video_codec:
            return configured_options
        raise CommandBuildError("VP8/VP9 encoding requires matching VPx encoder options.")
    if configured_options is not None:
        raise CommandBuildError("VP8/VP9 encoding requires VPx encoder options.")

    return VpxEncoderOptions(
        codec=video_codec,
        cpu_used=default_vpx_cpu_used(video_codec),
    )


def _vpx_encoder_tuning_arguments(
    job: JobDefinition,
    encoder: VpxEncoderOptions,
    *,
    pass_index: int | None = None,
) -> list[str]:
    cpu_used = encoder.cpu_used
    if _uses_two_pass(job) and pass_index == 1:
        cpu_used = max(cpu_used, 4)
    arguments = [
        "-deadline",
        "good",
        "-cpu-used",
        str(cpu_used),
    ]
    if job.video_codec is VideoCodec.VP9:
        arguments.extend(["-row-mt", "1"])
    if _uses_two_pass(job) and pass_index == 2:
        arguments.extend(["-auto-alt-ref", "1"])
    return arguments


def _resolve_video_bitrate(job: JobDefinition, media_info: SourceMediaInfo | None) -> int:
    if job.encoding_strategy is EncodingStrategy.TARGET_BITRATE:
        if job.target_bitrate is None or job.target_bitrate.video_bitrate <= 0:
            raise CommandBuildError("Target bitrate mode requires a positive video bitrate.")
        return job.target_bitrate.video_bitrate

    if job.encoding_strategy is EncodingStrategy.TARGET_FILE_SIZE:
        if job.target_file_size is None:
            raise CommandBuildError("Target file size mode requires file size options.")
        if media_info is None:
            raise CommandBuildError("Target file size mode requires probed media info.")
        if media_info.duration_seconds is None or media_info.duration_seconds <= 0:
            raise CommandBuildError("Target file size mode requires a positive source duration.")

        estimated_audio_bits = _estimate_audio_bits(job, media_info)
        total_budget_bits = job.target_file_size.target_size_mb * 1024 * 1024 * 8
        usable_budget_bits = int(
            total_budget_bits * (1 - job.target_file_size.muxing_overhead_ratio)
        )
        video_budget_bits = usable_budget_bits - estimated_audio_bits
        if video_budget_bits <= 0:
            raise CommandBuildError("The target file size leaves no bitrate budget for video.")
        return int(video_budget_bits / media_info.duration_seconds)

    raise CommandBuildError("Bitrate resolution was requested for a quality-based encode.")


def _estimate_audio_bits(job: JobDefinition, media_info: SourceMediaInfo) -> int:
    result = estimate_audio_bits(
        _effective_audio_mode(job),
        job.audio_options.bitrate,
        media_info.audio_streams,
        media_info.duration_seconds,
    )
    if result is None:
        raise CommandBuildError(
            "Audio size estimation failed: missing duration, bitrate, or stream information."
        )
    return result


def _video_scale_filter(job: JobDefinition, media_info: SourceMediaInfo) -> str | None:
    if job.resolution_preset is ResolutionPreset.ORIGINAL:
        return "scale=trunc(iw/2)*2:trunc(ih/2)*2"

    resolution = _target_bounding_box(job, media_info)
    return (
        f"scale={resolution.width}:{resolution.height}:"
        "force_original_aspect_ratio=decrease:force_divisible_by=2"
    )


def _target_bounding_box(job: JobDefinition, media_info: SourceMediaInfo) -> Resolution:
    try:
        resolution = PRESET_RESOLUTIONS[job.resolution_preset]
    except KeyError as exc:  # pragma: no cover - defensive only
        raise CommandBuildError("Unsupported resolution preset.") from exc
    primary_video = media_info.primary_video_stream
    if (
        primary_video is not None
        and primary_video.width is not None
        and primary_video.height is not None
        and primary_video.height > primary_video.width
    ):
        return Resolution(width=resolution.height, height=resolution.width)
    return resolution


def _effective_audio_mode(job: JobDefinition) -> AudioMode:
    if job.stream_handling.remove_audio:
        return AudioMode.REMOVE
    return job.audio_options.mode


def _uses_two_pass(job: JobDefinition) -> bool:
    if job.encoding_strategy is EncodingStrategy.TARGET_FILE_SIZE:
        return bool(job.target_file_size and job.target_file_size.two_pass)
    if job.encoding_strategy is EncodingStrategy.TARGET_BITRATE:
        return bool(job.target_bitrate and job.target_bitrate.two_pass)
    if job.encoding_strategy is EncodingStrategy.TARGET_QUALITY:
        quality = job.target_quality
        if isinstance(quality, (Vp8QualityOptions, Vp9QualityOptions)):
            return quality.two_pass
    return False


def _pass_log_path(output_path: Path) -> Path:
    return output_path.with_name(f".{output_path.stem}-ffpass")


def _video_encoder_name(codec: VideoCodec) -> str:
    encoders = {
        VideoCodec.H264: "libx264",
        VideoCodec.H265: "libx265",
        VideoCodec.VP8: "libvpx",
        VideoCodec.VP9: "libvpx-vp9",
    }
    return encoders[codec]


def _audio_encoder_name(codec: AudioCodec) -> str:
    encoders = {
        AudioCodec.AAC: "aac",
        AudioCodec.MP3: "libmp3lame",
        AudioCodec.VORBIS: "libvorbis",
        AudioCodec.OPUS: "libopus",
    }
    return encoders[codec]


def _format_video_bitrate_argument(bit_rate: int) -> str:
    if bit_rate <= 0:
        raise CommandBuildError("Video bitrate must be positive.")
    return f"{round(bit_rate / 1_000):d}k"
