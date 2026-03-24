# FFmpeg GUI Implementation Plan

This document turns the MVP in [mvp-plan.md](mvp-plan.md) into a phased implementation plan that a developer can execute.

## Goals

- Deliver a usable MVP in small, testable increments.
- Keep probing, validation, command generation, and process execution separate.
- Avoid UI-first shortcuts that would make queueing, presets, or future video filters harder to add.

## Recommended Solution Structure

Create a small multi-project solution instead of putting everything into the Photino UI project.

### Projects

- `src/FFApp.App`
  - Photino.Blazor host
  - UI components and pages
  - Dependency injection setup
  - app startup and desktop integration
- `src/FFApp.Core`
  - domain models
  - validation rules
  - compatibility matrix
  - command generation
  - queue orchestration contracts
  - preset and settings contracts
- `src/FFApp.Infrastructure`
  - `ffprobe` integration
  - `ffmpeg` process execution
  - settings persistence
  - preset persistence
  - file system helpers
- `tests/FFApp.Core.Tests`
  - validation tests
  - command generation tests
  - compatibility tests
- `tests/FFApp.Infrastructure.Tests`
  - parsing tests
  - settings serialization tests
  - queue/execution unit tests with fakes where possible

### High-Level Layering

- `FFApp.App` depends on `FFApp.Core` and `FFApp.Infrastructure`
- `FFApp.Infrastructure` depends on `FFApp.Core`
- `FFApp.Core` depends on nothing UI-specific

## Core Domain to Define Early

These types should be created before major UI work begins.

- `SourceMediaInfo`
- `VideoStreamInfo`
- `AudioStreamInfo`
- `SubtitleStreamInfo`
- `DataStreamInfo`
- `OutputContainer`
- `VideoCodec`
- `AudioCodec`
- `EncodingStrategy`
- `TargetFileSizeOptions`
- `TargetBitrateOptions`
- `TargetQualityOptions`
- `AudioMode`
- `AudioOptions`
- `ResolutionPreset`
- `StreamHandlingOptions`
- `PresetDefinition`
- `AppSettings`
- `JobDefinition`
- `JobState`
- `GeneratedCommand`
- `ValidationIssue`
- `ValidationResult`

## Core Services to Define Early

- `IMediaProbeService`
- `ISettingsService`
- `IPresetService`
- `ICompatibilityService`
- `IValidationService`
- `ICommandBuilder`
- `IProcessRunner`
- `IJobExecutionService`
- `IQueueService`

## Cross-Cutting Rules

- Keep UI state separate from persisted presets/settings models.
- Treat command generation as a pure function over validated job settings.
- Use immutable or near-immutable models for generated jobs where practical.
- Make validation deterministic and testable outside the UI.
- Parse `ffmpeg` progress in infrastructure, not in components.
- Design video transforms so resize is the first item in a future filter pipeline.

## Phase 0: Repository and Project Bootstrap

### Objective

Create the initial solution and the minimum app shell so later phases can land cleanly.

### Tasks

- Create the solution file.
- Create the `App`, `Core`, `Infrastructure`, and test projects.
- Wire project references.
- Add package references needed for Photino.Blazor and test frameworks.
- Set up dependency injection in the app project.
- Add a basic layout with placeholder navigation or sections.
- Add configuration support for app settings storage locations.
- Add a shared logging abstraction or use the built-in .NET logging pipeline.

### Deliverables

- Buildable solution
- Launchable desktop shell
- Basic DI container setup
- Empty but compilable service interfaces and models

### Acceptance Criteria

- `dotnet build` succeeds for the whole solution.
- The desktop app launches and shows a placeholder shell.
- Core and infrastructure projects compile independently of UI logic.

## Phase 1: Media Probing and Dependency Detection

### Objective

Implement dependency discovery and media inspection so the app can load a source file and understand it.

### Tasks

