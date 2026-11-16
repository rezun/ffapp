# FFApp

A cross-platform desktop GUI for FFmpeg, built to make video and audio encoding straightforward without sacrificing control.

## Features

- **Multiple codecs** — H.264, H.265, VP9, AV1 for video; MP3, AAC, Vorbis, Opus for audio
- **Three encoding strategies** — Target quality (CRF), target bitrate (two-pass), or target file size
- **Job queue** — Batch encode with progress tracking, pause/resume, and per-job logs
- **Video preview** — Scrub through video with trim and crop controls (via LibVLC)
- **Encoding comparisons** — A separate window with editable, evenly distributed samples, independent settings variants, synchronized A/B playback, and a size/time results table. Comparisons and completed clips are saved for reuse.
- **Color information** — Preserve source color labels, explicitly fill missing values as BT.709 / Limited, or correct labels manually; source and effective output values are shown
- **HDR support** — Detect HDR10/HLG content, preserve metadata, or tone-map to SDR
- **Presets** — Save and load encoding configurations
- **Validation** — Warns about incompatible codec/container combinations before encoding
- **Stream handling** — Selectively remove subtitle or data streams
- **Resolution & frame rate** — Presets for common targets (720p–4K, 23.976–60fps)
- **Drag and drop** — Add files or folders directly into the source list
- **Cross-platform** — Windows, macOS (Intel & Apple Silicon), and Linux

### Color information

Re-encodes explicitly preserve known source primaries, transfer function, YUV matrix and signal range by default. Missing or unspecified values remain unspecified; resolution and bit depth are not used to guess a color space.

For a known untagged HD SDR Blu-ray, choose **Use source; assume BT.709 / Limited for missing values**. This fills only missing fields and shows the assumption in the output summary. **Manual color labels** corrects the four labels independently; it does not perform a color-space conversion. Copy mode leaves the stream unchanged.

HDR settings take precedence: HDR preservation keeps the source color information, and HDR-to-SDR processing outputs BT.709 / Limited. SDR overrides are disabled for sources with HDR indicators, including incomplete HDR metadata. Color settings are included in presets and restored sessions; older presets default to source preservation.

### Compare encodes

Select a video in re-encode mode and click **Compare encodes…** to open a non-modal comparison window. The window captures the current source, trim range, crop and output settings. Reopening that source restores its saved comparison; subsequent changes in the main window do not alter it.

The window guides you through **1. Review samples**, **2. Choose variants**, and **3. Compare results**. Selecting a sample automatically loads its original first frame, paused, without encoding. Press **Play sample** to watch it; adjust its start/duration or add manual samples. The timeline selects the nearest sample. **Next: choose variants** opens the independent settings editor. Duplicate variants and edit them with the usual settings controls; AV1 also offers **Add grain 0 / 8 / 10**, with source denoising enabled and the selected variant's CRF/preset retained.

**Create test clips (N)** shows how many short clips need encoding across all samples and variants; matching completed clips are reused. The results table appears only when results are available. Click a result to compare that sample against the original, or choose two encoded variants. **Apply … to main window** explicitly applies the selected variant when the matching source is selected there. Shared seeking, looping, stepping and centered zoom use persistent FFmpeg decoders, with a shared presentation clock and paired frames. Comparison playback is muted and does not require VLC. Sizes exclude audio/subtitles/attachments and include up to two seconds of pre-roll. Size targets retain the full trimmed movie's calculated video bitrate. These samples compare settings; they do not provide an exact prediction of final movie size. For HDR-to-SDR judgments, compare SDR variants: the comparison display currently converts to 8-bit RGB without HDR tone mapping.

Sessions and clips are stored in `comparisons/` under the app's data directory. Closing a comparison cancels active encoding and saves completed work. Changed sample ranges, encoding commands, source file fingerprints or FFmpeg binaries invalidate the relevant cached clips. Automatic grain/scene analysis is not part of this workflow.

## Requirements

