# FFApp: Python/Qt to C#/Avalonia Conversion Plan

## Context

FFApp is a ~7,200-line Python/PySide6 desktop application that provides a guided GUI for building and executing ffmpeg commands. The developer is primarily a C# developer and finds the Python/Qt codebase difficult to maintain. This plan converts the entire application to C# 14 / .NET 10 / Avalonia 11.2 with proper MVVM architecture, async/await, and dependency injection -- making it feel like a native C# application rather than a Python port.

The current Python app has clean separation (core/infrastructure/app layers) but the UI layer has a 1,227-line MainWindow god-object. The C# version will decompose this into focused ViewModels.

---

## Solution & Project Layout

Single solution, single application project (the app is ~7K lines -- multiple assemblies would be overengineering). Separate test project.

```
dotnet-app/
  FFApp.sln
  src/
    FFApp/
      FFApp.csproj
      Program.cs                          # Avalonia entry point
      App.axaml / App.axaml.cs            # Application + DI setup
      Core/
        Models/
          Enums.cs                        # OutputContainer, VideoCodec, AudioCodec, etc.
          StreamInfo.cs                   # VideoStreamInfo, AudioStreamInfo, etc.
          MediaInfo.cs                    # SourceMediaInfo record
          EncodingOptions.cs              # VideoEncoderOptions hierarchy, QualityOptions hierarchy
          OutputSettings.cs               # OutputSettings, AudioOptions, StreamHandlingOptions, etc.
          JobModels.cs                    # JobDefinition, GeneratedCommand, progress, results
          QueueModels.cs                  # QueuedJob, QueueItem
          ValidationModels.cs            # ValidationIssue, ValidationResult
          AppSettings.cs                  # AppSettings, ExecutablePaths, WindowGeometry, etc.
          PresetModels.cs                 # PresetDefinition
        Services/
          IMediaProbeService.cs
          ISettingsService.cs
          IPresetService.cs
          IValidationService.cs
          ICommandBuilder.cs
          IProcessRunner.cs
          IJobExecutionService.cs
          IQueueService.cs
          IDependencyService.cs
        CommandBuilder.cs                 # ffmpeg command generation (from core/commands.py)
        Validation/
          CompatibilityData.cs            # Static codec/container matrices
          ValidationService.cs            # Validates settings against source media
      Infrastructure/
        AppPaths.cs                       # Platform-specific app data directories
        DependencyManager.cs              # Discovers/validates ffmpeg and ffprobe
        MediaProbeService.cs              # Runs ffprobe, parses JSON output
        ProcessRunner.cs                  # Runs ffmpeg with progress parsing
        JobExecutionService.cs            # Orchestrates single/multi-pass execution
        QueueService.cs                   # In-memory job queue with sequential execution
        SettingsService.cs                # JSON settings persistence
        PresetService.cs                  # JSON preset library persistence
        Serialization/
          SettingsSerializer.cs           # Custom JSON converters for settings
          PresetSerializer.cs             # Custom JSON converters for presets
      ViewModels/
        MainWindowViewModel.cs            # Top-level navigation (setup vs workspace)
        DependencySetupViewModel.cs       # Configure ffmpeg/ffprobe paths
        WorkspaceViewModel.cs             # Coordinator for the main editing workspace
        SourcePanelViewModel.cs           # Source file loading and media info display
        OutputSettingsViewModel.cs        # Master settings form coordinator
        FormatViewModel.cs                # Container + video codec selection with filtering
        AudioViewModel.cs                 # Audio mode, codec, bitrate, stereo remix
        EncodingStrategyViewModel.cs      # Strategy selector + sub-VM switching
        FileSizeViewModel.cs              # Target file size controls
        BitrateViewModel.cs               # Target bitrate controls
        QualityViewModel.cs               # CRF/quality controls (switches H26x vs VPx)
        StreamHandlingViewModel.cs        # Subtitle/data/audio removal toggles
        PresetViewModel.cs                # Preset save/load/rename/delete
        CommandPreviewViewModel.cs        # Generated ffmpeg command display
        ExecutionViewModel.cs             # Job execution with progress
        QueuePanelViewModel.cs            # Queue management (add/remove/start/cancel)
        QueueItemViewModel.cs             # Per-item state wrapper for ObservableCollection
        QueueItemDetailsViewModel.cs      # Selected queue item details
      Views/
        MainWindow.axaml / .axaml.cs
        DependencySetupView.axaml / .axaml.cs
        WorkspaceView.axaml / .axaml.cs
        SourcePanelView.axaml / .axaml.cs
        OutputSettingsView.axaml / .axaml.cs
        FormatView.axaml / .axaml.cs
        AudioView.axaml / .axaml.cs
        FileSizeView.axaml / .axaml.cs
        BitrateView.axaml / .axaml.cs
        QualityView.axaml / .axaml.cs
        StreamHandlingView.axaml / .axaml.cs
        PresetBarView.axaml / .axaml.cs
        CommandPreviewView.axaml / .axaml.cs
        ExecutionView.axaml / .axaml.cs
        QueuePanelView.axaml / .axaml.cs
        QueueItemDetailsView.axaml / .axaml.cs
      Controls/
        NoWheelComboBox.cs                # ComboBox that ignores wheel when unfocused
        NoWheelNumericUpDown.cs           # NumericUpDown that ignores wheel when unfocused
        SliderField.axaml / .axaml.cs     # Slider + value label + recommended range indicator
      Converters/
        EnumToBoolConverter.cs            # For RadioButton groups bound to enums
        JobStateToColorConverter.cs       # Queue item state coloring
        BitrateFormatConverter.cs         # Format integers as "X kbps"
      Services/
        IDialogService.cs                 # Abstraction for file/folder/message dialogs
        DialogService.cs                  # Avalonia implementation using StorageProvider
  tests/
    FFApp.Tests/
      FFApp.Tests.csproj
      Core/
        CommandBuilderTests.cs
        ValidationServiceTests.cs
      Infrastructure/
        DependencyManagerTests.cs
        ProbeServiceTests.cs
        SettingsServiceTests.cs
        PresetServiceTests.cs
        QueueServiceTests.cs
```

