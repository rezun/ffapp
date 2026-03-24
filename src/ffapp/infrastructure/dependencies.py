from __future__ import annotations

import shutil
import subprocess
from pathlib import Path

from ffapp.core.models import (
    AppSettings,
    DependencyStatus,
    ExecutablePaths,
    ExecutableValidation,
)


class DependencyManager:
    def discover(self, settings: AppSettings) -> DependencyStatus:
        configured_paths = settings.executable_paths
        ffmpeg_path = self._resolve_candidate("ffmpeg", configured_paths.ffmpeg)
        ffprobe_path = self._resolve_candidate("ffprobe", configured_paths.ffprobe)
        return self.validate_paths(ExecutablePaths(ffmpeg=ffmpeg_path, ffprobe=ffprobe_path))

    def validate_paths(self, executable_paths: ExecutablePaths) -> DependencyStatus:
        ffmpeg_validation = self._validate_executable("ffmpeg", executable_paths.ffmpeg)
        ffprobe_validation = self._validate_executable("ffprobe", executable_paths.ffprobe)
        return DependencyStatus(ffmpeg=ffmpeg_validation, ffprobe=ffprobe_validation)

    def _resolve_candidate(self, name: str, configured_path: Path | None) -> Path | None:
        if configured_path is not None:
            return configured_path.expanduser()

        discovered_path = shutil.which(name)
        return Path(discovered_path) if discovered_path else None

    def _validate_executable(self, name: str, path: Path | None) -> ExecutableValidation:
        if path is None:
            return ExecutableValidation(
                name=name,
                path=None,
                is_valid=False,
                error=f"{name} was not found on PATH and is not configured.",
            )

        candidate = path.expanduser()
        if not candidate.exists():
            return ExecutableValidation(
                name=name,
                path=candidate,
                is_valid=False,
                error=f"{candidate} does not exist.",
            )

        if not candidate.is_file():
            return ExecutableValidation(
                name=name,
                path=candidate,
                is_valid=False,
                error=f"{candidate} is not a file.",
            )

        try:
            completed = subprocess.run(
                [str(candidate), "-version"],
                capture_output=True,
                text=True,
                timeout=5,
                check=False,
            )
        except OSError as exc:
            return ExecutableValidation(
                name=name,
                path=candidate,
                is_valid=False,
                error=str(exc),
            )
        except subprocess.TimeoutExpired:
            return ExecutableValidation(
                name=name,
                path=candidate,
                is_valid=False,
                error=f"{name} validation timed out.",
            )

        output = (completed.stdout or completed.stderr).strip() or None
        if completed.returncode != 0:
            return ExecutableValidation(
                name=name,
                path=candidate,
                is_valid=False,
                version_output=output,
                error=f"{name} exited with status {completed.returncode}.",
            )

        return ExecutableValidation(
            name=name,
            path=candidate,
            is_valid=True,
            version_output=output,
        )

