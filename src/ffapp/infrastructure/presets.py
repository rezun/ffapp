from __future__ import annotations

import json
import logging
from pathlib import Path
from typing import Any, cast

from ffapp.core.models import PresetDefinition
from ffapp.core.services import PresetService
from ffapp.infrastructure.app_paths import presets_file_path
from ffapp.infrastructure.serialization import (
    PRESET_LIBRARY_SCHEMA_VERSION,
    PRESET_SCHEMA_VERSION,
    PresetDecodeError,
    deserialize_preset,
    serialize_preset,
)

LOGGER = logging.getLogger(__name__)


class PresetStorageError(ValueError):
    pass


class JsonPresetService(PresetService):
    def __init__(self, path: Path | None = None) -> None:
        self._path = path or presets_file_path()

    @property
    def path(self) -> Path:
        return self._path

    def load_all(self) -> tuple[PresetDefinition, ...]:
        if not self._path.exists():
            return ()

        payload = json.loads(self._path.read_text(encoding="utf-8"))
        schema_version = int(payload.get("schema_version", PRESET_LIBRARY_SCHEMA_VERSION))
        if schema_version != PRESET_LIBRARY_SCHEMA_VERSION:
            LOGGER.warning(
                "Skipping preset library with unsupported schema version %s at %s.",
                schema_version,
                self._path,
            )
            return ()

        presets: list[PresetDefinition] = []
        for entry in _preset_entries(payload):
            try:
                presets.append(deserialize_preset(entry))
            except PresetDecodeError as exc:
                LOGGER.warning("Skipping invalid preset entry: %s", exc)
        return tuple(sorted(presets, key=lambda preset: preset.name.casefold()))

    def save(self, preset: PresetDefinition) -> None:
        name = _normalized_name(preset.name)
        presets = list(self.load_all())
        updated_preset = PresetDefinition(
            name=name,
            settings=preset.settings,
            schema_version=PRESET_SCHEMA_VERSION,
        )

        replaced = False
        for index, existing in enumerate(presets):
            if existing.name.casefold() == name.casefold():
                presets[index] = updated_preset
                replaced = True
                break
        if not replaced:
            presets.append(updated_preset)

        self._write_presets(presets)

    def rename(self, current_name: str, new_name: str) -> PresetDefinition:
        current = _normalized_name(current_name)
        target = _normalized_name(new_name)
        presets = list(self.load_all())
        renamed_preset: PresetDefinition | None = None

        for preset in presets:
            if (
                preset.name.casefold() == target.casefold()
                and preset.name.casefold() != current.casefold()
            ):
                raise PresetStorageError(f"A preset named '{target}' already exists.")

        for index, preset in enumerate(presets):
            if preset.name.casefold() == current.casefold():
                renamed_preset = PresetDefinition(
                    name=target,
                    settings=preset.settings,
                    schema_version=PRESET_SCHEMA_VERSION,
                )
                presets[index] = renamed_preset
                break

        if renamed_preset is None:
            raise PresetStorageError(f"No preset named '{current}' exists.")

        self._write_presets(presets)
        return renamed_preset

    def delete(self, name: str) -> None:
        target = _normalized_name(name)
        presets = list(self.load_all())
        filtered = [preset for preset in presets if preset.name.casefold() != target.casefold()]
        if len(filtered) == len(presets):
            raise PresetStorageError(f"No preset named '{target}' exists.")
        self._write_presets(filtered)

    def _write_presets(self, presets: list[PresetDefinition]) -> None:
        self._path.parent.mkdir(parents=True, exist_ok=True)
        payload = {
            "schema_version": PRESET_LIBRARY_SCHEMA_VERSION,
            "presets": [
                serialize_preset(preset)
                for preset in sorted(presets, key=lambda item: item.name.casefold())
            ],
        }
        self._path.write_text(json.dumps(payload, indent=2, sort_keys=True), encoding="utf-8")


def _normalized_name(value: str) -> str:
    name = value.strip()
    if not name:
        raise PresetStorageError("Preset name cannot be empty.")
    return name


def _preset_entries(payload: dict[str, Any]) -> list[dict[str, Any]]:
    entries = payload.get("presets", [])
    if not isinstance(entries, list):
        LOGGER.warning("Preset library payload does not contain a list of presets.")
        return []

    result: list[dict[str, Any]] = []
    for entry in entries:
        if isinstance(entry, dict):
            result.append(cast(dict[str, Any], entry))
    return result