---

## NuGet Packages

**FFApp.csproj:**
- `Avalonia` 11.2.x
- `Avalonia.Desktop`
- `Avalonia.Themes.Fluent`
- `Avalonia.Diagnostics` (Debug only)
- `CommunityToolkit.Mvvm` 8.4.x (source generators for ObservableObject, RelayCommand, ObservableProperty, Messenger)
- `Microsoft.Extensions.DependencyInjection` 10.x

**FFApp.Tests.csproj:**
- `xunit` 2.9.x
- `xunit.runner.visualstudio`
- `Microsoft.NET.Test.Sdk`
- `NSubstitute` 5.x

No explicit `System.Text.Json` needed -- ships with .NET 10.

---

## Models Layer (from `core/models.py`)

### Enums

Python `StrEnum` values become C# `enum` with `[JsonStringEnumMemberName]` for serialization where the wire name differs from the C# member name.

```csharp
public enum OutputContainer { Mp4, Mkv, Webm, Ogg }
public enum VideoCodec { H264, H265, Vp8, Vp9 }
public enum AudioCodec { Mp3, Aac, Vorbis, Opus }
public enum EncodingStrategy { TargetFileSize, TargetBitrate, TargetQuality }
public enum AudioMode { Copy, Reencode, Remove }
public enum ResolutionPreset { Original, P720, P1080, P1440, P4K }
public enum JobState { Pending, Running, Completed, Failed, Canceled }
public enum ValidationSeverity { Error, Warning }
```

### Records

All Python frozen dataclasses become C# `record` types (value equality, `with` expressions, immutable).

```csharp
public sealed record Resolution(int Width, int Height);
public sealed record AudioOptions(AudioMode Mode = AudioMode.Copy, AudioCodec? Codec = null, int? Bitrate = null, bool RemixToStereo = false);
public sealed record TargetFileSizeOptions(int TargetSizeMb, bool TwoPass = false, double MuxingOverheadRatio = 0.015);
// etc.
```

### Union Types (discriminated unions)

Python's `VideoEncoderOptions = H264EncoderOptions | H265EncoderOptions | VpxEncoderOptions` and `QualityOptions` union types become abstract record hierarchies:

```csharp
public abstract record VideoEncoderOptions(VideoCodec Codec);
public sealed record H264EncoderOptions(string Preset = "medium", string Profile = "high") : VideoEncoderOptions(VideoCodec.H264);
public sealed record H265EncoderOptions(string Preset = "medium", string Profile = "main") : VideoEncoderOptions(VideoCodec.H265);
public sealed record VpxEncoderOptions(VideoCodec Codec, int CpuUsed = 2) : VideoEncoderOptions(Codec);

public abstract record QualityOptions(VideoCodec Codec);
public sealed record H264QualityOptions(int Crf = 23, string Preset = "medium", string Profile = "high") : QualityOptions(VideoCodec.H264);
public sealed record H265QualityOptions(int Crf = 28, string Preset = "medium", string Profile = "main") : QualityOptions(VideoCodec.H265);
public sealed record Vp8QualityOptions(int Quality = 31, bool TwoPass = false) : QualityOptions(VideoCodec.Vp8);
public sealed record Vp9QualityOptions(int Quality = 32, bool TwoPass = false) : QualityOptions(VideoCodec.Vp9);
```

