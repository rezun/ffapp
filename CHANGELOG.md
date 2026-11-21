# Changelog

User-visible changes to FFApp and FFRux. Both apps share one version.

Add entries under **Unreleased** as part of each change. `scripts/release.py <version>` turns that section into the new release, and its bullets become the GitHub release notes. Use the headings `Added`, `Changed`, `Fixed` and `Removed`; leave out empty ones.

## [Unreleased]

### Added

- FFApp debug builds include a Debug menu that simulates an update offline.

### Changed

- FFApp uses the status bar for update checks, download progress, and the restart action while the transcode queue is idle.
- "Optimize for streaming" in FFApp and FFRux reserves room for the index in MKV and WebM outputs, so the file no longer has to be rewritten after muxing. MP4 keeps faststart.

### Fixed

- Starting the transcode queue cancels an in-progress update check or download, so a slow or offline connection never delays transcoding.

## [1.14.3] - 2026-11-19

### Added

- FFApp and FFRux: **Optimize for streaming** optionally moves MP4 playback metadata or the MKV/WebM seek index to the start of the file. FFApp presets and FFRux output settings remember the option.

### Changed

- **Optimize for streaming** defaults to off for every format. Saved choices still apply.

### Fixed

- Linux: recognize system-installed LibVLC for the video preview.

## [1.14.2] - 2026-11-17

### Fixed

- Preview no longer plays without sound after loading a file.

## [1.14.1] - 2026-11-17

### Fixed

- Windows: bundled FFRux gets its own taskbar identity instead of grouping with FFApp.
- Completed queue jobs no longer show a progress bar.

## [1.14.0] - 2026-11-16

### Added

- Optional audio filter for re-encoded audio.
- Estimated time until the whole encode queue finishes.

## [1.13.9] - 2026-11-16

### Changed

- Preview shows the first frame without pressing play.

### Fixed

- macOS: bundled FFRux helper no longer appears in Open With.
- Expanded preview stays tall enough for its controls.

## [1.13.8] - 2026-11-16

### Changed

- Reworked tools dialog; Scoop shims on Windows are resolved to the real executables.

### Removed

- Portable bundles. Only the five newest GitHub releases are kept.

## [1.13.7] - 2026-11-16

### Changed

- Updates download in the background and install on restart.

### Removed

- Permanent optional-features notice bar.

## [1.13.6] - 2026-11-16

### Fixed

- Windows: bundled FFRux is discoverable in the Start menu.

## [1.13.5] - 2026-11-16

### Changed

- Compact dialogs with standard Enter and Escape behavior.

## [1.13.4] - 2026-11-16

### Added

- Update check at startup with confirmed Velopack updates.

## [1.13.3] - 2026-11-16

### Added

- macOS: native FFRux launcher, discoverable in Spotlight, Finder and the Dock.

## [1.13.2] - 2026-11-16

### Changed

- FFRux is bundled with FFApp in one installation.

## [1.13.1] - 2026-11-16

First release on GitHub.

### Added

- Velopack installers for Windows, macOS and Linux (x64 and ARM64).
- FFRux remux companion app.
- Tools and components setup that validates FFmpeg 9 and optional components.
- Per-source subtitle track selection.
- Per-job FFmpeg logs, an app log and Info → Open logs folder.
- Black-bar crop suggestion from samples across the whole source.

[Unreleased]: https://github.com/rezun/ffapp/compare/v1.14.3...HEAD
[1.14.3]: https://github.com/rezun/ffapp/compare/v1.14.2...v1.14.3
[1.14.2]: https://github.com/rezun/ffapp/compare/v1.14.1...v1.14.2
[1.14.1]: https://github.com/rezun/ffapp/compare/v1.14.0...v1.14.1
[1.14.0]: https://github.com/rezun/ffapp/compare/v1.13.9...v1.14.0
[1.13.9]: https://github.com/rezun/ffapp/compare/v1.13.8...v1.13.9
[1.13.8]: https://github.com/rezun/ffapp/compare/v1.13.7...v1.13.8
[1.13.7]: https://github.com/rezun/ffapp/compare/v1.13.6...v1.13.7
[1.13.6]: https://github.com/rezun/ffapp/compare/v1.13.5...v1.13.6
[1.13.5]: https://github.com/rezun/ffapp/compare/v1.13.4...v1.13.5
[1.13.4]: https://github.com/rezun/ffapp/compare/v1.13.3...v1.13.4
[1.13.3]: https://github.com/rezun/ffapp/compare/v1.13.2...v1.13.3
[1.13.2]: https://github.com/rezun/ffapp/compare/v1.13.1...v1.13.2
[1.13.1]: https://github.com/rezun/ffapp/releases/tag/v1.13.1
