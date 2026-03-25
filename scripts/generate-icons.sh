#!/usr/bin/env bash

set -euo pipefail

usage() {
  cat <<'EOF'
Usage: scripts/generate-icons.sh [source-image]

Generates macOS, Windows, and Linux app icons from a square PNG source image.
Defaults to logo.png in the project root.
EOF
}

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  usage
  exit 0
fi

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SOURCE_IMAGE="${1:-$PROJECT_ROOT/logo.png}"
ICON_ROOT="$PROJECT_ROOT/assets/icons"
MACOS_ICON_PNG="$ICON_ROOT/macos/app-icon.png"
MACOS_ICON_ICNS="$ICON_ROOT/macos/app-icon.icns"
WINDOWS_ICON="$ICON_ROOT/windows/app-icon.ico"
LINUX_ICON="$ICON_ROOT/linux/app-icon.png"

if [[ ! -f "$SOURCE_IMAGE" ]]; then
  printf 'Missing source image: %s\n' "$SOURCE_IMAGE" >&2
  exit 1
fi

if [[ "$(sips -g pixelWidth -g pixelHeight "$SOURCE_IMAGE" | awk '/pixelWidth:/ {w=$2} /pixelHeight:/ {h=$2} END {print (w==h) ? "square" : "not-square"}')" != "square" ]]; then
  printf 'Source image must be square.\n' >&2
  exit 1
fi

mkdir -p "$ICON_ROOT/macos" "$ICON_ROOT/windows" "$ICON_ROOT/linux"
rm -f "$MACOS_ICON_PNG" "$MACOS_ICON_ICNS" "$WINDOWS_ICON" "$LINUX_ICON"

sips -z 1024 1024 "$SOURCE_IMAGE" --out "$MACOS_ICON_PNG" >/dev/null
magick "$SOURCE_IMAGE" -background none -define icon:auto-resize=16,24,32,48,64,128,256 "$WINDOWS_ICON"
sips -z 512 512 "$SOURCE_IMAGE" --out "$LINUX_ICON" >/dev/null

TEMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TEMP_DIR"' EXIT

for size in 16 32 64 128 256 512 1024; do
  sips -z "$size" "$size" "$SOURCE_IMAGE" --out "$TEMP_DIR/$size.png" >/dev/null
done

"${PYTHON_BIN:-python3}" - <<'PY' "$TEMP_DIR" "$MACOS_ICON_ICNS"
from __future__ import annotations

import struct
import sys
from pathlib import Path

temp_dir = Path(sys.argv[1])
output_path = Path(sys.argv[2])
icon_types = [
    ("icp4", 16),
    ("icp5", 32),
    ("icp6", 64),
    ("ic07", 128),
    ("ic08", 256),
    ("ic09", 512),
    ("ic10", 1024),
]

chunks: list[bytes] = []
for icon_type, size in icon_types:
    png_data = (temp_dir / f"{size}.png").read_bytes()
    chunks.append(icon_type.encode("ascii") + struct.pack(">I", len(png_data) + 8) + png_data)

body = b"".join(chunks)
output_path.write_bytes(b"icns" + struct.pack(">I", len(body) + 8) + body)
PY

printf 'Generated:\n'
printf '  %s\n' "$MACOS_ICON_PNG"
printf '  %s\n' "$MACOS_ICON_ICNS"
printf '  %s\n' "$WINDOWS_ICON"
printf '  %s\n' "$LINUX_ICON"