Consuming code uses C# `switch` expressions with pattern matching -- directly replacing Python's `isinstance()` checks.

### Collection Types

Python `tuple[T, ...]` fields become `ImmutableArray<T>` for value equality and immutability.

---

## Service Interfaces (from `core/services.py`)

Each Python `Protocol` becomes a C# `interface`. Key change: all I/O methods become `async Task<T>` with `CancellationToken`. Pure computation methods (validation, command building) stay synchronous.

```csharp
public interface IMediaProbeService
{
    Task<SourceMediaInfo> ProbeAsync(string sourcePath, CancellationToken ct = default);
}

public interface IProcessRunner
{
    Task<ProcessExecutionResult> RunAsync(GeneratedCommand command,
        float? expectedDurationSeconds = null, int? expectedFrameCount = null,
        Action<ProcessProgress>? progressCallback = null, Action<string>? logCallback = null,
        CancellationToken ct = default);
}

public interface IJobExecutionService
{
    Task<JobResult> ExecuteAsync(JobDefinition job, SourceMediaInfo mediaInfo,
        Action<JobProgress>? progressCallback = null, Action<string>? logCallback = null,
        CancellationToken ct = default);
}

public interface IQueueService
{
    IReadOnlyList<QueueItem> Enqueue(IEnumerable<QueuedJob> jobs);
    IReadOnlyList<QueueItem> Items { get; }
    bool HasPending { get; }
    IReadOnlyList<QueueItem> Remove(IEnumerable<string> itemIds);
    Task<IReadOnlyList<QueueItem>> ExecuteAllAsync(
        Action<QueueItem>? itemStarted = null, Action<QueueItem>? itemProgress = null,
        Action<QueueItem>? itemFinished = null, Action<QueueItem, string>? logCallback = null,
        CancellationToken ct = default);
}
// ISettingsService, IPresetService, IValidationService, ICommandBuilder, IDependencyService similar
```

**CancellationToken replaces Python's manual `cancel()` methods.** The `IProcessRunner` and `IJobExecutionService` no longer need separate `cancel()` -- cancellation flows through the token. Queue cancellation uses `CancellationTokenSource.Cancel()`.

---

## Service Implementations

### Key design decisions per service:

| Service | Python Source | C# Approach |
|---------|-------------|-------------|
| `DependencyManager` | `infrastructure/dependencies.py` (99 lines) | `Process.Start` with async. `which` equivalent via `PATH` search. ~100 lines |
| `MediaProbeService` | `infrastructure/probe.py` (183 lines) | `Process` with stdout redirect, `System.Text.Json` `JsonDocument` parsing. ~180 lines |
| `ProcessRunner` | `infrastructure/execution.py` (489 lines) | `Process` with `OutputDataReceived`/`ErrorDataReceived` events, `CancellationToken` triggers `Process.Kill()`. ~200 lines |
| `JobExecutionService` | Part of `infrastructure/execution.py` | Orchestrates multi-pass with async/await. ~150 lines |
| `QueueService` | `infrastructure/queue.py` (301 lines) | Lock-protected list, async `ExecuteAllAsync`. ~250 lines |
| `SettingsService` | `infrastructure/settings.py` (143 lines) | `System.Text.Json` + `File.ReadAllTextAsync`. ~100 lines |
| `PresetService` | `infrastructure/presets.py` (142 lines) | Same pattern. ~120 lines |
| `CommandBuilder` | `core/commands.py` (426 lines) | Direct translation of ffmpeg arg building. ~400 lines |
| `ValidationService` | `core/validation.py` (723 lines) | Direct translation with pattern matching. ~550 lines |
| `AppPaths` | `infrastructure/app_paths.py` (29 lines) | `Environment.SpecialFolder.ApplicationData` on Windows, `~/Library/Application Support` on macOS, `XDG_CONFIG_HOME` on Linux. ~30 lines |

### What gets eliminated

**`app/execution.py` (147 lines -- 4 QThread worker classes)**: Entirely eliminated. In Avalonia with async/await, `[RelayCommand]` async methods run on the thread pool and marshal property changes back to the UI thread automatically. No QThread/QObject workers needed.

