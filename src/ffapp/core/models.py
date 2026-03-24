from __future__ import annotations

from dataclasses import dataclass, field
from enum import StrEnum
from pathlib import Path
from typing import Self


class OutputContainer(StrEnum):
    MP4 = "mp4"
    MKV = "mkv"
    WEBM = "webm"
    OGG = "ogg"


class VideoCodec(StrEnum):
    H264 = "h264"
    H265 = "h265"
    VP8 = "vp8"
    VP9 = "vp9"


class AudioCodec(StrEnum):
    MP3 = "mp3"
    AAC = "aac"
    VORBIS = "vorbis"
    OPUS = "opus"


class EncodingStrategy(StrEnum):
    TARGET_FILE_SIZE = "target_file_size"
    TARGET_BITRATE = "target_bitrate"
    TARGET_QUALITY = "target_quality"


class AudioMode(StrEnum):
    COPY = "copy"
    REENCODE = "reencode"
    REMOVE = "remove"


class ResolutionPreset(StrEnum):
    ORIGINAL = "original"
    P720 = "720p"
    P1080 = "1080p"
    P1440 = "1440p"
    P4K = "4k"


class JobState(StrEnum):
    PENDING = "pending"
    RUNNING = "running"
    COMPLETED = "completed"
    FAILED = "failed"
    CANCELED = "canceled"


class ValidationSeverity(StrEnum):
    ERROR = "error"
    WARNING = "warning"


@dataclass(slots=True, frozen=True)
class Resolution:
    width: int
    height: int


@dataclass(slots=True, frozen=True)
class TargetFileSizeOptions:
    target_size_mb: int
    two_pass: bool = False
    muxing_overhead_ratio: float = 0.015


@dataclass(slots=True, frozen=True)
class TargetBitrateOptions:
    video_bitrate: int
    two_pass: bool = False


@dataclass(slots=True, frozen=True)
class H264EncoderOptions:
    codec: VideoCodec = VideoCodec.H264
    preset: str = "medium"
    profile: str = "high"


@dataclass(slots=True, frozen=True)
class H265EncoderOptions:
    codec: VideoCodec = VideoCodec.H265
    preset: str = "medium"
    profile: str = "main"


@dataclass(slots=True, frozen=True)
class VpxEncoderOptions:
    codec: VideoCodec = VideoCodec.VP9
    cpu_used: int = 2


@dataclass(slots=True, frozen=True)
class H264QualityOptions:
    codec: VideoCodec = VideoCodec.H264
    crf: int = 23
    preset: str = "medium"
    profile: str = "high"


@dataclass(slots=True, frozen=True)
class H265QualityOptions:
    codec: VideoCodec = VideoCodec.H265
    crf: int = 28
    preset: str = "medium"
    profile: str = "main"


@dataclass(slots=True, frozen=True)
class Vp8QualityOptions:
    codec: VideoCodec = VideoCodec.VP8
    quality: int = 31
    two_pass: bool = False


@dataclass(slots=True, frozen=True)
class Vp9QualityOptions:
    codec: VideoCodec = VideoCodec.VP9
    quality: int = 32
    two_pass: bool = False


VideoEncoderOptions = H264EncoderOptions | H265EncoderOptions | VpxEncoderOptions
QualityOptions = H264QualityOptions | H265QualityOptions | Vp8QualityOptions | Vp9QualityOptions


def default_vpx_cpu_used(video_codec: VideoCodec) -> int:
    return 4 if video_codec is VideoCodec.VP8 else 2


@dataclass(slots=True, frozen=True)
class AudioOptions:
    mode: AudioMode = AudioMode.COPY
    codec: AudioCodec | None = None
    bitrate: int | None = None
    remix_to_stereo: bool = False


@dataclass(slots=True, frozen=True)
class StreamHandlingOptions:
    remove_subtitles: bool = False
    remove_data: bool = False
    remove_audio: bool = False


@dataclass(slots=True, frozen=True)
class VideoStreamInfo:
    index: int
    codec_name: str | None = None
    bit_rate: int | None = None
    width: int | None = None
    height: int | None = None
    frame_rate: float | None = None


@dataclass(slots=True, frozen=True)
class AudioStreamInfo:
    index: int
    codec_name: str | None = None
    bit_rate: int | None = None
    channels: int | None = None
    language: str | None = None


@dataclass(slots=True, frozen=True)
class SubtitleStreamInfo:
    index: int
    codec_name: str | None = None
    language: str | None = None


@dataclass(slots=True, frozen=True)
class DataStreamInfo:
    index: int
    codec_name: str | None = None


@dataclass(slots=True, frozen=True)
class SourceMediaInfo:
    source_path: Path
    container_format: str | None = None
    duration_seconds: float | None = None
    bit_rate: int | None = None
    video_streams: tuple[VideoStreamInfo, ...] = field(default_factory=tuple)
    audio_streams: tuple[AudioStreamInfo, ...] = field(default_factory=tuple)
    subtitle_streams: tuple[SubtitleStreamInfo, ...] = field(default_factory=tuple)
    data_streams: tuple[DataStreamInfo, ...] = field(default_factory=tuple)

    @property
    def primary_video_stream(self) -> VideoStreamInfo | None:
        return self.video_streams[0] if self.video_streams else None

    @property
    def stream_count(self) -> int:
        return (
            len(self.video_streams)
            + len(self.audio_streams)
            + len(self.subtitle_streams)
            + len(self.data_streams)
        )


