from __future__ import annotations

import json
from pathlib import Path
from typing import Any

from ffapp.core.models import AppSettings, ExecutablePaths, OutputSettings
from ffapp.core.services import SettingsService
from ffapp.infrastructure.app_paths import settings_file_path
from ffapp.infrastructure.serialization import (
    SETTINGS_SCHEMA_VERSION,
    SettingsDecodeError,
    deserialize_output_settings,
    serialize_output_settings,
)


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
        try:
            last_used_settings = deserialize_output_settings(
                _mapping(payload.get("last_used_settings"))
            )
        except SettingsDecodeError:
            last_used_settings = OutputSettings()
        return AppSettings(
            schema_version=int(payload.get("schema_version", SETTINGS_SCHEMA_VERSION)),
            executable_paths=ExecutablePaths(
                ffmpeg=_optional_path(executable_paths.get("ffmpeg")),
                ffprobe=_optional_path(executable_paths.get("ffprobe")),
            ),
            last_used_settings=last_used_settings,
        )

    def save(self, settings: AppSettings) -> None:
        self._path.parent.mkdir(parents=True, exist_ok=True)
        payload = {
            "schema_version": SETTINGS_SCHEMA_VERSION,
            "executable_paths": {
                "ffmpeg": _optional_string(settings.executable_paths.ffmpeg),
                "ffprobe": _optional_string(settings.executable_paths.ffprobe),
            },
            "last_used_settings": serialize_output_settings(settings.last_used_settings),
        }
        self._path.write_text(json.dumps(payload, indent=2, sort_keys=True), encoding="utf-8")


def _optional_path(value: Any) -> Path | None:
    if value in (None, ""):
        return None
    return Path(str(value)).expanduser()


def _optional_string(value: Path | None) -> str | None:
    return None if value is None else str(value)


def _mapping(value: object) -> dict[str, Any]:
    if isinstance(value, dict):
        return value
    return {}
