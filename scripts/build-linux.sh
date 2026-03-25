#!/usr/bin/env bash

set -euo pipefail

usage() {
  cat <<'EOF'
Usage: scripts/build-linux.sh [--dry-run] [--skip-archive]

Builds the Linux standalone bundle with pyside6-deploy and writes release artifacts to dist/.

Options:
  --dry-run       Show the generated deployment command without building the app.
  --skip-archive  Build the .dist folder but do not create a .tar.gz archive.
EOF
}

DRY_RUN=0
SKIP_ARCHIVE=0

for arg in "$@"; do
  case "$arg" in
    --dry-run)
      DRY_RUN=1
      ;;
    --skip-archive)
      SKIP_ARCHIVE=1
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      printf 'Unknown option: %s\n\n' "$arg" >&2
      usage >&2
      exit 1
      ;;
  esac
done

if [[ "$(uname -s)" != "Linux" ]]; then
  printf 'scripts/build-linux.sh must be run on Linux.\n' >&2
  exit 1
fi

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SPEC_FILE="$PROJECT_ROOT/pysidedeploy.spec"
DIST_DIR="$PROJECT_ROOT/dist"
ICON_FILE="$PROJECT_ROOT/assets/icons/linux/app-icon.png"

if [[ -n "${PYTHON_BIN:-}" ]]; then
  PYTHON="$PYTHON_BIN"
elif [[ -n "${VIRTUAL_ENV:-}" ]]; then
  PYTHON="$(command -v python)"
elif [[ -x "$PROJECT_ROOT/.venv/bin/python" ]]; then
  PYTHON="$PROJECT_ROOT/.venv/bin/python"
else
  PYTHON="$(command -v python3)"
fi

if [[ ! -x "$PYTHON" ]]; then
  printf 'Could not find a usable Python interpreter.\n' >&2
  exit 1
fi

PYTHON_VERSION="$("$PYTHON" - <<'PY'
import sys
print(f"{sys.version_info.major}.{sys.version_info.minor}")
PY
)"

if [[ "$PYTHON_VERSION" != "3.13" ]]; then
  printf 'Packaging currently requires Python 3.13. Found %s at %s\n' "$PYTHON_VERSION" "$PYTHON" >&2
  printf 'Create or activate a Python 3.13 virtual environment, then run the build again.\n' >&2
  exit 1
fi

PYSIDE_DEPLOY="${PYTHON%/python}/pyside6-deploy"
if [[ ! -x "$PYSIDE_DEPLOY" ]]; then
  PYSIDE_DEPLOY="$(command -v pyside6-deploy || true)"
fi

if [[ -z "$PYSIDE_DEPLOY" || ! -x "$PYSIDE_DEPLOY" ]]; then
  printf 'Could not find pyside6-deploy in the selected Python environment.\n' >&2
  exit 1
fi

if [[ ! -f "$SPEC_FILE" ]]; then
  printf 'Missing %s\n' "$SPEC_FILE" >&2
  exit 1
fi

export CCACHE_DISABLE="${CCACHE_DISABLE:-1}"

"$PYTHON" - <<'PY' "$SPEC_FILE" "$PYTHON" "$ICON_FILE"
from __future__ import annotations

import configparser
import sys
from pathlib import Path

spec_path = Path(sys.argv[1])
python_path = sys.argv[2]
icon_path = Path(sys.argv[3])

spec = configparser.ConfigParser()
spec.read(spec_path)

spec["python"]["python_path"] = python_path

spec["app"]["icon"] = str(icon_path) if icon_path.is_file() else ""

with spec_path.open("w", encoding="utf-8") as handle:
    spec.write(handle)
PY

readarray -t BUILD_META < <(
  "$PYTHON" - <<'PY' "$PROJECT_ROOT"
from __future__ import annotations

import configparser
import re
import sys
import tomllib
from pathlib import Path

project_root = Path(sys.argv[1])
pyproject = tomllib.loads((project_root / "pyproject.toml").read_text())
spec = configparser.ConfigParser()
spec.read(project_root / "pysidedeploy.spec")

project_name = pyproject["project"]["name"]
version = pyproject["project"]["version"]
app_title = spec["app"]["title"].strip() or project_name
artifact_name = re.sub(r"[^a-z0-9]+", "-", project_name.lower()).strip("-")

print(project_name)
print(version)
print(app_title)
print(artifact_name)
PY
)

PROJECT_NAME="${BUILD_META[0]}"
VERSION="${BUILD_META[1]}"
APP_TITLE="${BUILD_META[2]}"
ARTIFACT_NAME="${BUILD_META[3]}"

APP_DIST_DIR="$DIST_DIR/$APP_TITLE.dist"
ARCHIVE_PATH="$DIST_DIR/$ARTIFACT_NAME-$VERSION-linux.tar.gz"

mkdir -p "$DIST_DIR"

printf 'Using Python: %s\n' "$PYTHON"
printf 'Building %s %s\n' "$PROJECT_NAME" "$VERSION"

"$PYTHON" "$PROJECT_ROOT/scripts/sync_editable_version.py"

if [[ "$DRY_RUN" -eq 1 ]]; then
  "$PYSIDE_DEPLOY" -c "$SPEC_FILE" --dry-run -f
  exit 0
fi

rm -rf "$APP_DIST_DIR"
rm -f "$ARCHIVE_PATH"

"$PYSIDE_DEPLOY" -c "$SPEC_FILE" -f

if [[ ! -d "$APP_DIST_DIR" ]]; then
  printf 'Expected standalone bundle was not produced: %s\n' "$APP_DIST_DIR" >&2
  exit 1
fi

if [[ "$SKIP_ARCHIVE" -eq 1 ]]; then
  printf 'Standalone bundle created: %s\n' "$APP_DIST_DIR"
  exit 0
fi

tar -C "$DIST_DIR" -czf "$ARCHIVE_PATH" "$APP_TITLE.dist"

printf 'Archive created: %s\n' "$ARCHIVE_PATH"
