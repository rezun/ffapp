# FFmpeg GUI MVP Plan

## Purpose

Build a small cross-platform desktop helper application for Windows, macOS, and Linux using .NET 10 and Photino.Blazor.

The app helps users generate and execute valid `ffmpeg` commands through a guided UI instead of typing commands manually.

## Core Product Decisions

- The app must invoke `ffmpeg` directly for all output generation.
- Media inspection may use `ffprobe` directly or a small .NET wrapper around `ffprobe`/media metadata.
- `ffmpeg` and `ffprobe` are required runtime dependencies.
- The app is greenfield and should be designed as an MVP first, with later expansion after the core workflow is stable.
- The UI must prevent invalid container/codec combinations instead of allowing them and failing later.
- The generated `ffmpeg` command is read-only in the UI, but it must be visible, copyable, and exportable.
- The app targets single-file and batch processing in MVP.
- Multiple video streams are not supported in MVP. The app should warn and use the first video stream only.
- Global audio settings apply to all audio streams in a job. Per-stream audio settings are out of scope for MVP.

## Non-Goals for MVP

- Shell integration such as "Open With".
- Direct command editing in the UI.
- Manual stream-by-stream editing for audio, subtitle, or data streams.
- Support for multiple video streams beyond warning and selecting the first one.
- Subtitle re-encoding.
- Data stream re-encoding.
- Arbitrary resize input beyond the fixed presets defined below.

## Runtime Requirements

- .NET 10 runtime for the app.
- Photino.Blazor desktop shell.
- `ffmpeg` available on `PATH` or manually configured by the user.
- `ffprobe` available on `PATH` or manually configured by the user.

## Startup and Dependency Discovery

On startup, the app should:

1. Try to locate `ffmpeg` and `ffprobe` on `PATH`.
2. If either is missing, show a setup screen.
3. Allow the user to manually configure the executable paths.
4. Validate the executables by running:
   - `ffmpeg -version`
   - `ffprobe -version`
5. Store validated paths in user settings.

## Main User Flows

### Single File

The user can:

- Choose a source file through a file picker.
- Drag and drop a file into the main window.
- Paste a file path or file reference from the clipboard when the platform provides it.

Then the user configures output settings, previews the generated command, and starts the job.

### Batch Processing

The user can:

- Choose multiple source files.
- Choose a folder and let the app add supported files from that folder.

All batch items share the same active settings or selected preset.

Batch processing must use a real queue with sequential execution, progress tracking, cancellation, and per-job result state.

## Supported Output Formats

### Video Codecs

- H.264
- H.265
- VP8
- VP9

### Audio Codecs

- MP3
- AAC
- Vorbis
- Opus

### Containers in MVP

- MP4
- MKV
- OGG

`MOV` is deferred until after MVP.

## Container and Codec Compatibility Rules

The UI must only allow valid combinations.

### MP4

- Video: H.264, H.265
- Audio: AAC, MP3

### MKV

- Video: H.264, H.265, VP8, VP9
- Audio: MP3, AAC, Vorbis, Opus

### OGG

- Video: VP8, VP9
- Audio: Vorbis, Opus

If existing copied streams are not compatible with the selected output container, the app must show inline validation errors and block execution.

## Stream Handling Rules

### Default Mapping

- Use `-map 0` by default.
- Copy all input streams by default where compatible.

### User Toggles

The user can globally:

- Remove all audio streams.
- Remove all subtitle streams.
- Remove all data streams.

### Video Stream Behavior

- Use the first video stream only in MVP.
- If multiple video streams are detected, show a warning before execution.

### Audio Stream Behavior

Global audio mode applies to all audio streams:

- Copy all audio streams.
- Re-encode all audio streams with a selected codec and bitrate.
- Remove all audio streams.

Optional audio remix:

- Remix to stereo when enabled.
- Remixing to stereo always implies audio re-encoding.

### Subtitle and Data Streams

- Subtitle streams can be copied or removed.
- Data streams can be copied or removed.
- Re-encoding subtitles or data is out of scope for MVP.

## Encoding Strategies

The user must choose one encoding strategy for video output.

### 1. Target File Size

Inputs:

- Target size in MB
- Optional audio re-encode settings
- Optional 2-pass mode

Rules:

- This mode always uses bitrate-based video encoding.
- Audio defaults to stream copy if feasible.
- If audio stream copy does not provide reliable size estimation, the app must require audio re-encoding.
- Container overhead must be reserved in the size calculation.
- Default reserved muxing overhead: 1.5%.

Calculation model:

- `totalBudgetBits = targetSizeMB * 1024 * 1024 * 8`
- `usableBudgetBits = totalBudgetBits * 0.985`
- `audioBudgetBits = estimatedAudioBits`
- `videoBudgetBits = usableBudgetBits - audioBudgetBits`
- `videoBitrate = videoBudgetBits / durationSeconds`

Validation:

- If `videoBudgetBits <= 0`, block execution with an inline error.
- If the calculated video bitrate is unusually low for the chosen resolution, show a warning but still allow execution.
- If duration is unavailable, block this mode until probing succeeds.

2-pass:

- Optional.
- Available only for bitrate-based video encodes.
- The app must manage pass log files automatically.

### 2. Target Quality

This mode uses codec-specific quality controls rather than a target file size.

#### H.264

Expose:

- CRF
- Preset
- Profile

#### H.265

Expose:

- CRF
- Preset
- Profile

#### VP8

Suggested MVP controls:

- CQ/CRF value
- Speed setting

Implementation note:

- Use libvpx quality mode with `-crf` plus `-b:v 0`.
- Map the speed UI to `-cpu-used`.

#### VP9

Suggested MVP controls:

- CQ/CRF value
- Speed setting

Implementation note:

- Use libvpx-vp9 quality mode with `-crf` plus `-b:v 0`.
- Map the speed UI to `-cpu-used`.
- Enable encoder-side optimizations internally if useful, but keep the UI minimal.

Decision:

- MVP quality mode for VP8/VP9 will expose only `Quality` and `Speed`.
- Advanced VP8/VP9 controls such as tile columns, row multithreading, or deadline are not part of the user-facing MVP UI.

### 3. Target Bitrate

Inputs:

- Video bitrate
- Optional audio re-encode settings
- Optional 2-pass mode

Rules:

- Similar to Target File Size, but the user sets video bitrate directly.
- No file size target is enforced.
- Audio can be copied, removed, or globally re-encoded.

## Audio Encoding

If audio re-encoding is selected, the user can choose:

- Audio codec
- Target audio bitrate
- Stereo remix on or off

Supported audio codecs depend on the chosen container.

Defaults:

- Audio mode defaults to `copy` when compatible.
- Stereo remix defaults to `off`.

## Resolution Change

Resolution presets are required in MVP.

### Supported Presets

- Original
- 720p
- 1080p
- 1440p
- 4K

### Resolution Rules

- The video must always keep its aspect ratio.
- The app must dynamically apply the preset to horizontal and vertical videos.
- The preset defines a target bounding box:
  - 720p: `1280x720`
  - 1080p: `1920x1080`
  - 1440p: `2560x1440`
  - 4K: `3840x2160`
- For portrait input, the bounding box is treated as rotated:
  - 720p portrait target: `720x1280`
  - 1080p portrait target: `1080x1920`
  - 1440p portrait target: `1440x2560`
  - 4K portrait target: `2160x3840`
- The output should fit inside the selected bounding box while preserving aspect ratio.
- MVP should not upscale smaller sources by default.
- Dimensions must be adjusted to encoder-safe values where needed.

## Generated Command Visibility

The generated `ffmpeg` command must be:

- Visible before execution
- Read-only
- Copyable to clipboard
- Exportable to a text file

The command preview must always match the effective UI state.

## Validation Rules

The app must validate before queueing or running a job.

Validation includes:

- Source file exists
- `ffmpeg` path is configured and valid
- `ffprobe` path is configured and valid
- Container/video/audio combination is valid
- Stream-copy choices are compatible with the output container
- Target file size is large enough to fit the selected audio strategy
- Resolution preset is valid for the source and target codec
- Multiple video streams trigger warning state
- Missing or unreliable audio bitrate in file-size mode triggers forced audio re-encode

Validation severity:

- Error: execution blocked
- Warning: execution allowed after user confirmation or visible acknowledgment

### Low Bitrate Warning Thresholds

For bitrate-based validation, the app should use hard-coded warning thresholds based on target resolution and video codec.

These thresholds apply to the video bitrate only.

Resolution is based on output pixel dimensions, not orientation, so landscape and portrait variants of the same preset share the same threshold.

Default warning thresholds:

| Resolution | H.264 | H.265 | VP8 | VP9 |
|---|---:|---:|---:|---:|
| 720p | 1.2 Mbps | 0.8 Mbps | 1.5 Mbps | 0.9 Mbps |
| 1080p | 2.0 Mbps | 1.2 Mbps | 2.5 Mbps | 1.4 Mbps |
| 1440p | 4.0 Mbps | 2.5 Mbps | 5.0 Mbps | 3.0 Mbps |
| 4K | 8.0 Mbps | 5.0 Mbps | 10.0 Mbps | 6.0 Mbps |

Rules:

