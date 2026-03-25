# Release Workflow

This project uses `pyside6-deploy` for desktop packaging.

## Versioning

The canonical app version lives in [pyproject.toml](pyproject.toml):

```toml
[project]
version = "1.0.0"
```

Update that value before building a release. The build scripts refresh the local editable package metadata from `pyproject.toml` before packaging so the bundled app version stays in sync.

## Packaging Files

- [main.py](main.py): packaging entry point that adds `src/` to `sys.path`
- [pysidedeploy.spec](pysidedeploy.spec): checked-in deployment config
- [logo.png](logo.png): master square source image for app icons
- [scripts/generate-icons.sh](scripts/generate-icons.sh): generates platform icon assets from `logo.png`
- [scripts/build-macos.sh](scripts/build-macos.sh): macOS build command
- [scripts/build-linux.sh](scripts/build-linux.sh): Linux build command
- [scripts/build-windows.ps1](scripts/build-windows.ps1): Windows build command

Generated icon assets live under `assets/icons/`:

- `assets/icons/macos/app-icon.icns`
- `assets/icons/windows/app-icon.ico`
- `assets/icons/linux/app-icon.png`

Regenerate them after changing the source logo:

```bash
scripts/generate-icons.sh
```

## macOS Build

Recommended environment:

```bash
python3.13 -m venv .venv
. .venv/bin/activate
python -m pip install -U pip
python -m pip install -e ".[dev]"
```

`pyside6-deploy` currently depends on a Nuitka version that does not support Python 3.14 for this project, so use Python 3.13 for packaging. The build scripts disable `ccache` by default to keep packaging non-interactive and avoid extra local tool setup.

Dry run the deployment command:

```bash
scripts/build-macos.sh --dry-run
```

Build the `.app` bundle and wrap it in a `.dmg`:

```bash
scripts/build-macos.sh
```

Build only the `.app` bundle:

```bash
scripts/build-macos.sh --skip-dmg
```

Artifacts are written to `dist/`:

- `dist/FFApp.app`
- `dist/ffapp-<version>-macos.dmg`

## Linux Build

Recommended environment:

```bash
python3.13 -m venv .venv
. .venv/bin/activate
python -m pip install -U pip
python -m pip install -e ".[dev]"
```

Dry run the deployment command:

```bash
scripts/build-linux.sh --dry-run
```

Build the standalone folder and archive it:

```bash
scripts/build-linux.sh
```

Build only the standalone folder:

```bash
scripts/build-linux.sh --skip-archive
```

Artifacts are written to `dist/`:

- `dist/FFApp.dist`
- `dist/ffapp-<version>-linux.tar.gz`

## Windows Build

Recommended environment:

```powershell
py -3.13 -m venv .venv
.venv\Scripts\Activate.ps1
python -m pip install -U pip
python -m pip install -e ".[dev]"
```

Dry run the deployment command:

```powershell
.\scripts\build-windows.ps1 -DryRun
```

Build the standalone folder and zip it:

```powershell
.\scripts\build-windows.ps1
```

Build only the standalone folder:

```powershell
.\scripts\build-windows.ps1 -SkipArchive
```

Artifacts are written to `dist\`:

- `dist\FFApp.dist`
- `dist\ffapp-<version>-windows.zip`

## Runtime Dependencies

The packaged app bundles:

- Python runtime
- PySide6 / Qt runtime
- application code

The packaged app does not bundle:

- `ffmpeg`
- `ffprobe`

Those remain system requirements and are detected at runtime by the app.

## Notes

- `pyside6-deploy` updates [pysidedeploy.spec](pysidedeploy.spec) with the Python interpreter path and discovered Qt modules/plugins used during the build.
- The Linux and Windows scripts were added from the same deployment model as macOS, but they were not executed in this macOS workspace.
- The Linux script currently ships a `.tar.gz` archive rather than an `AppImage`.
- The Windows script currently ships a `.zip` archive rather than an installer `.exe`.
