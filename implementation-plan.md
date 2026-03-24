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
- `tests/app`
  - app-layer formatting and widget-behavior tests that do not require a live desktop session
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
- `H264EncoderOptions`
- `H265EncoderOptions`
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

- Added phase-2 output models in `src/ffapp/core/models.py`, including `OutputSettings`, orientation-aware resolution handling, shared H.264/H.265 encoder tuning options, and codec-specific quality option types for H.264, H.265, VP8, and VP9.
- Implemented the compatibility matrix and structured validation engine in `src/ffapp/core/validation.py`, covering container/codec rules, audio copy and re-encode validation, subtitle/data stream copy constraints, file-size budget checks, bitrate warning thresholds, and validation of shared H.264/H.265 preset/profile selections.
- Replaced the output-settings placeholder UI in `src/ffapp/app/main_window.py` with a grouped settings panel for container, codec, audio, stream-removal, resolution, and encoding-strategy controls plus inline validation feedback and resolved output-resolution display.
- The settings UI now includes:
  - shared H.264/H.265 preset and profile controls for quality, target bitrate, and target file size modes
  - an audio bitrate preset dropdown with common values plus a custom bitrate field on the same row
  - dynamic strategy-panel sizing so the encoding section only uses the height needed by the active strategy
- Added `tests/core/test_validation.py` to cover compatibility rules, portrait-aware resolution presets, file-size mode validation, bitrate validation, shared encoder-option validation, and codec-specific quality settings.
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

Completed

### Developer Notes

- Implemented deterministic command generation in `src/ffapp/core/commands.py`, including output-file naming, stream mapping, codec argument selection, resolution scaling filters, bitrate/file-size calculations, shared H.264/H.265 preset/profile arguments for bitrate-based modes, compact `k` bitrate formatting, and 2-pass command generation.
- Added `tests/core/test_commands.py` to cover single-pass quality commands, portrait scaling, stream removal, target-file-size bitrate calculation, bitrate-mode preset/profile handling, 2-pass bitrate generation, and output-path collision handling.
- Replaced the phase-3 placeholder in `src/ffapp/app/main_window.py` with a live read-only command preview that refreshes from the current source/settings state and supports copy/export actions.
- The command preview now includes:
  - a `Single Command` option that merges 2-pass commands into one shell-style preview using `&&`
  - blocked-state messaging when validation errors prevent command generation
- Added `tests/app/test_main_window.py` to cover command-preview formatting for separate 2-pass output versus merged `&&` output.
- Verification completed locally via:
  - `.venv/bin/python -m pytest`
  - `.venv/bin/python -m ruff check src tests`
  - `.venv/bin/python -m mypy`
  - `QT_QPA_PLATFORM=offscreen .venv/bin/python -c 'from PySide6.QtWidgets import QApplication; from ffapp.app.main_window import MainWindow; from ffapp.infrastructure.dependencies import DependencyManager; from ffapp.infrastructure.settings import JsonSettingsService; app = QApplication([]); window = MainWindow(JsonSettingsService(), DependencyManager()); print(window.windowTitle())'`

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

Completed

### Developer Notes

- Added execution progress and result models in `src/ffapp/core/models.py` plus updated execution service contracts in `src/ffapp/core/services.py`.
- Implemented the execution pipeline in `src/ffapp/infrastructure/execution.py`, including:
  - `subprocess.Popen`-based `ffmpeg` execution
  - `-progress pipe:1` parsing for elapsed time, processed time, percentage, and speed
  - stderr capture for troubleshooting output
  - cancellation support for the active child process
  - automatic cleanup of 2-pass log files after the job finishes
- Added the Qt worker adapter in `src/ffapp/app/execution.py` so execution runs off the UI thread and progress/log updates marshal back through signals.
- Replaced the phase-4 placeholder in `src/ffapp/app/main_window.py` with a `Job Details` panel that includes:
  - start and cancel controls for the current single job
  - live state and progress reporting
  - captured `ffmpeg` log output
  - a collapsible `Job Command` section that is collapsed by default to avoid duplicating the always-visible command preview
- Added `tests/infrastructure/test_execution.py` for progress parsing, multi-pass execution behavior, failure short-circuiting, and cancellation delegation.
- Expanded `tests/app/test_main_window.py` to cover the job-details widget behavior, including the collapsed-by-default command section.
- Verification completed locally via:
  - `.venv/bin/python -m ruff check src tests`
  - `.venv/bin/python -m mypy`
  - `.venv/bin/python -m pytest`