- [FFmpeg and ffprobe](https://ffmpeg.org/download.html) **9 or newer**, installed in PATH or configured in **Settings → Tools and components…** in either app. FFmpeg 9.x is the release baseline. Older releases and development snapshots without a recognizable release version are rejected with an explanation.
- FFApp checks the encoders and filters actually included in the selected build. Video encoding uses `libx264`, `libx265`, `libvpx-vp9` or `libsvtav1`; audio encoding uses `aac`, `libmp3lame`, `libvorbis` or `libopus`. Missing features block only settings that need them; copying streams remains available. Standard HDR-to-SDR conversion needs `zscale` (zimg), `tonemap` and `format`; the optional alternative needs `libplacebo`. Advanced AV1 parameters also depend on the linked SVT-AV1 version; custom encoder options are passed through to that encoder.
- FFmpeg **9 shared libraries** (`avcodec` 63, `avformat` 63, `avutil` 61, `swscale` 10) enable FFApp comparison playback. An optional matching `avfilter` 12 with the tone-mapping filters enables HDR conversion in the comparison. These files are separate from a standalone static `ffmpeg` executable. Place the complete shared build beside the configured executable, in its sibling `lib` directory, in an `ffmpeg` directory beside FFApp, or in a standard system library directory. Their dependencies must also be installed. Shared libraries must match the app architecture and these library major versions, even when a newer executable is used.
- [VLC / LibVLC](https://www.videolan.org/vlc/) enables the main-window video preview (optional). Match the app architecture.
- [mkvpropedit from MKVToolNix](https://mkvtoolnix.download/downloads.html) enables Matroska track-statistics updates (optional in both apps).

Comparison playback, VLC preview and Matroska statistics are optional. Missing or incompatible components appear in **Tools and components**; FFApp also displays a notice for unavailable optional features at startup. They do not prevent other encoding work.

## Installation

Download installers and portable builds for FFApp and FFRux from [GitHub Releases](https://github.com/rezun/ffapp/releases), or build from source (see below).

GitHub releases provide self-contained Velopack packages for Windows, macOS and Linux, each for x64 and ARM64. Both apps use the release version and have separate package IDs and update channels. Windows builds provide Setup installers, macOS builds provide installation packages and app bundles, and Linux builds provide AppImages. The packages are currently unsigned and macOS packages are not notarized. Velopack startup hooks are integrated; in-app update checking and downloading are not yet implemented.

External media tools are installed separately; release builds do not download or bundle FFmpeg, VLC or MKVToolNix. GitHub releases and local **Full** builds include the .NET runtime. Local **Slim** builds require the [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0) for the matching architecture.

On every launch, FFApp checks the configured tools and comparison libraries. If a required executable is missing, too old or cannot run, setup shows the individual error and preserves any tools already discovered. Select executables with **Browse…**, enter paths or command names, or leave a field empty for automatic discovery. **Check again** refreshes diagnostics; **Validate and save** saves working paths. You can reopen this screen through **Settings → Tools and components…** without losing sources or queued jobs. FFRux has the same settings menu with **Check and save**; installation or path changes can be retried without restarting it.

- **macOS:** A package-manager installation that supplies FFmpeg 9 shared libraries is suitable. Homebrew executables and their sibling library directory are detected automatically, even when launching from Finder. VLC is detected in `/Applications/VLC.app`.
- **Windows:** Choose a **shared** FFmpeg 9 build matching the app's x64 or ARM64 architecture for comparison playback. Keep its DLLs and their dependencies together with the executable; a static build alone only covers command-line encoding. Select both `ffmpeg.exe` and `ffprobe.exe` if their directory is not in PATH.
- **Linux:** Install packages that provide FFmpeg/ffprobe 9 or newer, and version-9 shared libraries for comparison playback. Distribution packages may be older or omit optional encoders/filters; use the component diagnostics to check. LibVLC is detected in standard system library locations.

Restart FFApp after installing VLC or replacing shared libraries: native libraries already loaded by the current process cannot be switched safely. FFmpeg/ffprobe executable paths can be changed without a restart. Both apps configure and check tools that are already installed; the tool settings do not offer downloads or installation actions.

### File associations

FFApp declares itself as an optional handler for common video and audio containers. Opening a file with FFApp adds it to the source list — launching a fresh window if the app isn't already running.

- **macOS:** Associations ship in the `.app` bundle's `Info.plist`. FFApp registers as an `Alternate` handler so it won't displace QuickTime or VLC as the system default; right-click any media file and choose **Open With → FFApp**, or set FFApp as the default via **Get Info → Open with** for a specific extension.
- **Windows:** File associations require the installer to write HKCR registry keys and are not registered by the standalone `.exe`. The Velopack installer does not register these associations yet.
- **Linux:** `packaging/ffapp/linux/ffapp.desktop` has no `MimeType=` entries yet. A full implementation depends on a proper package (`.deb`/`.rpm`/Flatpak) that can run `update-desktop-database`.

### Logs

FFApp and FFRux each keep two kinds of logs in their own log folder:

| Platform | Folder |
|---|---|
| macOS | `~/Library/Logs/FFApp`, `~/Library/Logs/FFRux` (listed in Console.app) |
| Windows | `%LOCALAPPDATA%\FFApp\Logs`, `%LOCALAPPDATA%\FFRux\Logs` |
| Linux | `$XDG_STATE_HOME/ffapp/logs`, `$XDG_STATE_HOME/ffrux/logs` (default `~/.local/state`) |

- **`app-YYYYMMDD.log`**: app events and errors, one file per day, kept for 14 days.
- **`jobs/`**: one file per encode or remux, named after its start time and output file. It contains the commands and the complete FFmpeg/mkvpropedit output, followed by the result. These logs are kept for 30 days. Use **Open log file** on a job (Log tab in FFApp, result bar in FFRux) to open one, or **Info → Open logs folder** to browse them all.

---

## Developer Guide

### Tech Stack

| | |
|---|---|
| Framework | [Avalonia UI](https://avaloniaui.net/) 11.x |
| Runtime | .NET 10 |
| Pattern | MVVM with [CommunityToolkit.Mvvm](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/) |
| DI | Microsoft.Extensions.DependencyInjection |
| Video preview | LibVLCSharp |
| Tests | xUnit, NSubstitute, FluentAssertions |

### Project Structure

The repository builds two desktop apps on two shared libraries:

```
src/
  FFApp.Media/          # UI-free: media models, ffprobe, process runner, tool discovery
  FFApp.Desktop/        # Avalonia: app identity, single instance, launch files, theme tokens, styles, controls
  FFApp/                # The encoder app
    Views/              # Avalonia XAML views
    ViewModels/         # MVVM ViewModels (ObservableObject, RelayCommand)
    Core/
      Models/           # Encoding models, enums, records
      Services/         # Service interfaces
      CommandBuilder.cs # FFmpeg argument generation
      Validation/       # Codec/container compatibility rules
    Infrastructure/     # Service implementations (queue, settings, comparison)
    Converters/         # XAML value converters
    Controls/           # FFApp-only Avalonia controls
  FFRux/                # The remux app (see docs/ffrux-requirements.md)
    Core/               # Remux plan, copy matrix (CodecRules), compatibility check, command builder
    Infrastructure/     # Job runner, settings
    ViewModels/  Views/ # Three-column UI: sources, track table, track properties
tests/
  FFApp.Shared.Tests/   # Tests for FFApp.Media and FFApp.Desktop
  FFApp.Tests/
  FFRux.Tests/          # Unit tests, plus integration tests that run the copy matrix against FFmpeg
packaging/
  ffapp/  ffrux/        # Per-app Info.plist, icons and .desktop files
```

Shared code keeps its original namespaces (`FFApp.Core.Models`, `FFApp.Infrastructure`, …), so moving a type into a shared library does not change any `using`. Each app has its own `AppIdentity`, which gives it a separate data folder and single-instance pipe: FFApp and FFRux can run side by side and never receive each other's "Open with" files. Package versions are pinned once in `Directory.Packages.props`.

### Building

```bash
# Run locally
dotnet run --project src/FFApp/FFApp.csproj
dotnet run --project src/FFRux/FFRux.csproj

# Run tests
dotnet test

# Only the fast tests, without the FFRux integration tests that need FFmpeg
dotnet test --filter "Category!=Integration"

# Publish for a specific platform (self-contained)
./build.sh --rid osx-arm64

# Publish and deploy to /Applications (macOS)
./build.sh --deploy-mac

# The same for FFRux
./build.sh --app ffrux --deploy-mac
```

`--app` selects `ffapp` (default) or `ffrux`; each app's version comes from its own `.csproj`. The build script supports `win-x64`, `osx-arm64`, `osx-x64`, and `linux-x64`. Each target produces a platform-appropriate artifact (.app bundle, .exe, or .tar.gz).

### GitHub builds and releases

[CI](https://github.com/rezun/ffapp/actions/workflows/ci.yml) builds the solution in Release and runs the unit tests on pushes to `main` and pull requests. FFmpeg-dependent integration tests can be run locally; CI excludes `Category=Integration`.

The [Release workflow](https://github.com/rezun/ffapp/actions/workflows/release.yml) runs CI before packaging both apps for all six platform/architecture combinations. The tag version overrides both project versions for that release. For example:

```bash
git tag v1.13.2
git push origin v1.13.2
```

A `v*` tag publishes the release after every package has built and uploaded successfully. Prerelease versions such as `v1.13.2-beta.1` are marked as prereleases. To test packaging first, run the workflow manually with a SemVer version (without `v`) and leave `publish_release` disabled; the assets stay in a draft release. Enable it to publish after upload.

Velopack's SDK and CLI use the same pinned version from `Directory.Packages.props`. Each app/architecture has its own channel (for example `ffapp-osx-arm64` and `ffrux-osx-arm64`). Previous packages are downloaded when available to generate delta updates; the first release needs no previous packages. The workflow uses the built-in `GITHUB_TOKEN`, with write access limited to the publishing job; no publishing secret is required. Signing and notarization credentials are not configured.

### Architecture Notes

- **ViewModels communicate via `WeakReferenceMessenger`** for loose coupling (e.g., source selection, dependency resolution)
- **`JobContext`** captures an immutable snapshot of all encoding settings when a job is created
- **`CommandBuilder`** generates FFmpeg arguments from an `OutputSettings` record — handles trim, crop, filters, multi-pass, and HDR metadata
- **Two-pass encoding** writes a null first pass, then a real second pass, with progress tracked per-pass
- **Compiled XAML bindings** (`x:CompileBindings="True"`) are used throughout for type safety