- If `video bitrate < threshold`, show a low bitrate warning.
- If `video bitrate < 60% of threshold`, show a stronger warning indicating quality is very likely to be poor.
- For 50 fps or 60 fps output, multiply the warning threshold by `1.25`.
- These warnings apply to `Target File Size` and `Target Bitrate` modes.
- These warnings do not need to apply to `Target Quality` mode in MVP.

## Queue and Execution Model

The app needs a real job queue.

### Queue Behavior

- One job runs at a time in MVP.
- Jobs have states:
  - Pending
  - Running
  - Completed
  - Failed
  - Canceled
- Batch processing adds one queue item per source file.
- All queued items use the current active settings or a selected preset.

### Execution Behavior

- Each job generates a concrete `ffmpeg` command.
- The app executes `ffmpeg` as a child process.
- Progress is parsed from `ffmpeg` process output.
- Standard error and command details should be retained per job for troubleshooting.
- The current running job can be canceled.
- Canceling a batch should stop the active job and leave the remaining jobs pending or canceled according to the chosen UX policy.

## Output File Naming

Defaults:

- Output file is written next to the source file.
- Filename format: original base name plus new extension.
- If a file already exists, append `-1`, `-2`, and so on.

Example:

- `video.mov` to MP4 becomes `video.mp4`
- If `video.mp4` already exists, use `video-1.mp4`

## Presets

The user can save the current settings as a named preset.

Preset examples:

- `Discord`
- `Archive H265`
- `VP9 Web`

Each preset must include:

- Name
- Container
- Video codec
- Encoding strategy
- Codec-specific options
- Audio mode and options
- Resolution preset
- Stream removal flags

Storage rules:

- Store presets as JSON in the user profile/application data directory.
- Include a schema version for future migration.

## Settings Persistence

The app should persist:

- Last used output settings
- Saved executable paths
- Saved presets
- Last used output folder preference if later added

## Clipboard and Drag-and-Drop Support

MVP input methods:

- File picker
- Drag and drop
- Paste file references from clipboard

Clipboard support only needs to handle file references or paths, not arbitrary binary media blobs.

## Error Handling and UX Requirements

- Inline validation errors must appear near the conflicting setting where practical.
- Missing dependencies should lead to a dedicated setup screen, not a generic crash.
- Failed jobs must surface the generated command and process output for troubleshooting.
- Warnings should be human-readable and explain the likely outcome.
- The UI should keep advanced behavior understandable without exposing raw `ffmpeg` complexity by default.

## Technical Implementation Notes

- Use a C# domain model to represent:
  - source media info
  - target container
  - target codecs
  - encoding strategy
  - audio strategy
  - resolution preset
  - stream toggles
  - preset definition
  - queue job state
- Treat command generation as a pure transformation from validated settings to a concrete process invocation model.
- Keep probing, validation, command generation, and process execution separated into distinct services.
- Avoid coupling the UI directly to raw `ffmpeg` argument strings.

## Recommended MVP UI Sections

- Source selection
- Media info summary
- Output format
- Video encoding strategy
- Audio settings
- Stream toggles
- Resolution preset
- Command preview
- Queue
- Presets
- Dependency status

## Explicit MVP Exclusions

- MOV output
- Per-stream audio overrides
- Multiple concurrent encode jobs
- Manual subtitle codec selection
- Manual data stream mapping
- Multiple video stream selection
- Editable command text
- Arbitrary custom resize values

## Open Items for Post-MVP

- MOV container support
- Per-stream audio settings
- Advanced VP8/VP9 controls
- FPS change
- Crop mode
- Pad mode
- Exact target frame sizing strategies
- Trim start/end
- Metadata copy vs strip controls
- Attachment and chapter handling
- Editable filename templates
- Custom output directories
- Multiple concurrent jobs
- Manual stream mapping UI
- More detailed bitrate warning heuristics by codec and resolution

## Future Features Worth Planning For

These features are good candidates for v2 or later and should influence the internal design of MVP even if they are not exposed in the first release.

### Video Filter Pipeline

MVP should not model resizing as a one-off special case.

Instead, the internal domain model should be flexible enough to support a future ordered video filter pipeline so later features can be added without refactoring the whole command generation system.

Likely future video filter steps:

- Resize to fit
- Resize to fill with crop
- Pad to exact target size
- FPS conversion
- Trim start/end

### Crop and Pad Modes

The current MVP only supports aspect-ratio-preserving resize to fit within a target bounding box.

Future versions will likely need:

- Fit inside target without cropping
- Fill target by cropping
- Pad to exact dimensions

This should be planned as an extension of the video filter pipeline rather than a separate ad hoc option set.

### FPS Change

Changing frame rate is a useful future feature but is intentionally out of scope for MVP.

It should be added later as another optional video transformation step.
