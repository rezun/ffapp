#!/usr/bin/env bash
# Add the Spotlight launcher to the same macOS installer.
# Velopack still owns the FFApp payload, full packages and delta packages.
set -euo pipefail
if [[ $# -ne 4 ]]; then
    echo "Usage: $0 <FFApp.app> <release-directory> <channel> <version>" >&2
    exit 1
fi
APP="$1" RELEASES="$2" CHANNEL="$3" VERSION="$4"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
[[ "$APP" == /* ]] || APP="$PWD/$APP"
[[ "$RELEASES" == /* ]] || RELEASES="$PWD/$RELEASES"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/launcher-root"
cp -R "$APP/Contents/Helpers/FFRuxLauncher.app" "$WORK/launcher-root/FFRux.app"

# Keep Velopack's component exactly as produced, including its update helper.
pkgutil --expand "$RELEASES/FFApp-$CHANNEL-Setup.pkg" "$WORK/expanded"
python3 - "$WORK/expanded" "$WORK" <<'PY'
import subprocess, sys
from pathlib import Path
import xml.etree.ElementTree as E
expanded, work = map(Path, sys.argv[1:])
root = E.parse(expanded / 'Distribution').getroot()
refs = [r for r in root.findall('pkg-ref') if r.attrib['id'] == 'com.ffapp.desktop' and r.text and r.text.strip().startswith('#')]
assert len(refs) == 1, 'Expected one Velopack installer component'
source = expanded / refs[0].text.strip().removeprefix('#')
subprocess.run(['pkgutil', '--flatten', str(source), str(work / 'FFApp.pkg')], check=True)
PY
pkgbuild --analyze --root "$WORK/launcher-root" "$WORK/components.plist"
python3 - "$WORK/components.plist" <<'PY2'
import plistlib, sys
from pathlib import Path
p = Path(sys.argv[1])
items = plistlib.loads(p.read_bytes())
for item in items:
    item['BundleIsRelocatable'] = False
    # Replace legacy standalone FFRux entries, whose bundle ID differs. User data is external.
    item['BundleHasStrictIdentifier'] = False
    item['BundleIsVersionChecked'] = False
p.write_bytes(plistlib.dumps(items))
PY2
pkgbuild --root "$WORK/launcher-root" --component-plist "$WORK/components.plist" --install-location /Applications \
    --identifier com.ffapp.ffrux.launcher --version "$VERSION" "$WORK/FFRux.pkg"
productbuild --synthesize --package "$WORK/FFApp.pkg" --package "$WORK/FFRux.pkg" "$WORK/Distribution"
python3 - "$WORK/Distribution" <<'PY'
import sys
import xml.etree.ElementTree as E
p = sys.argv[1]
tree = E.parse(p)
root = tree.getroot()
E.SubElement(root, 'domains', enable_anywhere='false', enable_currentUserHome='true', enable_localSystem='true')
E.SubElement(root, 'title').text = 'FFApp and FFRux'
options = root.find('options')
if options is None:
    options = E.SubElement(root, 'options')
options.set('customize', 'never')
tree.write(p, encoding='utf-8', xml_declaration=True)
PY
productbuild --distribution "$WORK/Distribution" --package-path "$WORK" "$WORK/Setup.pkg"
mv "$WORK/Setup.pkg" "$RELEASES/FFApp-$CHANNEL-Setup.pkg"
python3 "$ROOT/scripts/verify-macos-installer.py" "$RELEASES/FFApp-$CHANNEL-Setup.pkg"
