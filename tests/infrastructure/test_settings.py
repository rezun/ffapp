from __future__ import annotations

import unittest
from pathlib import Path

from ffapp.core.models import (
    AppSettings,
    AudioCodec,
    AudioMode,
    AudioOptions,
    ExecutablePaths,
    OutputContainer,
    OutputSettings,
    VideoCodec,
    Vp9QualityOptions,
)
from ffapp.infrastructure.settings import JsonSettingsService


class JsonSettingsServiceTests(unittest.TestCase):
    def test_load_returns_defaults_when_settings_file_is_missing(self) -> None:
        service = JsonSettingsService(path=Path("/tmp/this-file-should-not-exist-ffapp.json"))

        settings = service.load()

        self.assertIsNone(settings.executable_paths.ffmpeg)
        self.assertIsNone(settings.executable_paths.ffprobe)

    def test_save_round_trips_paths(self) -> None:
        import tempfile

        with tempfile.TemporaryDirectory() as temp_dir:
            settings_path = Path(temp_dir) / "settings.json"
            service = JsonSettingsService(path=settings_path)
            expected = AppSettings(
                executable_paths=ExecutablePaths(
                    ffmpeg=Path("/usr/local/bin/ffmpeg"),
                    ffprobe=Path("/usr/local/bin/ffprobe"),
                ),
                last_used_settings=OutputSettings(
                    container=OutputContainer.MKV,
                    video_codec=VideoCodec.VP9,
                    audio_options=AudioOptions(
                        mode=AudioMode.REENCODE,
                        codec=AudioCodec.OPUS,
                        bitrate=192_000,
                        remix_to_stereo=True,
                    ),
                    target_quality=Vp9QualityOptions(),
                ),
            )

            service.save(expected)
            actual = service.load()

        self.assertEqual(actual, expected)

    def test_load_defaults_last_used_settings_when_legacy_payload_omits_it(self) -> None:
        import tempfile

        with tempfile.TemporaryDirectory() as temp_dir:
            settings_path = Path(temp_dir) / "settings.json"
            settings_path.write_text(
                """
                {
                  "schema_version": 1,
                  "executable_paths": {
                    "ffmpeg": "/usr/local/bin/ffmpeg",
                    "ffprobe": "/usr/local/bin/ffprobe"
                  }
                }
                """.strip(),
                encoding="utf-8",
            )
            service = JsonSettingsService(path=settings_path)

            actual = service.load()

        self.assertEqual(actual.last_used_settings, OutputSettings())


if __name__ == "__main__":
    unittest.main()
