from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from pathlib import Path
from typing import Self


class OutputContainer(str, Enum):
    MP4 = "mp4"
    MKV = "mkv"
    OGG = "ogg"


class VideoCodec(str, Enum):
    H264 = "h264"
    H265 = "h265"
    VP8 = "vp8"
    VP9 = "vp9"


class AudioCodec(str, Enum):
    MP3 = "mp3"
    AAC = "aac"
    VORBIS = "vorbis"
    OPUS = "opus"


class EncodingStrategy(str, Enum):
    TARGET_FILE_SIZE = "target_file_size"
    TARGET_BITRATE = "target_bitrate"
    TARGET_QUALITY = "target_quality"


class AudioMode(str, Enum):
    COPY = "copy"
    REENCODE = "reencode"
    REMOVE = "remove"


class ResolutionPreset(str, Enum):
    ORIGINAL = "original"
    P720 = "720p"
    P1080 = "1080p"
    P1440 = "1440p"
    P4K = "4k"


class JobState(str, Enum):
    PENDING = "pending"
    RUNNING = "running"
    COMPLETED = "completed"
    FAILED = "failed"
    CANCELED = "canceled"


class ValidationSeverity(str, Enum):
    ERROR = "error"
    WARNING = "warning"


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
class TargetQualityOptions:
    codec: VideoCodec
    crf: int | None = None
    preset: str | None = None
    profile: str | None = None
    speed: int | None = None


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


@dataclass(slots=True, frozen=True)
class SubtitleStreamInfo:
    index: int
    codec_name: str | None = None


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
    container: OutputContainer
    video_codec: VideoCodec
    encoding_strategy: EncodingStrategy
    resolution_preset: ResolutionPreset = ResolutionPreset.ORIGINAL
    audio_options: AudioOptions = field(default_factory=AudioOptions)
    stream_handling: StreamHandlingOptions = field(default_factory=StreamHandlingOptions)
    target_file_size: TargetFileSizeOptions | None = None
    target_bitrate: TargetBitrateOptions | None = None
    target_quality: TargetQualityOptions | None = None
    schema_version: int = 1


@dataclass(slots=True, frozen=True)
class AppSettings:
    executable_paths: ExecutablePaths = field(default_factory=ExecutablePaths.empty)
    schema_version: int = 1


@dataclass(slots=True, frozen=True)
class JobDefinition:
    source_path: Path
    output_path: Path
    container: OutputContainer
    video_codec: VideoCodec
    encoding_strategy: EncodingStrategy
    audio_options: AudioOptions = field(default_factory=AudioOptions)
    stream_handling: StreamHandlingOptions = field(default_factory=StreamHandlingOptions)
    resolution_preset: ResolutionPreset = ResolutionPreset.ORIGINAL
    target_file_size: TargetFileSizeOptions | None = None
    target_bitrate: TargetBitrateOptions | None = None
    target_quality: TargetQualityOptions | None = None


@dataclass(slots=True, frozen=True)
class GeneratedCommand:
    executable_path: Path
    arguments: tuple[str, ...]
    display_string: str


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

