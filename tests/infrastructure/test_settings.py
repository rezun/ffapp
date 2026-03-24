from __future__ import annotations

import unittest
from pathlib import Path

from ffapp.core.models import AppSettings, ExecutablePaths
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
                )
            )

            service.save(expected)
            actual = service.load()

        self.assertEqual(actual, expected)


if __name__ == "__main__":
    unittest.main()
