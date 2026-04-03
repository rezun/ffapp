# Code Review: FFApp

**Date:** 2026-04-04

---

## Critical / High Severity

- [x] **1. Deadlock risk in `QueueService.Pause()`**

  **File:** `src/FFApp/Infrastructure/QueueService.cs:26-32`

  ~~`_pauseGate.Wait()` is called while holding `_lock`. If `ExecuteAllAsync` holds the semaphore and tries to acquire `_lock` (e.g., in `StartNextItem`, `UpdateItem`), this creates a classic lock-ordering deadlock. Make `Pause` async or acquire the semaphore outside the lock.~~

  **Resolution:** Moved `_pauseGate.Wait()` and `_pauseGate.Release()` outside the `lock` block in `Pause()` and `Resume()`.

- [x] **2. Redundant synchronous file I/O on startup**

  **File:** `src/FFApp/App.axaml.cs:34`

  ~~`SettingsService.LoadWindowGeometrySync()` performs blocking file I/O on the UI thread during `OnFrameworkInitializationCompleted`, but the same geometry is loaded again asynchronously in `MainWindow.OnLoaded`. The synchronous load appears unnecessary and violates the "avoid blocking calls" rule.~~

  **Resolution:** Sync load is intentional (avoids window flash). Consolidated geometry restoration to `App.axaml.cs` only, added screen-bounds validation, removed duplicate from `MainWindow.OnLoaded`.

- [x] **3. `async void` fire-and-forget in constructor**

  **File:** `src/FFApp/ViewModels/MainWindowViewModel.cs:38-39`

  ~~`InitializeAsync()` is called fire-and-forget from the constructor. The catch block silently swallows initialization failures with no UI feedback — the user sees nothing if settings loading fails.~~

  **Resolution:** Introduced `IAsyncInitializable` interface. Removed fire-and-forget from constructor; `MainWindow.OnLoaded` now awaits `InitializeAsync()`. All remaining `async void` methods (event handler bridges, `IRecipient<T>.Receive`) are documented with comments explaining why fire-and-forget is unavoidable, and wrapped in `try/catch`. `OnClosing` save path protected with `try/catch/finally` to ensure cleanup even on failure.

- [x] **4. `SemaphoreSlim` never disposed**

  **File:** `src/FFApp/Infrastructure/QueueService.cs:14`

  ~~`_pauseGate` is a `SemaphoreSlim` that is never disposed. `QueueService` should implement `IDisposable`.~~

  **Resolution:** `QueueService` now implements `IDisposable` and disposes `_pauseGate`.

- [x] **5. Race condition in ProcessRunner cancellation**

  **File:** `src/FFApp/Infrastructure/ProcessRunner.cs:53-86`

  ~~`ReadLineAsync(ct)` uses the cancellation token, but the process kill happens separately. Cancellation can fire before the process is killed, causing `OperationCanceledException` to race with stream closure.~~

  **Resolution:** Removed `ct` from `ReadLineAsync` calls. Streams now close naturally when the process is killed via the cancellation registration, avoiding the race.

- [x] **6. `new`-ing up dependencies directly**

  **Files:**
  - `src/FFApp/App.axaml.cs:48-49` — ~~`new SettingsService()` and `new PresetService()` in lambda factories instead of `services.AddSingleton<ISettingsService, SettingsService>()`.~~
  - `src/FFApp/ViewModels/WorkspaceViewModel.cs:33-57` — ~~All child ViewModels are `new`'d up directly.~~
  - `src/FFApp/ViewModels/MainWindowViewModel.cs:59-63, 82-86, 95` — ~~`DependencySetupViewModel` and `WorkspaceViewModel` are `new`'d up.~~

  **Resolution:** Registered all ViewModels in the DI container (`App.axaml.cs`). `SettingsService` and `PresetService` use standard `AddSingleton<TInterface, TImpl>()` registration. `MainWindowViewModel` now receives explicit dependencies + `Func<T>` factory delegates for on-demand VM creation (replacing `IServiceProvider` service locator). `WorkspaceViewModel` receives all 7 child VMs via constructor injection. `PresetViewModel`'s `Action<OutputSettings>` callback replaced with a `PresetApplied` event, wired by `WorkspaceViewModel`.