- Implement executable path discovery for `ffmpeg` and `ffprobe`.
- Implement manual path configuration and persistence.
- Implement runtime validation using `ffmpeg -version` and `ffprobe -version`.
- Implement `IMediaProbeService`.
- Probe media metadata using `ffprobe`.
- Parse:
  - duration
  - stream list
  - codec names
  - bitrates when available
  - channel counts
  - dimensions
  - frame rate if available
- Add source selection support:
  - file picker
  - drag and drop
  - paste file references
- Add a media info summary panel in the UI.

### Deliverables

- Setup screen for missing dependencies
- Source selection workflow
- Parsed media model displayed in the UI

### Acceptance Criteria

- User can load a supported source file.
- App shows media summary and stream counts.
- Invalid or missing dependency configuration is handled without crashing.
- Probe failures produce user-visible errors.

## Phase 2: Output Settings and Validation Engine

### Objective

Implement the full MVP settings model and all validation rules before process execution starts.

### Tasks

- Implement the output compatibility matrix.
- Implement container, video codec, and audio codec selection logic.
- Implement global audio mode options:
  - copy
  - re-encode
  - remove
- Implement audio re-encode options:
  - codec
  - bitrate
  - stereo remix
- Implement stream removal toggles:
  - subtitles
  - data
- Implement resolution preset model and orientation-aware target logic.
- Implement the encoding strategy model:
  - target file size
  - target bitrate
  - target quality
- Implement codec-specific quality option models:
  - H.264: CRF, preset, profile
  - H.265: CRF, preset, profile
  - VP8: quality, speed
  - VP9: quality, speed
- Implement bitrate warning thresholds and severity rules.
- Implement a validation engine that returns structured issues, not just strings.

### Deliverables

- Full settings UI bound to a stable domain model
- Structured validation engine
- Inline validation messages

### Acceptance Criteria

- UI only offers valid container/codec combinations.
- Invalid copy combinations are blocked before execution.
- Resolution presets work for portrait and landscape media.
- File-size mode computes validation outcomes based on probed media info.
- All validation rules are test-covered in `FFApp.Core.Tests`.

## Phase 3: Command Generation and Preview

### Objective

Generate correct `ffmpeg` commands from validated settings and show them in the UI.

### Tasks

- Implement `ICommandBuilder`.
- Define a `GeneratedCommand` model with:
  - executable path
  - argument list
  - display string
- Implement stream mapping rules for MVP:
  - first video stream only
  - `-map 0` base behavior
  - global audio/subtitle/data removal behavior
- Implement container-specific codec argument generation.
- Implement audio copy/re-encode/remix argument generation.
- Implement resolution scaling argument generation.
- Implement target file size bitrate calculation.
- Implement target bitrate generation.
- Implement quality mode generation for:
  - H.264
  - H.265
  - VP8
  - VP9
- Implement 2-pass command generation for bitrate-based modes.
- Add read-only command preview with copy and export actions.

### Deliverables

- Deterministic command builder
- Read-only preview in the UI
- Exportable command text

### Acceptance Criteria

- Every valid settings combination yields a command or command pair.
- Generated commands are covered by unit tests.
- Command preview always reflects the current UI state.
- 2-pass generation produces both passes correctly.

## Phase 4: Single-Job Execution, Progress, and Cancellation

### Objective

Execute one job end-to-end and report progress reliably.

### Tasks

- Implement `IProcessRunner`.
- Implement `IJobExecutionService`.
- Start `ffmpeg` as a child process.
- Parse progress from process output.
- Surface:
  - elapsed time
  - processed time
  - percentage when derivable
  - current speed if available
- Capture stderr and store it with the job result.
- Implement cancellation for the running job.
- Add a job details panel with command, state, and log output.

### Deliverables

- One fully working encode flow
- Progress display
- Cancel button
- Failure diagnostics

### Acceptance Criteria

- A single source file can be encoded successfully from the UI.
- Progress updates are visible during execution.
- Canceling a job stops the child process and marks the job as canceled.
- Failed jobs expose the command and process output.

## Phase 5: Queue and Batch Processing

### Objective

Add the real queue required by MVP and support batch execution with shared settings.