@dataclass(slots=True, frozen=True)
class OutputSettings:
    container: OutputContainer = OutputContainer.MP4
    video_codec: VideoCodec = VideoCodec.H264
    encoding_strategy: EncodingStrategy = EncodingStrategy.TARGET_QUALITY
    resolution_preset: ResolutionPreset = ResolutionPreset.ORIGINAL
    video_encoder_options: VideoEncoderOptions | None = None
    audio_options: AudioOptions = field(default_factory=AudioOptions)
    stream_handling: StreamHandlingOptions = field(default_factory=StreamHandlingOptions)
    target_file_size: TargetFileSizeOptions = field(
        default_factory=lambda: TargetFileSizeOptions(target_size_mb=50)
    )
    target_bitrate: TargetBitrateOptions = field(
        default_factory=lambda: TargetBitrateOptions(video_bitrate=3_000_000)
    )
    target_quality: QualityOptions = field(default_factory=H264QualityOptions)


@dataclass(slots=True, frozen=True)
class ExecutablePaths:
    ffmpeg: Path | None = None
    ffprobe: Path | None = None

    @classmethod
    def empty(cls) -> Self:
        return cls()


@dataclass(slots=True, frozen=True)
class ExecutableValidation:
    name: str
    path: Path | None
    is_valid: bool
    version_output: str | None = None
    error: str | None = None


@dataclass(slots=True, frozen=True)
class DependencyStatus:
    ffmpeg: ExecutableValidation
    ffprobe: ExecutableValidation

    @property
    def is_ready(self) -> bool:
        return self.ffmpeg.is_valid and self.ffprobe.is_valid

    @property
    def executable_paths(self) -> ExecutablePaths:
        return ExecutablePaths(
            ffmpeg=self.ffmpeg.path if self.ffmpeg.is_valid else None,
            ffprobe=self.ffprobe.path if self.ffprobe.is_valid else None,
        )


@dataclass(slots=True, frozen=True)
class PresetDefinition:
    name: str
    settings: OutputSettings = field(default_factory=OutputSettings)
    schema_version: int = 1


@dataclass(slots=True, frozen=True)
class AppSettings:
    executable_paths: ExecutablePaths = field(default_factory=ExecutablePaths.empty)
    last_used_settings: OutputSettings = field(default_factory=OutputSettings)
    schema_version: int = 2


@dataclass(slots=True, frozen=True)
class JobDefinition:
    source_path: Path
    output_path: Path
    container: OutputContainer
    video_codec: VideoCodec
    encoding_strategy: EncodingStrategy
    video_encoder_options: VideoEncoderOptions | None = None
    audio_options: AudioOptions = field(default_factory=AudioOptions)
    stream_handling: StreamHandlingOptions = field(default_factory=StreamHandlingOptions)
    resolution_preset: ResolutionPreset = ResolutionPreset.ORIGINAL
    target_file_size: TargetFileSizeOptions | None = None
    target_bitrate: TargetBitrateOptions | None = None
    target_quality: QualityOptions | None = None


@dataclass(slots=True, frozen=True)
class GeneratedCommand:
    executable_path: Path
    arguments: tuple[str, ...]
    display_string: str


@dataclass(slots=True, frozen=True)
class ProcessProgress:
    elapsed_seconds: float
    processed_seconds: float | None = None
    percentage: float | None = None
    speed: float | None = None


@dataclass(slots=True, frozen=True)
class ProcessExecutionResult:
    command: GeneratedCommand
    state: JobState
    return_code: int
    elapsed_seconds: float
    stderr_output: str


@dataclass(slots=True, frozen=True)
class JobProgress:
    state: JobState
    pass_index: int
    pass_count: int
    elapsed_seconds: float
    processed_seconds: float | None = None
    percentage: float | None = None
    speed: float | None = None


@dataclass(slots=True, frozen=True)
class JobResult:
    job: JobDefinition
    state: JobState
    commands: tuple[GeneratedCommand, ...]
    process_results: tuple[ProcessExecutionResult, ...]
    elapsed_seconds: float
    log_output: str


@dataclass(slots=True, frozen=True)
class QueuedJob:
    job: JobDefinition
    media_info: SourceMediaInfo
    commands: tuple[GeneratedCommand, ...]
    validation: ValidationResult


@dataclass(slots=True, frozen=True)
class QueueItem:
    id: str
    queued_job: QueuedJob
    state: JobState = JobState.PENDING
    progress: JobProgress | None = None
    result: JobResult | None = None
    failure_message: str | None = None


@dataclass(slots=True, frozen=True)
class ValidationIssue:
    severity: ValidationSeverity
    code: str
    message: str
    field_name: str | None = None


@dataclass(slots=True, frozen=True)
class ValidationResult:
    issues: tuple[ValidationIssue, ...] = field(default_factory=tuple)

    @property
    def has_errors(self) -> bool:
        return any(issue.severity is ValidationSeverity.ERROR for issue in self.issues)

    @property
    def has_warnings(self) -> bool:
        return any(issue.severity is ValidationSeverity.WARNING for issue in self.issues)

    @property
    def errors(self) -> tuple[ValidationIssue, ...]:
        return tuple(issue for issue in self.issues if issue.severity is ValidationSeverity.ERROR)

    @property
    def warnings(self) -> tuple[ValidationIssue, ...]:
        return tuple(issue for issue in self.issues if issue.severity is ValidationSeverity.WARNING)
