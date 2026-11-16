#!/usr/bin/env bash
# Shared payload layout for local builds and Velopack releases. No Velopack tool required.
set -euo pipefail

if [[ $# -ne 4 ]]; then
    echo "Usage: $0 <rid> <version> <self-contained:true|false> <output-directory>" >&2
    exit 1
fi
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RID="$1"
VERSION="$2"
SELF_CONTAINED="$3"
OUTPUT="$4"
[[ "$OUTPUT" == /* ]] || OUTPUT="$PWD/$OUTPUT"
if [[ -e "$OUTPUT" ]]; then
    echo "Output directory already exists: $OUTPUT" >&2
    exit 1
fi
case "$SELF_CONTAINED" in true|false) ;; *) echo "Invalid self-contained value" >&2; exit 1 ;; esac
case "$RID" in win-x64|win-arm64|osx-x64|osx-arm64|linux-x64|linux-arm64) ;; *) echo "Unsupported RID: $RID" >&2; exit 1 ;; esac

make_bundle() {
    local app="$1" key="$2" bundle="$3"
    mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
    sed "s/VERSION_PLACEHOLDER/$VERSION/g" "$ROOT/packaging/$key/macos/Info.plist" > "$bundle/Contents/Info.plist"
    cp "$ROOT/packaging/$key/macos/$key.icns" "$bundle/Contents/Resources/"
    dotnet publish "$ROOT/src/$app/$app.csproj" -c Release -r "$RID" \
        --self-contained "$SELF_CONTAINED" -p:Version="$VERSION" \
        -o "$bundle/Contents/MacOS" -v quiet
    chmod +x "$bundle/Contents/MacOS/$app"
}

if [[ "$RID" == osx-* ]]; then
    make_bundle FFApp ffapp "$OUTPUT/FFApp.app"
    make_bundle FFRux ffrux "$OUTPUT/FFApp.app/Contents/Helpers/FFRux.app"
    # Only the FFRux.app launcher in Applications should appear in Finder's "Open With" menu.
    # The launcher hands files to this helper directly, so the helper needs no document types.
    plutil -remove CFBundleDocumentTypes "$OUTPUT/FFApp.app/Contents/Helpers/FFRux.app/Contents/Info.plist"
    bash "$ROOT/scripts/build-macos-launcher.sh" "$RID" "$VERSION" "$OUTPUT/FFApp.app/Contents/Helpers/FFRuxLauncher.app"
else
    dotnet publish "$ROOT/src/FFApp/FFApp.csproj" -c Release -r "$RID" \
        --self-contained "$SELF_CONTAINED" -p:Version="$VERSION" -o "$OUTPUT" -v quiet
    dotnet publish "$ROOT/src/FFRux/FFRux.csproj" -c Release -r "$RID" \
        --self-contained "$SELF_CONTAINED" -p:Version="$VERSION" -o "$OUTPUT/companion/FFRux" -v quiet
    if [[ "$RID" == linux-* ]]; then
        chmod +x "$OUTPUT/FFApp" "$OUTPUT/companion/FFRux/FFRux"
    fi
fi

python3 "$ROOT/scripts/verify-bundle.py" "$OUTPUT" "$RID" "$VERSION"