- Manual end-to-end encoding with a real source file was not exercised as part of the automated verification.

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

Completed

### Developer Notes

- Added typed queue snapshots and queue-item state models in `src/ffapp/core/models.py` plus expanded queue service contracts in `src/ffapp/core/services.py`.
- Implemented the in-memory sequential queue runner in `src/ffapp/infrastructure/queue.py`, including:
  - per-item command snapshots so queued jobs keep the settings active when they were added
  - sequential execution over the existing job execution pipeline
  - active-job cancellation that also marks remaining pending items as canceled
  - retained per-item results and failure messages for later inspection
- Added `QueueExecutionWorker` in `src/ffapp/app/execution.py` so batch execution stays off the UI thread.
- Replaced the phase-5 queue placeholder in `src/ffapp/app/main_window.py` with a real batch queue UI that supports:
  - adding the currently loaded source
  - multi-file batch selection
  - folder import using a supported-extension filter
  - removing selected non-running items from the queue
  - sequential queue start/cancel controls
  - per-item state summaries, command inspection, and retained log output for the selected queue item
- Added `tests/infrastructure/test_queue.py` for sequential queue execution and cancellation behavior.
- Expanded `tests/app/test_main_window.py` to cover queue summary/detail rendering.
- Verification completed locally via:
  - `.venv/bin/python -m pytest`
  - `.venv/bin/python -m ruff check src tests`
  - `.venv/bin/python -m mypy`
- Manual end-to-end batch encoding with real media files was not exercised as part of the automated verification.

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

## Phase 6: Main Workflow Layout and UX Rework

### Status

Completed

### Developer Notes

- Reworked the main window from a long vertical stack into a split workflow layout.
- The main workspace now uses a horizontal splitter with left-side workflow tabs and a right-side settings and inspector column.
- The left side separates `Source` and `Queue` into distinct tabs so single-file and batch workflows no longer compete for the same screen space.
- The right side keeps output settings visible while moving command preview and execution inspection into tabs.
- Queue item inspection moved out of the queue management panel and into the shared execution inspector so commands and logs live in one place.
- Added a compact output summary at the top of the right pane so the current encode intent is visible without reading the full form.
- Refined the layout after the initial rework to improve real-world usability:
  - reduced minimum window size pressure and width constraints so the split panels can be resized more freely
  - moved `Validation and Output` below the settings groups instead of keeping it in a cramped side column
  - changed the right pane into a vertical splitter so the bottom inspector tabs can be resized with a visible handle
  - prevented mouse-wheel scrolling from accidentally changing combo box and spin box values unless the control has focus
  - added small spacer gaps between stacked group panels on both the left and right sides
- Verification completed locally via:
  - `.venv/bin/python -m pytest`
  - `.venv/bin/python -m ruff check src tests`
  - `.venv/bin/python -m mypy`

### Objective

Improve the core MVP workflow by restructuring the main UI around task-oriented panels instead of one long scrolling form.

### Tasks

- Replace the long stacked main screen with a horizontal split layout.
- Add left-side workflow tabs for:
  - source selection and media summary
  - queue management
- Keep output settings persistently visible on the right side.
- Add a compact output summary above the settings form.
- Move command preview and execution details into inspector tabs.
- Consolidate queue item inspection into the shared execution inspector.
- Make the right-side inspector area user-resizable.
- Reduce accidental value changes from mouse-wheel scrolling over numeric and selection controls.
- Improve section spacing and minimum-size behavior so the layout remains usable on smaller displays.
- Preserve the existing validation, preview, single-job execution, and queue behaviors while changing only the app-layer layout.

### Deliverables

- Split-panel main workspace
- Tabbed workflow navigation for source and queue modes
- Shared inspector area for preview and execution context
- Reduced duplication between single-job and queue inspection areas
- Resizable bottom inspector via vertical splitter
- Safer settings controls that ignore wheel changes without focus

### Acceptance Criteria

- Source and queue workflows are separated into distinct tabs.
- Output settings remain visible while the user inspects preview or execution state.
- Command preview, current job execution, and queue item inspection are available without returning to the old stacked layout.
- Validation status sits below the settings form instead of consuming horizontal form space.
- The lower preview/execution area can be resized by the user.
- Scrolling the pane does not accidentally change spin box or combo box values under the mouse cursor.
- Existing MVP encode and queue flows continue to function after the layout refactor.

## Phase 7: Presets and Settings Persistence

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

## Phase 8: Polish, Hardening, and Packaging Readiness

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
