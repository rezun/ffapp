from __future__ import annotations

from collections.abc import Iterable, Sequence
from pathlib import Path
from typing import Protocol

from ffapp.core.models import (
    AppSettings,
    DependencyStatus,
    GeneratedCommand,
    JobDefinition,
    PresetDefinition,
    SourceMediaInfo,
    ValidationResult,
)


class MediaProbeService(Protocol):
    def probe(self, source_path: Path) -> SourceMediaInfo: ...


class SettingsService(Protocol):
    def load(self) -> AppSettings: ...

    def save(self, settings: AppSettings) -> None: ...


class PresetService(Protocol):
    def load_all(self) -> Sequence[PresetDefinition]: ...

    def save(self, preset: PresetDefinition) -> None: ...


class CompatibilityService(Protocol):
    def validate(self, job: JobDefinition) -> ValidationResult: ...


class ValidationService(Protocol):
    def validate(self, job: JobDefinition, media_info: SourceMediaInfo) -> ValidationResult: ...


class CommandBuilder(Protocol):
    def build(self, job: JobDefinition, media_info: SourceMediaInfo) -> Sequence[GeneratedCommand]: ...


class ProcessRunner(Protocol):
    def run(self, command: GeneratedCommand) -> int: ...


class JobExecutionService(Protocol):
    def execute(self, job: JobDefinition) -> int: ...


class QueueService(Protocol):
    def enqueue(self, jobs: Iterable[JobDefinition]) -> None: ...

    def cancel_active(self) -> None: ...


class DependencyService(Protocol):
    def discover(self, settings: AppSettings) -> DependencyStatus: ...

