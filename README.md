# FFApp

A cross-platform desktop GUI for FFmpeg, built to make video and audio encoding straightforward without sacrificing control.

## Features

- **Multiple codecs** — H.264, H.265, VP9, AV1 for video; MP3, AAC, Vorbis, Opus for audio
- **Three encoding strategies** — Target quality (CRF), target bitrate (two-pass), or target file size
- **Job queue** — Batch encode with progress tracking, pause/resume, and per-job logs
- **Video preview** — Scrub through video with trim and crop controls (via LibVLC)
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

## Requirements

- [FFmpeg](https://ffmpeg.org/download.html) and ffprobe installed and available in PATH (or configured via the app)
- [LibVLC](https://www.videolan.org/) for video preview (optional)

## Installation

Download a release build for your platform, or build from source (see below).

On first launch, FFApp will check for ffmpeg/ffprobe and prompt you to set their paths if they aren't found automatically.

### File associations

FFApp declares itself as an optional handler for common video and audio containers. Opening a file with FFApp adds it to the source list — launching a fresh window if the app isn't already running.

- **macOS:** Associations ship in the `.app` bundle's `Info.plist`. FFApp registers as an `Alternate` handler so it won't displace QuickTime or VLC as the system default; right-click any media file and choose **Open With → FFApp**, or set FFApp as the default via **Get Info → Open with** for a specific extension.
- **Windows:** File associations require the installer to write HKCR registry keys and are not registered by the standalone `.exe`. This will be wired up when the packaged installer lands.
- **Linux:** `packaging/linux/ffapp.desktop` has no `MimeType=` entries yet. A full implementation depends on a proper package (`.deb`/`.rpm`/Flatpak) that can run `update-desktop-database`.

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

```
src/FFApp/
  Views/              # Avalonia XAML views
  ViewModels/         # MVVM ViewModels (ObservableObject, RelayCommand)
  Core/
    Models/           # Data models, enums, records
    Services/         # Service interfaces
    CommandBuilder.cs # FFmpeg argument generation
    Validation/       # Codec/container compatibility rules
  Infrastructure/     # Service implementations (process runner, queue, probing)
  Converters/         # XAML value converters
  Controls/           # Custom Avalonia controls
tests/FFApp.Tests/    # Unit tests
```

### Building

```bash
# Run locally
dotnet run --project src/FFApp/FFApp.csproj

# Run tests
dotnet test

# Publish for a specific platform (self-contained)
./build.sh --rid osx-arm64

# Publish and deploy to /Applications (macOS)
./build.sh --deploy-mac
```

The build script supports `win-x64`, `osx-arm64`, `osx-x64`, and `linux-x64`. Each target produces a platform-appropriate artifact (.app bundle, .exe, or .tar.gz).

### Architecture Notes

- **ViewModels communicate via `WeakReferenceMessenger`** for loose coupling (e.g., source selection, dependency resolution)
- **`JobContext`** captures an immutable snapshot of all encoding settings when a job is created
- **`CommandBuilder`** generates FFmpeg arguments from an `OutputSettings` record — handles trim, crop, filters, multi-pass, and HDR metadata
- **Two-pass encoding** writes a null first pass, then a real second pass, with progress tracked per-pass
- **Compiled XAML bindings** (`x:CompileBindings="True"`) are used throughout for type safety
