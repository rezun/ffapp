# FFApp

A cross-platform desktop GUI for FFmpeg, built with C# and [Avalonia UI](https://avaloniaui.net/).

FFApp provides an intuitive interface for video and audio encoding without needing to know FFmpeg command-line syntax.

## Features

- **Media probing** — select a source file and inspect its streams/codecs
- **Encoding options** — configure container, video/audio codecs, quality (CRF), target bitrate, or target file size
- **Two-pass encoding** with automatic stats handling
- **Preset management** — save, load, and delete encoding configurations
- **Job queue** — run a single job or batch-process multiple files
- **Real-time progress** tracking
- **Command preview** — see and copy the exact FFmpeg command
- **Dark/light themes**

## Requirements

- [.NET 10](https://dotnet.microsoft.com/download) SDK (for building)
- FFmpeg and FFprobe on your system PATH

## Build & Run

```bash
dotnet run --project src/FFApp/FFApp.csproj
```

### Tests

```bash
dotnet test
```

### Package for Distribution

```bash
./build.sh                     # all platforms
./build.sh --rid osx-arm64     # specific platform
./build.sh --version 1.2.3     # custom version
```

Produces self-contained and framework-dependent builds for Windows, macOS (arm64/x64), and Linux in the `dist/` directory.

## Tech Stack

- **C# / .NET 10** with Avalonia UI 11
- **MVVM** via CommunityToolkit.Mvvm
- **Dependency Injection** via Microsoft.Extensions.DependencyInjection
- **Testing** with xUnit, NSubstitute, and FluentAssertions