---

## Medium Severity

- [x] **7. Potential integer overflow in bitrate calculations**

  **Files:**
  - `src/FFApp/Core/Validation/CompatibilityData.cs:113` — `(int)(audioBitrate * durationSeconds * streams.Length)` can overflow for long videos (128kbps * 7200s * 2 streams = 1.84B, near `int.MaxValue`).
  - `src/FFApp/Core/CommandBuilder.cs:400` — Casts a potentially large `long` division result to `int`.

  ~~Use `long` for these calculations.~~

  **Resolution:** Changed `EstimateAudioBits` return type to `long?` throughout (`CompatibilityData`, `CommandBuilder`, `ValidationService`). Added `Math.Min` guard on video bitrate cast.

- [x] **8. Infinite loop with no upper bound**

  **File:** `src/FFApp/Helpers/FormatHelpers.cs:226-231`

  ~~`SuggestQueueOutputPath` has an unbounded `for` loop, unlike `SuggestOutputPath` which caps at 10,000.~~

  **Resolution:** Added 10,000 iteration cap with `InvalidOperationException` on exhaustion.

- [x] **9. `KeyNotFoundException` for audio-only containers**

  **File:** `src/FFApp/Infrastructure/Serialization/SettingsSerializer.cs:394`

  ~~`VideoCodecMatrix[container]` will throw for audio-only containers (M4a, Mka, OpusFile) if someone edits the settings file manually. Should use `TryGetValue` or `AllowedVideoCodecs`.~~

  **Resolution:** Changed to use `AllowedVideoCodecs()` which uses `TryGetValue` internally.

- [x] **10. Double validation of audio container compatibility**

  **File:** `src/FFApp/Core/Validation/ValidationService.cs`

  ~~`ValidateAudioContainerCompatibility` is called at line 65 (always) and again at line 163 (inside `ValidateContainerCompatibility`), potentially producing duplicate warnings.~~

  **Resolution:** Removed the duplicate call inside `ValidateContainerCompatibility`. The top-level call at line 65 covers all cases.

- [x] **11. Event handler returns `Task` instead of `void`**

  **File:** `src/FFApp/Views/MainWindow.axaml.cs:109`

  ~~`OnDrop` returns `Task` but is registered as a Drag event handler. Avalonia cannot await it; exceptions will be silently lost.~~

  **Resolution:** Changed `OnDrop` to `async void`.

- [x] **12. `LoadedSettings` is publicly mutable**

  **File:** `src/FFApp/ViewModels/MainWindowViewModel.cs:26`

  ~~`public AppSettings? LoadedSettings { get; set; }` is directly mutated from the View, breaking ViewModel encapsulation. Consider making the setter private and providing a method.~~

  **Resolution:** Made setter private, added `UpdateSettings(Func<AppSettings, AppSettings>)` method. Updated all callers in `MainWindow.axaml.cs`.

- [x] **13. `LastUsedSettings` never persisted**

  **File:** `src/FFApp/Views/MainWindow.axaml.cs:129-168`

  ~~`OnClosing` saves window geometry but never writes the current `OutputSettings` to `AppSettings.LastUsedSettings`, so users lose their working settings on restart.~~

  **Resolution:** `OnClosing` now captures `WorkspaceViewModel.CurrentSettings` into `LastUsedSettings` before saving. Exposed `CurrentSettings` as a public read-only property on `WorkspaceViewModel`.

---

## Code Duplication

- [x] **14. `VideoCodecLabel` duplicated**

  **Files:**
  - `src/FFApp/Helpers/FormatHelpers.cs:234-242`
  - `src/FFApp/Core/Validation/CompatibilityData.cs:156-165`

  ~~Identical method in two places. Consolidate to one location.~~

  **Resolution:** `FormatHelpers.VideoCodecLabel` now delegates to `CompatibilityData.VideoCodecLabel`. Single source of truth in the Core layer.