---

## ViewModel Architecture

### Hierarchy

```
MainWindowViewModel
  |-- DependencySetupViewModel
  |-- WorkspaceViewModel (active when dependencies satisfied)
        |-- SourcePanelViewModel
        |-- OutputSettingsViewModel (coordinator)
        |     |-- FormatViewModel (container + video codec)
        |     |-- AudioViewModel (audio mode/codec/bitrate)
        |     |-- EncodingStrategyViewModel (strategy selector)
        |     |     |-- FileSizeViewModel
        |     |     |-- BitrateViewModel
        |     |     |-- QualityViewModel (switches H26x vs VPx sub-controls)
        |     |-- StreamHandlingViewModel
        |-- PresetViewModel
        |-- CommandPreviewViewModel
        |-- ExecutionViewModel
        |-- QueuePanelViewModel
        |     |-- ObservableCollection<QueueItemViewModel>
        |     |-- QueueItemDetailsViewModel
```

### Communication patterns

- **Parent-child**: Direct `PropertyChanged` subscription (parent owns child, identical lifetimes)
- **Cross-cutting**: `WeakReferenceMessenger` from CommunityToolkit.Mvvm
  - `SourceLoadedMessage(SourceMediaInfo)` -- SourcePanel -> OutputSettings, CommandPreview, Execution
  - `OutputSettingsChangedMessage(OutputSettings)` -- OutputSettings -> CommandPreview, Execution, Preset
  - `DependenciesResolvedMessage(ExecutablePaths)` -- DependencySetup -> MainWindow

### Key ViewModel responsibilities

**MainWindowViewModel** (~100 lines): Holds `CurrentView` property (either `DependencySetupViewModel` or `WorkspaceViewModel`). Switches based on dependency discovery. Handles drag-drop routing.

**WorkspaceViewModel** (~120 lines): Coordinator. Creates and owns all child ViewModels. Subscribes to messages. Wires up cross-ViewModel data flow.

**OutputSettingsViewModel** (~200 lines): Owns format/audio/strategy/stream sub-VMs. Subscribes to their `PropertyChanged` events. Assembles the complete `OutputSettings` record from sub-VM state. Triggers re-validation when source media changes.

**FormatViewModel** (~100 lines): `SelectedContainer`, `SelectedVideoCodec` with filtered `AvailableVideoCodecs`/`AvailableAudioCodecs`. Uses suppression flag to prevent circular updates on container change (same pattern as the Python `_updating_form` guard).

**ExecutionViewModel** (~120 lines): `StartJobCommand` (async), `CancelJobCommand`. Holds `CancellationTokenSource`. Uses `IProgress<JobProgress>` for UI-thread progress updates.

**QueuePanelViewModel** (~150 lines): `ObservableCollection<QueueItemViewModel>`. Batch probe with `AddFilesCommand`/`AddFolderCommand`. Sequential execution via `IQueueService.ExecuteAllAsync`.

### Async pattern for all background work

```csharp
[RelayCommand]
private async Task LoadSourceAsync(string path)
{
    IsProbing = true;
    try
    {
        var info = await _probeService.ProbeAsync(path);
        MediaInfo = info;
        Messenger.Send(new SourceLoadedMessage(info));
    }
    catch (Exception ex) { await _dialogService.ShowErrorAsync("Probe Failed", ex.Message); }
    finally { IsProbing = false; }
}
```

For long-running execution with progress:
```csharp
var cts = new CancellationTokenSource();
var progress = new Progress<JobProgress>(p => { Percentage = p.Percentage ?? 0; });
var result = await _executionService.ExecuteAsync(job, mediaInfo,
    progressCallback: ((IProgress<JobProgress>)progress).Report,
    ct: cts.Token);
```

### Dialogs

ViewModels use `IDialogService` interface (injected). Concrete `DialogService` uses Avalonia's `StorageProvider` for file/folder dialogs and content dialogs for messages/prompts. This keeps ViewModels testable.

---

## Views (AXAML)

### Navigation (Setup vs Workspace)

`MainWindow.axaml` uses `ContentControl` with `DataTemplates`:
```xml
<ContentControl Content="{Binding CurrentView}">
    <ContentControl.DataTemplates>
        <DataTemplate DataType="vm:DependencySetupViewModel"><views:DependencySetupView /></DataTemplate>
        <DataTemplate DataType="vm:WorkspaceViewModel"><views:WorkspaceView /></DataTemplate>
    </ContentControl.DataTemplates>
</ContentControl>
```

