"""FFmpeg GUI application package."""

from __future__ import annotations

from importlib.metadata import PackageNotFoundError, version
from pathlib import Path

__all__ = ["get_app_version"]


def get_app_version() -> str:
    pyproject_version = _read_version_from_pyproject()
    if pyproject_version is not None:
        return pyproject_version

    try:
        return version("ffapp")
    except PackageNotFoundError:
        return "0.0.0"


def _read_version_from_pyproject() -> str | None:
    import tomllib

    project_root = Path(__file__).resolve().parents[2]
    pyproject_path = project_root / "pyproject.toml"
    if not pyproject_path.is_file():
        return None

    payload = tomllib.loads(pyproject_path.read_text(encoding="utf-8"))
    return str(payload["project"]["version"])
