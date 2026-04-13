# FFApp

A cross-platform desktop GUI for FFmpeg, built to make video and audio encoding straightforward without sacrificing control.

## Features

- **Multiple codecs** — H.264, H.265, VP9, AV1 for video; MP3, AAC, Vorbis, Opus for audio
- **Three encoding strategies** — Target quality (CRF), target bitrate (two-pass), or target file size
- **Job queue** — Batch encode with progress tracking, pause/resume, and per-job logs
- **Video preview** — Scrub through video with trim and crop controls (via LibVLC)
- **HDR support** — Detect HDR10/HLG content, preserve metadata, or tone-map to SDR
- **Presets** — Save and load encoding configurations
- **Validation** — Warns about incompatible codec/container combinations before encoding
- **Stream handling** — Selectively remove subtitle or data streams
- **Resolution & frame rate** — Presets for common targets (720p–4K, 23.976–60fps)
- **Drag and drop** — Add files or folders directly into the source list
- **Cross-platform** — Windows, macOS (Intel & Apple Silicon), and Linux

## Requirements

- [FFmpeg](https://ffmpeg.org/download.html) and ffprobe installed and available in PATH (or configured via the app)
- [LibVLC](https://www.videolan.org/) for video preview (optional)

## Installation

Download a release build for your platform, or build from source (see below).

On first launch, FFApp will check for ffmpeg/ffprobe and prompt you to set their paths if they aren't found automatically.

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
