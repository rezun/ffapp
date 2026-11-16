#!/usr/bin/env python3
"""Fail packaging when either executable, its runtime config or its version is missing."""
import json
import plistlib
import sys
from pathlib import Path


def verify(root: Path, rid: str, version: str) -> None:
    package_version = version.split("+", 1)[0]
    for app in ("FFApp", "FFRux"):
        if rid.startswith("osx-"):
            bundle = root / "FFApp.app"
            if app == "FFRux":
                bundle = bundle / "Contents/Helpers/FFRux.app"
            with (bundle / "Contents/Info.plist").open("rb") as stream:
                plist = plistlib.load(stream)
            assert plist["CFBundleExecutable"] == app, bundle
            assert plist["CFBundleShortVersionString"] == version, bundle
            directory = bundle / "Contents/MacOS"
        else:
            directory = root if app == "FFApp" else root / "companion/FFRux"
        executable = directory / (app + (".exe" if rid.startswith("win-") else ""))
        assert executable.is_file() and executable.stat().st_size > 0, executable
        assert (directory / f"{app}.runtimeconfig.json").is_file(), directory
        deps = json.loads((directory / f"{app}.deps.json").read_text())
        assert f"{app}/{package_version}" in deps["libraries"], directory
    print(f"Verified FFApp and bundled FFRux {version} for {rid}.")


if __name__ == "__main__":
    verify(Path(sys.argv[1]), sys.argv[2], sys.argv[3])
