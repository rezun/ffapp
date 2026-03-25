from __future__ import annotations

import sys
from pathlib import Path


def _add_src_to_path() -> None:
    project_root = Path(__file__).resolve().parent
    src_path = project_root / "src"
    src_path_str = str(src_path)
    if src_path_str not in sys.path:
        sys.path.insert(0, src_path_str)


_add_src_to_path()

from ffapp.app.bootstrap import main


if __name__ == "__main__":
    raise SystemExit(main())
