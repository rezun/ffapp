from __future__ import annotations

import json
from pathlib import Path
from typing import Any

from ffapp.core.models import AppSettings, ExecutablePaths
from ffapp.core.services import SettingsService
from ffapp.infrastructure.app_paths import settings_file_path


class JsonSettingsService(SettingsService):
    def __init__(self, path: Path | None = None) -> None:
        self._path = path or settings_file_path()

    @property
    def path(self) -> Path:
        return self._path

    def load(self) -> AppSettings:
        if not self._path.exists():
            return AppSettings()

        payload = json.loads(self._path.read_text(encoding="utf-8"))
        executable_paths = payload.get("executable_paths", {})
        return AppSettings(
            schema_version=int(payload.get("schema_version", 1)),
            executable_paths=ExecutablePaths(
                ffmpeg=_optional_path(executable_paths.get("ffmpeg")),
                ffprobe=_optional_path(executable_paths.get("ffprobe")),
            ),
        )

    def save(self, settings: AppSettings) -> None:
        self._path.parent.mkdir(parents=True, exist_ok=True)
        payload = {
            "schema_version": settings.schema_version,
            "executable_paths": {
                "ffmpeg": _optional_string(settings.executable_paths.ffmpeg),
                "ffprobe": _optional_string(settings.executable_paths.ffprobe),
            },
        }
        self._path.write_text(json.dumps(payload, indent=2, sort_keys=True), encoding="utf-8")


def _optional_path(value: Any) -> Path | None:
    if value in (None, ""):
        return None
    return Path(str(value)).expanduser()


def _optional_string(value: Path | None) -> str | None:
    return None if value is None else str(value)

