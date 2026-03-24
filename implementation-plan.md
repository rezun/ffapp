# FFmpeg GUI Implementation Plan

This document turns the MVP in [mvp-plan.md](mvp-plan.md) into a phased implementation plan that a developer can execute.

## Goals

- Deliver a usable MVP in small, testable increments.
- Keep probing, validation, command generation, and process execution separate.
- Avoid UI-first shortcuts that would make queueing, presets, or future video filters harder to add.
- Keep non-UI logic independent from PySide6 so it can be tested without a running `QApplication`.
- Use modern Python 3.12+ conventions with explicit type hints as the default implementation style.

## Coding Standards

- Add explicit type hints to public functions, methods, dataclasses, protocols, and important internal helpers.
- Prefer `pathlib`, `dataclass`, `Enum`, `Protocol`, and built-in generic types over legacy patterns.
- Keep core models typed and structured rather than passing loosely shaped dictionaries between layers.
- Use small, composable functions and classes with clear ownership of side effects.
- Favor standard-library solutions unless a dependency materially simplifies the design.

## Recommended Solution Structure

Create a small Python application package with clear boundaries instead of putting everything into one PySide6 module.

### Package Layout

- `pyproject.toml`
  - project metadata
  - runtime dependencies
  - dev tooling configuration
- `src/ffapp/__main__.py`
  - app entry point
  - startup composition root
- `src/ffapp/app`
  - PySide6 application bootstrap
  - main window, dialogs, and widgets
  - view models or presentation-state adapters
  - signal wiring and desktop integration
- `src/ffapp/core`
  - domain models
  - validation rules
  - compatibility matrix
  - command generation
  - queue orchestration contracts
  - preset and settings contracts
- `src/ffapp/infrastructure`
  - `ffprobe` integration
  - `ffmpeg` process execution
  - settings persistence
  - preset persistence
  - file system helpers
- `tests/core`
  - validation tests
  - command generation tests
  - compatibility tests
- `tests/infrastructure`
  - parsing tests
  - settings serialization tests
  - queue/execution unit tests with fakes where possible

### High-Level Layering

- `ffapp.app` depends on `ffapp.core` and `ffapp.infrastructure`
- `ffapp.infrastructure` depends on `ffapp.core`
- `ffapp.core` depends on standard-library Python only where practical
- PySide6 stays in the app layer, except for thin adapter types where desktop integration requires it

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

Use `dataclass` and `Enum` types unless there is a strong reason not to.
Use modern typing features consistently so the core domain is readable and tool-friendly.

## Core Services to Define Early

Define these as small contracts, typically via `Protocol`, abstract base classes, or narrow concrete service boundaries.

- `MediaProbeService`
- `SettingsService`
- `PresetService`
- `CompatibilityService`
- `ValidationService`
- `CommandBuilder`
- `ProcessRunner`
- `JobExecutionService`
- `QueueService`

## Cross-Cutting Rules

- Keep UI state separate from persisted presets/settings models.
- Treat command generation as a pure function over validated job settings.
- Use immutable or near-immutable models for generated jobs where practical.
- Make validation deterministic and testable outside the UI.
- Parse `ffmpeg` progress in infrastructure, not in widgets.
- Keep Qt-specific types out of core models and service contracts.
- Design video transforms so resize is the first item in a future filter pipeline.

## Phase Tracking

Each phase should retain two small handoff fields:

- `Status`
  - Use one of: `Not Started`, `In Progress`, `Blocked`, `Completed`
- `Developer Notes`
  - Record what actually shipped, what was deferred, verification run, and any local environment limits that affected validation

## Phase 0: Repository and Project Bootstrap

### Status

Completed

### Developer Notes

- Created the Python project bootstrap in `pyproject.toml` with runtime and dev dependency groups plus baseline `pytest`, `ruff`, and `mypy` configuration.
- Added the initial package layout under `src/ffapp` and `tests`, including importable `core`, `infrastructure`, and `app` modules.
- Added the application entry point and startup composition root in `src/ffapp/__main__.py` and `src/ffapp/app/bootstrap.py`.
- Added structured logging setup and app-data/settings path helpers.
- Added a placeholder desktop shell in `src/ffapp/app/main_window.py` with top-level sections reserved for later phases.
- Added baseline non-UI unit tests and ensured the code compiles cleanly.
- Created a local `.venv` and installed the declared dependencies with `pip install -e '.[dev]'`.
- Verification now completed against the local virtual environment:
  - `.venv/bin/python -m pytest`
  - offscreen Qt startup smoke test for the main window

### Objective

Create the initial Python project and the minimum app shell so later phases can land cleanly.

### Tasks

- Create `pyproject.toml`.
- Create the `src/ffapp` package and the `tests` directories.
- Add runtime dependencies for PySide6 and any small supporting libraries if truly needed.
- Add dev dependencies and configuration for `pytest`.
- Add linting and static type-checking configuration appropriate for a typed Python codebase.
- Add the application entry point and startup composition root.
- Set up a basic main window with placeholder sections.
- Add configuration support for app settings storage locations.
- Add structured logging using the standard `logging` module.

