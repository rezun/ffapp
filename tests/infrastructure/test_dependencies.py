from __future__ import annotations

import subprocess
import unittest
from pathlib import Path
from unittest.mock import patch

from ffapp.core.models import AppSettings, ExecutablePaths
from ffapp.infrastructure.dependencies import DependencyManager


class DependencyManagerTests(unittest.TestCase):
    def test_discover_uses_configured_paths_before_path_lookup(self) -> None:
        manager = DependencyManager()
        settings = AppSettings(
            executable_paths=ExecutablePaths(
                ffmpeg=Path("/custom/ffmpeg"),
                ffprobe=Path("/custom/ffprobe"),
            )
        )

        with patch.object(Path, "exists", return_value=True), patch.object(
            Path, "is_file", return_value=True
        ), patch("subprocess.run") as run_mock, patch("shutil.which") as which_mock:
            run_mock.return_value = subprocess.CompletedProcess(
                args=[],
                returncode=0,
                stdout="ffmpeg version test\n",
                stderr="",
            )

            status = manager.discover(settings)

        which_mock.assert_not_called()
        self.assertTrue(status.is_ready)
        self.assertEqual(status.ffmpeg.path, Path("/custom/ffmpeg"))
        self.assertEqual(status.ffprobe.path, Path("/custom/ffprobe"))

    def test_validate_paths_returns_error_for_missing_file(self) -> None:
        manager = DependencyManager()

        with patch.object(Path, "exists", return_value=False):
            status = manager.validate_paths(
                ExecutablePaths(
                    ffmpeg=Path("/missing/ffmpeg"),
                    ffprobe=Path("/missing/ffprobe"),
                )
            )

        self.assertFalse(status.is_ready)
        self.assertIn("does not exist", status.ffmpeg.error or "")
        self.assertIn("does not exist", status.ffprobe.error or "")


if __name__ == "__main__":
    unittest.main()

