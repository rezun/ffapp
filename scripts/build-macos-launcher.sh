#!/usr/bin/env bash
set -euo pipefail
if [[ $# -ne 3 ]]; then
    echo "Usage: $0 <osx-rid> <version> <output-app>" >&2
    exit 1
fi
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RID="$1" VERSION="$2" BUNDLE="$3"
case "$RID" in osx-arm64) ARCH=arm64 ;; osx-x64) ARCH=x86_64 ;; *) echo "Unsupported RID: $RID" >&2; exit 1 ;; esac
mkdir -p "$BUNDLE/Contents/MacOS" "$BUNDLE/Contents/Resources"
python3 - "$ROOT" "$VERSION" "$BUNDLE" <<'PY'
import plistlib
import sys
from pathlib import Path
root, version, bundle = Path(sys.argv[1]), sys.argv[2], Path(sys.argv[3])
with (root / 'packaging/ffrux/macos/Info.plist').open('rb') as stream:
    info = plistlib.load(stream)
info.update(CFBundleIdentifier='com.ffapp.ffrux.launcher', CFBundleExecutable='FFRuxLauncher',
            CFBundleVersion=version, CFBundleShortVersionString=version)
with (bundle / 'Contents/Info.plist').open('wb') as stream:
    plistlib.dump(info, stream)
PY
cp "$ROOT/packaging/ffrux/macos/ffrux.icns" "$BUNDLE/Contents/Resources/"
xcrun clang -arch "$ARCH" -mmacosx-version-min=11.0 -fobjc-arc -Wall -Wextra -Werror \
    -Wno-unused-parameter -framework AppKit \
    "$ROOT/packaging/ffrux/macos/launcher/main.m" -o "$BUNDLE/Contents/MacOS/FFRuxLauncher"
codesign --force --sign - "$BUNDLE"
