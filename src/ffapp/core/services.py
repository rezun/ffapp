from __future__ import annotations

from collections.abc import Callable, Iterable, Sequence
from pathlib import Path
from typing import Protocol

from ffapp.core.models import (
    AppSettings,
    AudioCodec,
    DependencyStatus,
    GeneratedCommand,
    JobDefinition,
    JobProgress,
    JobResult,
    OutputContainer,
    OutputSettings,
    PresetDefinition,
    ProcessExecutionResult,
    ProcessProgress,
    QueuedJob,
    QueueItem,
    Resolution,
    SourceMediaInfo,
    ValidationResult,
    VideoCodec,
)


class MediaProbeService(Protocol):
    def probe(self, source_path: Path) -> SourceMediaInfo: ...


class SettingsService(Protocol):
    def load(self) -> AppSettings: ...

    def save(self, settings: AppSettings) -> None: ...


class PresetService(Protocol):
    def load_all(self) -> Sequence[PresetDefinition]: ...

    def save(self, preset: PresetDefinition) -> None: ...

    def rename(self, current_name: str, new_name: str) -> PresetDefinition: ...

    def delete(self, name: str) -> None: ...


class CompatibilityService(Protocol):
    def allowed_video_codecs(self, container: OutputContainer) -> Sequence[VideoCodec]: ...

    def allowed_audio_codecs(self, container: OutputContainer) -> Sequence[AudioCodec]: ...

    def validate(
        self,
        settings: OutputSettings,
        media_info: SourceMediaInfo,
    ) -> ValidationResult: ...


class ValidationService(Protocol):
    def resolve_output_resolution(
        self,
        settings: OutputSettings,
        media_info: SourceMediaInfo,
    ) -> Resolution | None: ...

    def validate(
        self,
        settings: OutputSettings,
        media_info: SourceMediaInfo,
    ) -> ValidationResult: ...


class CommandBuilder(Protocol):
    def build(
        self,
        job: JobDefinition,
        media_info: SourceMediaInfo,
    ) -> Sequence[GeneratedCommand]: ...


class ProcessRunner(Protocol):
    def run(
        self,
        command: GeneratedCommand,
        *,
        expected_duration_seconds: float | None = None,
        progress_callback: Callable[[ProcessProgress], None] | None = None,
        log_callback: Callable[[str], None] | None = None,
    ) -> ProcessExecutionResult: ...

    def cancel(self) -> None: ...


class JobExecutionService(Protocol):
    def execute(
        self,
        job: JobDefinition,
        media_info: SourceMediaInfo,
        *,
        progress_callback: Callable[[JobProgress], None] | None = None,
        log_callback: Callable[[str], None] | None = None,
    ) -> JobResult: ...

    def cancel(self) -> None: ...


class QueueService(Protocol):
    def enqueue(self, jobs: Iterable[QueuedJob]) -> Sequence[QueueItem]: ...

    def items(self) -> Sequence[QueueItem]: ...

    def has_pending(self) -> bool: ...

    def remove(self, item_ids: Iterable[str]) -> Sequence[QueueItem]: ...

    def execute_all(
        self,
        *,
        item_started_callback: Callable[[QueueItem], None] | None = None,
        item_progress_callback: Callable[[QueueItem], None] | None = None,
        item_finished_callback: Callable[[QueueItem], None] | None = None,
        log_callback: Callable[[QueueItem, str], None] | None = None,
    ) -> Sequence[QueueItem]: ...

    def cancel_active(self) -> None: ...


class DependencyService(Protocol):
    def discover(self, settings: AppSettings) -> DependencyStatus: ...