### Workspace Layout (3-panel splitter)

`WorkspaceView.axaml` uses nested `Grid` + `GridSplitter`:
```xml
<Grid ColumnDefinitions="300,Auto,*">
    <!-- Left: Source/Queue tabs -->
    <TabControl Grid.Column="0">
        <TabItem Header="Source"><views:SourcePanelView DataContext="{Binding SourcePanel}" /></TabItem>
        <TabItem Header="Queue"><views:QueuePanelView DataContext="{Binding QueuePanel}" /></TabItem>
    </TabControl>
    <GridSplitter Grid.Column="1" Width="6" />
    <!-- Right: Settings + Inspector -->
    <Grid Grid.Column="2" RowDefinitions="3*,Auto,2*">
        <ScrollViewer Grid.Row="0"><views:OutputSettingsView DataContext="{Binding OutputSettings}" /></ScrollViewer>
        <GridSplitter Grid.Row="1" Height="6" />
        <TabControl Grid.Row="2">
            <TabItem Header="Preview"><views:CommandPreviewView DataContext="{Binding CommandPreview}" /></TabItem>
            <TabItem Header="Execution"><views:ExecutionView DataContext="{Binding Execution}" /></TabItem>
        </TabControl>
    </Grid>
</Grid>
```

### Encoding Strategy Switching

`ContentControl` with `DataTemplates` -- each strategy ViewModel gets its own View:
```xml
<ContentControl Content="{Binding ActiveStrategyViewModel}">
    <ContentControl.DataTemplates>
        <DataTemplate DataType="vm:FileSizeViewModel"><views:FileSizeView /></DataTemplate>
        <DataTemplate DataType="vm:BitrateViewModel"><views:BitrateView /></DataTemplate>
        <DataTemplate DataType="vm:QualityViewModel"><views:QualityView /></DataTemplate>
    </ContentControl.DataTemplates>
</ContentControl>
```

Same pattern inside `QualityView` for H26x vs VPx sub-controls.

### Compiled Bindings

All views use `x:CompileBindings="True"` and `x:DataType="vm:XxxViewModel"` for compile-time binding verification.

### Drag-and-Drop

Minimal code-behind in `MainWindow.axaml.cs`:
```csharp
AddHandler(DragDrop.DropEvent, (_, e) => {
    if (DataContext is MainWindowViewModel vm)
    {
        var path = e.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath();
        if (path != null) vm.HandleFileDrop(path);
    }
});
```

### Custom Controls

- **NoWheelComboBox / NoWheelNumericUpDown**: Override `OnPointerWheelChanged`, ignore when not focused
- **SliderField**: UserControl with `Slider` + value `TextBlock` + recommended range indicator bar. Styled properties for `Value`, `Minimum`, `Maximum`, `RecommendedLower`, `RecommendedUpper`

### Splitter Persistence

Save `GridLength` values from `ColumnDefinitions`/`RowDefinitions` in `MainWindow.OnClosing`. Restore on startup from `AppSettings`. Window geometry (position, size, maximized state) saved/restored the same way.

---

## Implementation Sequence

Build bottom-up so each layer can be verified before the next:

### Phase 1: Project scaffold + Models
1. Create solution, project, folder structure
2. Add NuGet package references
3. Port all enums and records from `core/models.py`

### Phase 2: Service interfaces + Core logic
4. Define all service interfaces
5. Port `CommandBuilder` from `core/commands.py`
6. Port `ValidationService` from `core/validation.py` (includes compatibility matrices)

### Phase 3: Infrastructure services
7. Port `AppPaths` from `infrastructure/app_paths.py`
8. Port `DependencyManager` from `infrastructure/dependencies.py`
9. Port `MediaProbeService` from `infrastructure/probe.py`
10. Port `ProcessRunner` from `infrastructure/execution.py`
11. Port `JobExecutionService` (from `infrastructure/execution.py`)
12. Port `QueueService` from `infrastructure/queue.py`
13. Port `SettingsService` + `PresetService` + serialization from `infrastructure/settings.py`, `infrastructure/presets.py`, `infrastructure/serialization.py`