### Deliverables

- Runnable project
- Launchable desktop shell
- Basic service composition setup
- Empty but importable service interfaces and models

### Acceptance Criteria

- `python -m pytest` succeeds for the initial test suite.
- `python -m ffapp` launches and shows a placeholder window.
- Core and infrastructure modules import independently of UI logic.
- The project includes baseline tooling to validate typed, modern Python code.

## Phase 1: Media Probing and Dependency Detection

### Status

Completed

### Developer Notes

- Implemented executable discovery and validation in `src/ffapp/infrastructure/dependencies.py`, including saved-path preference and `-version` checks for both `ffmpeg` and `ffprobe`.
- Implemented JSON settings persistence for executable paths in `src/ffapp/infrastructure/settings.py`.
- Implemented `ffprobe`-based media probing and JSON parsing in `src/ffapp/infrastructure/probe.py`, including audio and subtitle language tags when available.
- Added typed media models for source/container/stream data in `src/ffapp/core/models.py`.
- Added the dependency setup screen, source file picker, drag-and-drop handling, clipboard path paste flow, and media summary panel in `src/ffapp/app/main_window.py`.
- The media summary now includes per-audio-stream details such as codec, language, channel count, and bitrate.
- Probe failures and invalid dependency configuration now surface as user-visible messages instead of crashing.
- Verification completed locally via:
  - `python3 -m compileall src tests`
  - `.venv/bin/python -m pytest`
  - offscreen Qt startup smoke test that instantiated `MainWindow` successfully under `PySide6`

### Objective

Implement dependency discovery and media inspection so the app can load a source file and understand it.

### Tasks

- Implement executable path discovery for `ffmpeg` and `ffprobe`.
- Implement manual path configuration and persistence.
- Implement runtime validation using `ffmpeg -version` and `ffprobe -version`.
- Implement `MediaProbeService`.
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

### Status

Completed

### Developer Notes

- Added phase-2 output models in `src/ffapp/core/models.py`, including `OutputSettings`, orientation-aware resolution handling, and codec-specific quality option types for H.264, H.265, VP8, and VP9.
- Implemented the compatibility matrix and structured validation engine in `src/ffapp/core/validation.py`, covering container/codec rules, audio copy and re-encode validation, subtitle/data stream copy constraints, file-size budget checks, and bitrate warning thresholds.
- Replaced the output-settings placeholder UI in `src/ffapp/app/main_window.py` with a grouped, space-efficient settings panel for container, codec, audio, stream-removal, resolution, and encoding-strategy controls plus inline validation feedback and resolved output-resolution display.
- Added `tests/core/test_validation.py` to cover compatibility rules, portrait-aware resolution presets, file-size mode validation, bitrate validation, and codec-specific quality settings.
- Verification completed locally via:
  - `.venv/bin/python -m ruff check src tests`
  - `.venv/bin/python -m pytest`
  - offscreen Qt startup smoke test that instantiated `MainWindow` successfully under `PySide6`

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
- All validation rules are test-covered in `tests/core`.

## Phase 3: Command Generation and Preview

### Status

Not Started

### Developer Notes

- Placeholder only. The main window already reserves a `Command Preview` section for this phase.

### Objective

Generate correct `ffmpeg` commands from validated settings and show them in the UI.

### Tasks

- Implement `CommandBuilder`.
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

### Status

Not Started

### Developer Notes

- No execution pipeline has been implemented yet.

### Objective

Execute one job end-to-end and report progress reliably.

### Tasks

- Implement `ProcessRunner`.
- Implement `JobExecutionService`.
- Start `ffmpeg` as a child process using `subprocess.Popen`.
- Run process monitoring off the UI thread.
- Parse progress from process output.
- Surface:
  - elapsed time
  - processed time
  - percentage when derivable
  - current speed if available
- Marshal job updates back to the UI safely through signals or a thin event adapter.
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

### Status

Not Started

### Developer Notes

- Placeholder only. The main window already reserves a `Queue` section for this phase.

### Objective

Add the real queue required by MVP and support batch execution with shared settings.

### Tasks

- Implement `QueueService`.
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

### Status

Not Started

### Developer Notes

- Executable path persistence exists from Phase 1, but preset persistence and last-used encode settings are still pending.

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
- Store app data under the platform-appropriate user data directory.
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

### Status

Not Started

### Developer Notes

- Packaging and cross-platform hardening have not started.

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
- Review startup, shutdown, threading, and cancellation edge cases.
- Prepare packaging and release checklist for Windows, macOS, and Linux.
- Decide and document the packaging path, such as PyInstaller or Nuitka.

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

Use `pytest` for non-UI tests and keep them runnable without a desktop session.
Include static type checking in the regular validation workflow once the initial project bootstrap is in place.

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

### Risk: UI freezes during probing or encoding

Mitigation:

- Keep `ffprobe` and `ffmpeg` execution off the main Qt thread.
- Send progress back through explicit events or signals rather than letting widgets own process state.

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