### Tasks

- Implement `IQueueService`.
- Model queue items independently from UI form state.
- Add batch file selection.
- Add folder import for supported files.
- Create one queue item per source file.
- Apply the active settings snapshot to each queued job.
- Execute jobs sequentially.
- Persist enough job state in memory to let the user inspect previous results.
- Define batch cancellation behavior:
  - cancel current job
  - mark remaining jobs as canceled
- Add queue list UI with job states and actions.

### Deliverables

- Batch queue
- Sequential runner
- Queue state UI

### Acceptance Criteria

- User can queue multiple files and process them in order.
- Each file runs with the settings that were active when it was queued.
- Batch cancellation behaves consistently.
- Completed, failed, and canceled states are visible per job.

## Phase 6: Presets and Settings Persistence

### Objective

Make the app practical for repeated use by implementing presets and persistent settings.

### Tasks

- Implement JSON-based preset persistence.
- Add schema versioning to preset files.
- Implement save, rename, load, and delete preset actions.
- Persist:
  - executable paths
  - last used settings
  - saved presets
- Add UI for preset selection and management.
- Validate loaded presets against the current compatibility rules.

### Deliverables

- Preset system
- Persistent app settings
- Preset management UI

### Acceptance Criteria

- User can save the current configuration as a named preset.
- Presets reload correctly after app restart.
- Invalid or outdated preset data is handled gracefully.

## Phase 7: Polish, Hardening, and Packaging Readiness

### Objective

Stabilize the MVP so it is ready for real use across supported desktop platforms.

### Tasks

- Improve empty states and validation messaging.
- Add confirmation UX for warnings.
- Add output collision tests and verify rename behavior.
- Verify clipboard, drag-and-drop, and file picker behavior on each target OS.
- Verify path handling for spaces and non-trivial file names.
- Verify progress parsing on short and long jobs.
- Add logging where failures are otherwise opaque.
- Review startup, shutdown, and cancellation edge cases.
- Prepare packaging and release checklist for Windows, macOS, and Linux.

### Deliverables

- Stable MVP candidate
- Release checklist
- Cross-platform verification notes

### Acceptance Criteria

- Core workflows work on all target platforms.
- The app handles common user errors without crashing.
- The developer has a release checklist for packaging and smoke testing.

## Suggested Development Order Inside Each Phase

For each phase, follow this order:

1. Define or update the domain model.
2. Implement the service logic.
3. Add unit tests for non-UI logic.
4. Bind the logic into the UI.
5. Perform a manual smoke test before moving to the next phase.

## Testing Strategy

### Unit Tests

Cover:

- compatibility rules
- validation rules
- bitrate calculations
- resolution mapping
- command generation
- preset serialization
- queue state transitions

### Manual Smoke Tests

Maintain a small set of real sample files:

- landscape video with audio
- portrait video with audio
- file with subtitles
- file with multiple audio streams
- file with multiple video streams
- file with missing or unreliable bitrate metadata

Use these to verify every major milestone.

## Risks and Early Mitigations

### Risk: `ffprobe` output variation

Mitigation:

- Parse conservatively.
- Keep probe parsing isolated and test it with sample JSON fixtures.

### Risk: UI and command logic drift apart

Mitigation:

- Use a single validated settings model as the input to command generation.
- Keep the preview sourced from the same `GeneratedCommand` used for execution.

### Risk: Batch jobs mutating when the user edits the form

Mitigation:

- Snapshot settings into each queue item at queue time.

### Risk: Resize logic becoming hard to extend

Mitigation:

- Represent resize as one video transform step in a future filter pipeline model.

## Definition of Done for MVP

The MVP is complete when:

- A user can choose a file, configure valid output settings, preview the generated command, and run an encode successfully.
- A user can queue multiple files with shared settings and monitor progress.
- Presets and dependency paths persist across restarts.
- Warnings and validation errors are shown before execution.
- The app behaves correctly on Windows, macOS, and Linux in manual smoke tests.