### Phase 4: ViewModels
14. `MainWindowViewModel` + `DependencySetupViewModel`
15. `SourcePanelViewModel`
16. `FormatViewModel` + `AudioViewModel` + `StreamHandlingViewModel`
17. `FileSizeViewModel` + `BitrateViewModel` + `QualityViewModel`
18. `EncodingStrategyViewModel` + `OutputSettingsViewModel` (coordinator)
19. `PresetViewModel`
20. `CommandPreviewViewModel`
21. `ExecutionViewModel`
22. `QueuePanelViewModel` + `QueueItemViewModel` + `QueueItemDetailsViewModel`
23. `WorkspaceViewModel` (wires everything together)

### Phase 5: Views + Controls
24. `App.axaml` + `Program.cs` + DI registration
25. Custom controls (NoWheelComboBox, SliderField)
26. Converters
27. `IDialogService` + `DialogService`
28. `MainWindow.axaml` + `DependencySetupView.axaml`
29. `WorkspaceView.axaml` + `SourcePanelView.axaml`
30. `OutputSettingsView.axaml` + all sub-views (Format, Audio, FileSize, Bitrate, Quality, StreamHandling)
31. `PresetBarView.axaml` + `CommandPreviewView.axaml`
32. `ExecutionView.axaml`
33. `QueuePanelView.axaml` + `QueueItemDetailsView.axaml`

### Phase 6: Formatting helpers + polish
34. Port `FormatHelpers` from `app/formatting.py`
35. Port `JobDefinitionBuilder` (build_job_definition, suggest_output_path from main_window.py)
36. Wire up splitter/window geometry persistence
37. Wire up drag-and-drop

### Phase 7: Tests
38. Port core tests (command builder, validation)
39. Port infrastructure tests (probe, settings, presets, queue)

---

## Critical Source Files

These are the Python files that contain the logic to port, in dependency order:

1. **`src/ffapp/core/models.py`** (404 lines) -- Every record, enum, and union type. Foundation for all other layers.
2. **`src/ffapp/core/services.py`** (132 lines) -- Service contracts / interfaces.
3. **`src/ffapp/core/commands.py`** (426 lines) -- FFmpeg command generation. Direct translation.
4. **`src/ffapp/core/validation.py`** (723 lines) -- Compatibility matrices + validation rules.
5. **`src/ffapp/infrastructure/app_paths.py`** (29 lines) -- Platform paths.
6. **`src/ffapp/infrastructure/dependencies.py`** (99 lines) -- FFmpeg/ffprobe discovery.
7. **`src/ffapp/infrastructure/probe.py`** (183 lines) -- FFprobe JSON parsing.
8. **`src/ffapp/infrastructure/execution.py`** (489 lines) -- Process execution + progress parsing.
9. **`src/ffapp/infrastructure/queue.py`** (301 lines) -- Queue management.
10. **`src/ffapp/infrastructure/settings.py`** (143 lines) -- Settings persistence.
11. **`src/ffapp/infrastructure/presets.py`** (142 lines) -- Preset persistence.
12. **`src/ffapp/infrastructure/serialization.py`** (425 lines) -- JSON serialization logic.
13. **`src/ffapp/app/main_window.py`** (1,227 lines) -- Orchestration logic to decompose into ViewModels.
14. **`src/ffapp/app/output_settings.py`** (1,013 lines) -- Settings form logic to split into ViewModels + AXAML.
15. **`src/ffapp/app/workflow_widgets.py`** (538 lines) -- Job/queue widget logic.
16. **`src/ffapp/app/formatting.py`** (497 lines) -- Text formatting helpers.
17. **`src/ffapp/app/qt.py`** (275 lines) -- Custom widgets to reimplement as Avalonia controls.
18. **`src/ffapp/app/execution.py`** (147 lines) -- **Eliminated** (async/await replaces QThread workers).

---

## Verification Plan

1. **Build**: `dotnet build` compiles without errors/warnings
2. **Unit tests**: `dotnet test` passes all ported tests
3. **Manual smoke test**:
   - Launch app, verify dependency setup screen appears if ffmpeg not on PATH
   - Configure ffmpeg/ffprobe paths, verify validation and transition to workspace
   - Load a media file via file picker, verify media info displays
   - Load a media file via drag-and-drop
   - Switch containers, verify codec dropdowns filter correctly
   - Switch encoding strategies, verify correct controls appear
   - Switch video codecs within quality strategy, verify CRF/quality controls switch
   - Configure audio options (copy/reencode/remove)
   - Verify ffmpeg command preview updates live
   - Execute a single job, verify progress bar and log output
   - Cancel a running job
   - Save/load/rename/delete presets
   - Add files to queue, execute queue, verify per-item progress
   - Verify settings persistence (close and reopen app)
   - Verify window geometry/splitter persistence
