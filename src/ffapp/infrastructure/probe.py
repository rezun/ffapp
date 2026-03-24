from __future__ import annotations

import json
import subprocess
from fractions import Fraction
from pathlib import Path
from typing import Any

from ffapp.core.models import (
    AudioStreamInfo,
    DataStreamInfo,
    SourceMediaInfo,
    SubtitleStreamInfo,
    VideoStreamInfo,
)
from ffapp.core.services import MediaProbeService


class MediaProbeError(RuntimeError):
    """Raised when ffprobe probing fails."""


class FfprobeMediaProbeService(MediaProbeService):
    def __init__(self, ffprobe_path: Path) -> None:
        self._ffprobe_path = ffprobe_path

    def probe(self, source_path: Path) -> SourceMediaInfo:
        if not source_path.exists():
            raise MediaProbeError(f"Source file does not exist: {source_path}")

        completed = subprocess.run(
            [
                str(self._ffprobe_path),
                "-v",
                "error",
                "-show_format",
                "-show_streams",
                "-of",
                "json",
                str(source_path),
            ],
            capture_output=True,
            text=True,
            timeout=20,
            check=False,
        )

        if completed.returncode != 0:
            error_output = (
                completed.stderr.strip()
                or completed.stdout.strip()
                or "Unknown ffprobe error"
            )
            raise MediaProbeError(error_output)

        try:
            payload = json.loads(completed.stdout)
        except json.JSONDecodeError as exc:
            raise MediaProbeError("ffprobe returned invalid JSON output.") from exc

        return parse_media_info(source_path=source_path, payload=payload)


def parse_media_info(source_path: Path, payload: dict[str, Any]) -> SourceMediaInfo:
    format_payload = _expect_mapping(payload.get("format"))
    streams_payload = payload.get("streams")
    if streams_payload is None:
        streams_payload = []
    if not isinstance(streams_payload, list):
        raise MediaProbeError("ffprobe JSON did not include a valid streams list.")

    video_streams: list[VideoStreamInfo] = []
    audio_streams: list[AudioStreamInfo] = []
    subtitle_streams: list[SubtitleStreamInfo] = []
    data_streams: list[DataStreamInfo] = []

    for raw_stream in streams_payload:
        stream = _expect_mapping(raw_stream)
        tags = _expect_mapping(stream.get("tags"))
        codec_type = str(stream.get("codec_type", "")).lower()
        index = _parse_int(stream.get("index")) or 0
        codec_name = _optional_str(stream.get("codec_name"))
        bit_rate = _parse_int(stream.get("bit_rate"))
        language = _optional_str(tags.get("language"))

        if codec_type == "video":
            video_streams.append(
                VideoStreamInfo(
                    index=index,
                    codec_name=codec_name,
                    bit_rate=bit_rate,
                    width=_parse_int(stream.get("width")),
                    height=_parse_int(stream.get("height")),
                    frame_rate=_parse_frame_rate(stream),
                )
            )
            continue

        if codec_type == "audio":
            audio_streams.append(
                AudioStreamInfo(
                    index=index,
                    codec_name=codec_name,
                    bit_rate=bit_rate,
                    channels=_parse_int(stream.get("channels")),
                    language=language,
                )
            )
            continue

        if codec_type == "subtitle":
            subtitle_streams.append(
                SubtitleStreamInfo(
                    index=index,
                    codec_name=codec_name,
                    language=language,
                )
            )
            continue

        if codec_type in {"data", "attachment"}:
            data_streams.append(DataStreamInfo(index=index, codec_name=codec_name))

    return SourceMediaInfo(
        source_path=source_path,
        container_format=_optional_str(format_payload.get("format_name")),
        duration_seconds=_parse_float(format_payload.get("duration")),
        bit_rate=_parse_int(format_payload.get("bit_rate")),
        video_streams=tuple(video_streams),
        audio_streams=tuple(audio_streams),
        subtitle_streams=tuple(subtitle_streams),
        data_streams=tuple(data_streams),
    )


def _parse_frame_rate(stream: dict[str, Any]) -> float | None:
    for key in ("avg_frame_rate", "r_frame_rate"):
        raw_value = _optional_str(stream.get(key))
        if raw_value is None:
            continue
        value = _parse_fraction(raw_value)
        if value is not None:
            return value
    return None


def _parse_fraction(value: str) -> float | None:
    if value in {"0/0", "N/A", ""}:
        return None
    try:
        fraction = Fraction(value)
    except (ValueError, ZeroDivisionError):
        return None
    return float(fraction)


def _parse_int(value: Any) -> int | None:
    if value in (None, "", "N/A"):
        return None
    try:
        return int(str(value))
    except ValueError:
        return None


def _parse_float(value: Any) -> float | None:
    if value in (None, "", "N/A"):
        return None
    try:
        return float(str(value))
    except ValueError:
        return None


def _optional_str(value: Any) -> str | None:
    if value in (None, ""):
        return None
    return str(value)


def _expect_mapping(value: Any) -> dict[str, Any]:
    if value is None:
        return {}
    if not isinstance(value, dict):
        raise MediaProbeError("ffprobe JSON had an unexpected structure.")
    return value