- [x] **15. `GetObjectOrEmpty` duplicated**

  **Files:**
  - `src/FFApp/Infrastructure/SettingsService.cs:153-163`
  - `src/FFApp/Infrastructure/Serialization/SettingsSerializer.cs:415-426`

  ~~Same helper in two files. `ReadBool` and `ReadOptionalInt` also appear duplicated between these files.~~

  **Resolution:** Made `GetObjectOrEmpty`, `ReadOptionalInt`, and `ReadBool` `internal` on `SettingsSerializer`. Removed duplicate copies from `SettingsService`, which now calls through to `SettingsSerializer`.

---

## Simplifications / Dead Code

- [x] **16. Unused constructor parameter**

  **File:** `src/FFApp/ViewModels/CommandPreviewViewModel.cs:33`

  ~~Constructor accepts `IValidationService` but never stores or uses it.~~

  **Resolution:** Removed `IValidationService` parameter from constructor and updated the caller in `WorkspaceViewModel`.

- [x] **17. Dead method `FormatTime`**

  **File:** `src/FFApp/ViewModels/VideoPreviewViewModel.cs:452-458`

  ~~`FormatTime` is defined but never called — only `FormatTimePrecise` is used.~~

  **Resolution:** Removed the unused `FormatTime` method.

- [x] **18. `EffectiveAudioMode` wrapper adds no value**

  **Files:**
  - `src/FFApp/Core/CommandBuilder.cs:467`
  - `src/FFApp/Core/Validation/ValidationService.cs:714`

  ~~Both simply return `settings.AudioOptions.Mode` with no additional logic. Remove the indirection.~~

  **Resolution:** Inlined all call sites to use `.AudioOptions.Mode` directly and removed both wrapper methods.

- [x] **19. `VideoPreviewViewModel.Dispose` missing `GC.SuppressFinalize`**

  **File:** `src/FFApp/ViewModels/VideoPreviewViewModel.cs:566-571`

  ~~Best practice for `IDisposable` implementations.~~

  **Resolution:** Added `GC.SuppressFinalize(this)` to `Dispose()`.

- [x] **20. `DependencyManager.ValidateExecutableAsync` ignores stderr**

  **File:** `src/FFApp/Infrastructure/DependencyManager.cs:105-136`

  ~~Standard error is redirected but never read. If `ffmpeg -version` writes to stderr, the version check succeeds but returns an empty version string.~~

  **Resolution:** Now reads both stdout and stderr concurrently. Falls back to stderr for version output if stdout is empty.

- [x] **21. No `CancellationToken` forwarding in several ViewModel commands**

  ~~`PresetViewModel.SavePresetAsync`, `RenamePresetAsync`, `DeletePresetAsync`, and `SourceListPanelViewModel.AddSinglePathAsync` do not pass cancellation tokens to underlying service calls.~~

  **Resolution:** Added `CancellationToken` parameters to all four methods and forwarded them to service calls.

- [x] **22. `VlcLocator.TryCreate()` called directly, not through DI**

  **File:** `src/FFApp/ViewModels/WorkspaceViewModel.cs:57`

  ~~Performs file system scanning and native library loading outside the DI container, making `VideoPreviewViewModel` hard to test.~~

  **Resolution:** `LibVLC` is now created in `App.axaml.cs` and registered in DI when available. `WorkspaceViewModel` resolves it via `GetService<LibVLC>()`.

---

## Architecture Notes

### Strengths

- Clean Core/Infrastructure/ViewModel separation
- Immutable record types throughout models
- `WeakReferenceMessenger` for loose ViewModel coupling
- `FrozenDictionary`/`FrozenSet` for static lookup data
- Good test coverage with builders, NSubstitute, FluentAssertions
- Compiled XAML bindings everywhere

### Suggestions

- **Add a logging framework** — Only `Debug.WriteLine` exists, invisible in Release builds. `Microsoft.Extensions.Logging` fits naturally with the existing DI setup.
- **`WorkspaceViewModel` fragility** — It manually coordinates many child VMs via `PropertyChanged` subscriptions. As the app grows, consider using messenger messages more consistently instead of direct wiring.
- ~~**ViewModel DI registration** — Consider registering ViewModel factories or using `ActivatorUtilities.CreateInstance` to make ViewModels resolvable from the container and easier to test.~~ **Done** — All ViewModels registered in DI. Workspace-scoped VMs use `AddScoped` so sibling VMs share instances within a workspace. `JobListPanelViewModel.Initialize()` temporal coupling eliminated.
