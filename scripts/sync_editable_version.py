from __future__ import annotations

import shutil
from importlib.metadata import PackageNotFoundError, distribution
from pathlib import Path

import tomllib


def main() -> int:
    project_root = Path(__file__).resolve().parents[1]
    target_version = _read_pyproject_version(project_root)
    try:
        dist = distribution("ffapp")
    except PackageNotFoundError:
        return 0

    dist_path = Path(dist._path)
    metadata_path = dist_path / "METADATA"
    if not metadata_path.is_file():
        return 0

    current_version = dist.version
    target_dist_path = dist_path
    if current_version != target_version:
        metadata_text = metadata_path.read_text(encoding="utf-8")
        metadata_text = metadata_text.replace(
            f"Version: {current_version}",
            f"Version: {target_version}",
            1,
        )
        metadata_path.write_text(metadata_text, encoding="utf-8")

        target_dist_path = dist_path.with_name(f"ffapp-{target_version}.dist-info")
        if target_dist_path != dist_path:
            if target_dist_path.exists():
                shutil.rmtree(target_dist_path)
            dist_path.rename(target_dist_path)

    for editable_path in target_dist_path.parent.glob("__editable__.ffapp-*.pth"):
        target_editable_path = editable_path.with_name(f"__editable__.ffapp-{target_version}.pth")
        if editable_path == target_editable_path:
            continue
        if target_editable_path.exists():
            target_editable_path.unlink()
        editable_path.rename(target_editable_path)

    return 0


def _read_pyproject_version(project_root: Path) -> str:
    payload = tomllib.loads((project_root / "pyproject.toml").read_text(encoding="utf-8"))
    return str(payload["project"]["version"])


if __name__ == "__main__":
    raise SystemExit(main())
