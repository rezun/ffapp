# FFRux - Requirements

## Overview

FFRux is a desktop app for **remuxing**: it combines tracks from one or more source files into exactly one output file, without re-encoding video or audio. MKVToolNix (mkvmerge GUI) is the model for the workflow, but FFRux writes many more output formats than Matroska. It covers the common video and audio formats FFApp supports, plus a few that are typical remux targets.

Every operation produces **one audio or video file**. Producing standalone subtitle or chapter files is out of scope.

## Goals

- Pick tracks from several sources and write them into one file quickly.
- Never transcode video or audio. Video and audio are always copied bit for bit.
- Tell the user before starting whether the selection fits the chosen output format, and why not if it does not.
- Make the common track metadata (name, language, flags) easy to correct.

## Scope

### In scope

- Adding one or more source files: media containers, standalone audio files and standalone subtitle files (text and bitmap).
- Showing every video, audio and subtitle track and the chapters of each source.
- Enabling and disabling individual tracks.
- Choosing an output format and an output path.
- Checking compatibility between the enabled tracks and the output format; blocking the start on incompatibilities.
- Converting text subtitles when their format does not fit the output format.
- Editing track name, language and flags.
- Shifting audio and subtitle tracks in time.
- Setting the container title.
- Running one job at a time with progress, cancel and a log.

### Out of scope

- Transcoding video or audio.
- Writing standalone subtitle, chapter or attachment files (extraction).
- OCR of bitmap subtitles (PGS, VobSub, DVB) into text, and any other conversion of bitmap subtitles.
- A job queue or batch processing of many files with the same settings.
- Appending files (part 1 + part 2) and cutting or splitting.
- Editing chapters, attachments or global tags other than the title.

See [After V1](#after-v1) for features that are planned but deliberately left out of the first version.

## Terms

| Term | Meaning |
|---|---|
| **Source** | A file the user added. A source has zero or more tracks and optionally chapters. |
| **Track** | One video, audio or subtitle stream of a source. |
| **Chapter set** | The chapters of one source. |
| **Output format** | The container and file extension that is written. |
| **Job** | One remux run: the enabled tracks of all sources, one chapter set (or none), one output file. |

## 1. Sources

### 1.1 Adding sources

- Through a file picker (multiple selection) and drag and drop onto the window.
- Files passed on launch or to the running instance (existing single-instance support) are added as sources.
- Each source is probed with FFprobe (`-show_format -show_streams -show_chapters`).
- A source that cannot be read stays in the list with an error message and contributes no tracks.
- Sources can be removed. Removing a source removes its tracks from the job.

### 1.2 Supported inputs

| Kind | Examples |
|---|---|
| Media containers | `.mkv`, `.mka`, `.mp4`, `.m4a`, `.m4b`, `.mov`, `.webm`, `.ts`, `.m2ts`, `.ogg`, `.opus` |
| Standalone audio | `.aac`, `.ac3`, `.eac3`, `.dts`, `.thd`, `.flac`, `.mp3`, `.wav` |
| Standalone text subtitles | `.srt`, `.ass`, `.ssa`, `.vtt` |
| Standalone bitmap subtitles | `.sup` (PGS), `.idx` + `.sub` (VobSub) |

The list is not a filter: anything FFprobe can read is accepted. The picker offers these extensions plus "all files".

### 1.3 Standalone subtitle files

- **Character encoding:** SRT and SSA files are often not UTF-8. FFRux detects UTF-8 (with or without BOM) and otherwise lets the user choose the encoding for that source (passed to FFmpeg as `-sub_charenc`).
- **Language and flags from the file name:** suffixes such as `Movie.de.srt`, `Movie.ger.forced.srt` or `Movie.en.sdh.srt` prefill language, forced and hearing impaired. The user can change the values.
- **VobSub:** the user adds the `.idx` file; FFmpeg reads the `.sub` file next to it. Adding the `.sub` file alone opens its `.idx` partner if it exists.
- **Bitmap subtitles are copied only.** They are never converted, so in practice they only fit Matroska outputs (section 4.3). The color palette from the `.idx` file must survive the remux into Matroska; this needs a test early on.

## 2. Tracks and chapters

### 2.1 Track list

All tracks of all sources appear in one list, grouped by type (video, audio, subtitles), in the order in which they will be written. Each row shows:

- Enabled checkbox
- Codec (readable label plus `codec_name`)
- Language
- Name
- Flags that are set (default, forced, hearing impaired, visual impaired, commentary, original)
- Details: resolution, frame rate and HDR format for video; channels, layout, sample rate and bitrate for audio; text or bitmap for subtitles; the timeshift if one is set
- Source file, as a numbered marker
- Compatibility status for the chosen output format (see section 4)

### 2.2 Enabling tracks

- Every track can be enabled or disabled with one click.
- Space toggles the selected tracks in the track list: if any of them is disabled, all are enabled; otherwise all are disabled.
- Each source in the source list has a checkbox for all of its tracks. It shows a mixed state when only some are enabled; clicking it then enables all of them.
- New tracks are enabled by default.
- At least one track must be enabled to start a job.

### 2.3 Track order

- Tracks are grouped by type, and the output contains video, then audio, then subtitles. Players only care about the order within a type, so mixing types is not offered.
- Within a group, the default order is the order in which the sources were added, and the stream order within each source.
- The user can reorder tracks within their group by drag and drop, also across sources. The order in the list is the order in the output.

### 2.4 Chapters

- Each source with chapters shows its chapter set as an entry with the chapter count.
- At most one chapter set can be enabled, because FFmpeg takes chapters from a single input (`-map_chapters`). By default it is the chapter set of the first source that has chapters.
- Chapters are shown read-only.

### 2.5 Font attachments for ASS/SSA

When an ASS/SSA track from a Matroska source is enabled and the output is Matroska, the font attachments of that source are copied too. Without them, the subtitles render with the wrong fonts. This is automatic and not editable.

### 2.6 Timeshift

Audio and subtitle tracks can be shifted in time, for example an external audio track that is 200 ms off. Video tracks are the reference and cannot be shifted.

- The shift is entered per track in milliseconds; positive values play the track later, negative values earlier.
- Few containers have a per-track delay field that players honor. FFRux therefore shifts the packet timestamps while remuxing, which works the same in every output format.
- FFmpeg's input options apply to a whole input file. When tracks of the same source need different shifts, that source is opened once per shift as separate inputs, and each track is mapped from the matching input.
- A positive shift uses `-itsoffset`. A negative shift seeks into the input (`-ss`) and drops the packets before the seek point (`-copypriorss 0`). A negative `-itsoffset` would move the whole output instead, including the video, which a test confirmed.

Behavior by track type (verified with FFmpeg 9, see the integration tests):

| Track | Positive shift | Negative shift |
|---|---|---|
| Text subtitles | Exact | Exact; cues that start before 0 are dropped |
| Bitmap subtitles | Exact | Exact; pictures that start before 0 are dropped |
| Audio | Exact | Exact; the packets before 0 are dropped, so the track may start a few milliseconds after 0 |

- In MP4-based outputs, a positive audio shift is stored as an edit list. Some players ignore edit lists; the track shows a warning in that case.

## 3. Output

### 3.1 Output formats

| Format | Extension | Kind | Notes |
|---|---|---|---|
| Matroska | `.mkv` | Video | Accepts nearly every codec |
| MP4 | `.mp4` | Video | |
| QuickTime | `.mov` | Video | Native home of ProRes and PCM |
| WebM | `.webm` | Video | VP8, VP9 and AV1 with Opus or Vorbis only |
| MPEG-TS | `.ts` | Video | Broadcast and Blu-ray codecs |
| Matroska audio | `.mka` | Audio | |
| MPEG-4 audio | `.m4a` | Audio | |
| MPEG-4 audiobook | `.m4b` | Audio | Like M4A, with chapters as the main use |
| Ogg | `.ogg` | Audio | |
| Opus | `.opus` | Audio | |
| MP3 | `.mp3` | Audio | Exactly one audio track |
| FLAC | `.flac` | Audio | Exactly one audio track |

Ogg is an audio format in FFRux. Its video codecs are Theora (the classic `.ogv`) and VP8, which FFRux does not target in V1. VP9 and AV1 cannot go into Ogg at all: FFmpeg's Ogg muxer rejects VP9, which a local test confirmed. FFApp currently offers Ogg with VP9 as a video target, which is a bug in FFApp.

### 3.2 Container title

The user can set a title for the output file. It starts with the title of the first source that has one.

| Format | Stored as |
|---|---|
| Matroska, WebM, MKA | Segment title |
| MP4, MOV, M4A, M4B | `©nam` |
| MP3 | ID3 `TIT2` |
| Ogg, Opus, FLAC | `TITLE` comment (in the stream header for Ogg and Opus) |
| MPEG-TS | Not supported; the field is disabled |

FFmpeg writes it with `-metadata title=…`. For audio formats the title is, in practice, the song or audiobook title.

### 3.3 Output path

- Default: the folder of the first source, file name of the first source with the new extension. This matches FFApp's "write next to the source" default.
- If the default would be one of the sources, a suffix is added (`Movie (remux).mkv`). Writing over a source is never allowed.
- If the output file exists, the user confirms overwriting before the job starts.

## 4. Compatibility check

### 4.1 Status levels

Every enabled track gets one status for the chosen output format:

| Status | Meaning | Blocks the start |
|---|---|---|
| **Copy** | The track is copied as is | No |
| **Convert** | Text subtitle that is converted to a format the output accepts (section 5) | No |
| **Warning** | Allowed, but many players do not support it (for example FLAC or Opus in MP4) | No |
| **Incompatible** | The output format cannot hold this track | Yes |

The check runs again whenever tracks, the output format or the order change. The track list shows the status per track. A summary lists every problem with the affected track. Incompatible tracks are not disabled automatically: the user decides whether to disable them or pick another format.

### 4.2 Structural rules

Besides the per-codec rules, the job is checked as a whole:

- Audio formats reject enabled video tracks.
- MP3 and FLAC accept exactly one audio track and no subtitles.
- Audio formats accept no subtitles, except MKA.
- At least one track is enabled.
- Several default tracks of the same type produce a warning.

### 4.3 Copy matrix

The matrix works on FFprobe `codec_name` values, not on FFApp's encoder enums, because FFRux also copies codecs FFApp cannot encode. It lives as data in `CodecRules` and was verified against FFmpeg 9 (section 8.3): every allowed entry is muxed and read back by the integration tests. "W" is a warning, "C" is a conversion, a missing entry is incompatible.

Some muxers accept more than the table allows: MPEG-TS writes almost anything as an unknown data stream, and MP4 takes Vorbis. FFRux still treats these as incompatible, because players cannot use the result.

**Video**

| Codec | mkv | mp4 | mov | webm | ts |
|---|---|---|---|---|---|
| `h264` | ✓ | ✓ | ✓ | | ✓ |
| `hevc` | ✓ | ✓ | ✓ | | ✓ |
| `av1` | ✓ | ✓ | | ✓ | |
| `vp9` | ✓ | W | | ✓ | |
| `vp8` | ✓ | | | ✓ | |
| `mpeg2video` | ✓ | W | ✓ | | ✓ |
| `mpeg4` | ✓ | ✓ | ✓ | | W |
| `vc1` | ✓ | | | | ✓ |
| `prores`, `dnxhd`, `mjpeg` | ✓ | | ✓ | | |
| `ffv1`, `theora` | ✓ | | | | |

AV1 in MPEG-TS is written as an unknown data stream by FFmpeg 9, so it is not offered. Other video codecs get a warning in MKV ("FFRux has not checked this codec"); FFmpeg decides when the remux starts.

**Audio**

| Codec | mkv/mka | mp4 | mov | m4a/m4b | webm | ts | ogg | opus | mp3 | flac |
|---|---|---|---|---|---|---|---|---|---|---|
| `aac`, `ac3` | ✓ | ✓ | ✓ | ✓ | | ✓ | | | | |
| `eac3` | ✓ | ✓ | ✓ | | | ✓ | | | | |
| `dts` | ✓ | W | W | | | ✓ | | | | |
| `truehd` | ✓ | | | | | ✓ | | | | |
| `flac` | ✓ | W | | | | | ✓ | | | ✓ |
| `alac` | ✓ | ✓ | ✓ | ✓ | | | | | | |
| `opus` | ✓ | W | | | ✓ | W | ✓ | ✓ | | |
| `vorbis` | ✓ | | | | ✓ | | ✓ | | | |
| `mp3` | ✓ | ✓ | ✓ | | | ✓ | | | ✓ | |
| `mp2` | ✓ | | ✓ | | | ✓ | | | | |
| `pcm_*` | ✓ | W | ✓ | | | | | | | |

M4A and M4B use FFmpeg's `ipod` muxer, which writes Apple's brands and accepts only Apple's codecs (AAC, AC-3, ALAC). MP2 in MP4 reads back as MP3, so it is not offered.

**Subtitles**

| Codec | mkv/mka | mp4/mov | webm | ts |
|---|---|---|---|---|
| `subrip`, `ass`, `ssa` | ✓ | C | C | |
| `webvtt` | ✓ | C | ✓ | |
| `mov_text` | C | ✓ | C | |
| other text formats | C | C | C | |
| `hdmv_pgs_subtitle` | ✓ | | | |
| `dvd_subtitle` | ✓ | | | |
| `dvb_subtitle` | ✓ | | | ✓ |

MP4 can technically carry `dvd_subtitle`, but hardly any player shows it, so FFRux treats it as incompatible. The bitmap rows are not covered by the integration tests yet, because FFmpeg cannot create bitmap subtitles; they need small fixture files.

**Chapters**

| mkv/mka | webm | mp4/m4a/m4b/mov | ogg/opus | mp3 | flac | ts |
|---|---|---|---|---|---|---|
| ✓ | ✓ | ✓ | ✓ | ✓ | ✗ | ✗ |

If the chosen format cannot hold chapters, an enabled chapter set produces a warning (not a block); chapters are then dropped.

### 4.4 Container-specific handling

The check also covers things a plain `-c copy` does not solve:

- HEVC in MP4/MOV is written with the `hvc1` tag so Apple players accept it.
- Dolby Vision is detected. MKV, MP4 and MOV keep the Dolby Vision configuration (MP4 and MOV need `-strict unofficial`); other formats show a warning. This is not covered by tests yet, because FFmpeg cannot create Dolby Vision sources.
- Sources with different start times are aligned: FFmpeg moves the start of every input to 0, so external tracks line up with the main file.
- QuickTime stores languages as Macintosh language codes. FFmpeg drops languages it cannot map, so FFRux uses the code FFmpeg knows (`fra` instead of `fre`) and warns when QuickTime cannot store the language at all.

## 5. Subtitle conversion

- Only text subtitles are converted. Bitmap subtitles cannot be converted (no OCR) and are incompatible with formats that do not accept them.
- Target per output format: `mov_text` for MP4/MOV, `webvtt` for WebM, `subrip` for `mov_text` going into Matroska.
- ASS/SSA lose their styling (fonts, positions, colors) when converted. The track shows this as a note on its "Convert" status.
- Conversion is the only exception to "copy only", and it applies to subtitles only.

## 6. Track metadata

### 6.1 Editable fields

| Field | Values |
|---|---|
| Name | Free text, empty removes the name |
| Language | ISO 639-2 code, picked from a searchable list (`ger`/`deu` - German) |
| Default | On/off |
| Forced | On/off |
| Hearing impaired | On/off |
| Visual impaired | On/off |
| Commentary | On/off |
| Original | On/off |

Hearing impaired and original are additions to the original list: hearing impaired (SDH) matters for subtitles, original marks the original language of the content. Both exist in FFmpeg and Matroska.

Fields start with the values from the source.

### 6.2 Format support

Not every output format can store every field. Fields the chosen format cannot write are disabled in the editor with a short reason, instead of being dropped silently. Verified with FFmpeg 9 by writing every field and reading it back:

| Format | Name | Language | Flags |
|---|---|---|---|
| Matroska, MKA | ✓ | ✓ | all |
| WebM | ✓ | ✓ | default, forced |
| MP4 | as handler name | ✓ | all except original |
| MOV | as handler name | ✓ (Macintosh codes) | default |
| M4A, M4B | as handler name | ✓ | default |
| MPEG-TS | | ✓ | |
| Ogg, Opus | | ✓ | |
| MP3, FLAC | | | |

MP4 and QuickTime have no track title; players show the handler name instead. Ogg, Opus, MP3 and FLAC have only the container title, which players show as the song or book title.

### 6.3 Language codes

- Version 1 uses ISO 639-2, which every format understands.
- BCP 47 tags (`de-CH`, `pt-BR`) only exist in Matroska and are a possible later addition through mkvpropedit.

## 7. Running a job

- One job at a time. Starting is possible only without "Incompatible" findings.
- FFmpeg writes into a hidden partial file next to the output (`.Movie.ffrux-partial.mkv`). Only after FFmpeg and mkvpropedit succeeded does it replace the output file, so a failed or canceled job never damages an existing file.
- Progress shows percent and the remaining time, based on the total duration.
- Cancel stops FFmpeg and deletes the partial file.
- A log keeps the FFmpeg and mkvpropedit output of the last job.
- A command preview shows the generated FFmpeg and mkvpropedit commands, as in FFApp.

## 8. Technical decisions

### 8.1 FFmpeg plus mkvpropedit

FFmpeg writes every output format. For Matroska outputs (`.mkv`, `.mka`, `.webm`), mkvpropedit runs afterwards:

- It writes track statistics tags (bitrate, duration, frame count), as FFApp already does in `MatroskaPostProcessor`.
- It sets header fields FFmpeg does not write correctly.

mkvpropedit is optional. Without it, Matroska files are still written, without the post-processing.

**Why not mkvmerge:** mkvmerge is the Matroska writer behind MKVToolNix. It writes all Matroska header fields, handles BCP 47, copes better with some tricky sources (VobSub `.idx/.sub`, broken timestamps) and can append files. It only writes Matroska, though. FFRux would need two backends with two compatibility models, and that is not worth it as long as the Matroska output from FFmpeg plus mkvpropedit is good enough. This can be revisited if specific Matroska problems appear; VobSub input (section 1.3) is the first candidate to watch.

### 8.2 Shared code

- `MediaProbeService` (FFApp.Media) is extended with track name (`tags.title`), all dispositions and chapters. FFApp benefits from this as well.
- The copy matrix from section 4.3 is new and based on `codec_name`. Parts of FFApp's `StreamCompatibilityPolicy` (audio copy sets, subtitle conversion) are a starting point. Where it lives (FFApp.Media or FFRux) is decided in the design.

### 8.3 Determining compatibility

The FFmpeg CLI cannot be asked whether a muxer accepts a codec, and it knows nothing about player support (the "Warning" level) or FFRux's own conversion choices. Compatibility therefore rests on three layers:

1. **Rule table:** the matrix from section 4.3, kept as data rather than scattered conditions. It drives the live check in the UI and produces all four status levels with readable messages.
2. **No separate trial mux:** FFmpeg checks the mapping, codecs and container while it writes the output header, so the real remux rejects a bad combination as quickly as a test would. It writes into the hidden partial file, so a rejection leaves an existing output untouched; FFRux then reports that FFmpeg rejected the combination and shows the FFmpeg error. A one-second trial would add nothing but time: sparse subtitle tracks can make FFmpeg read many minutes of the sources before it ends.
3. **Integration tests:** tests run every table entry against the real FFmpeg. Every compatible entry must mux and read back with the same codec; a list of combinations the muxers refuse must keep failing. Test sources are generated with FFmpeg (`lavfi` plus an encoder). Codecs without an FFmpeg encoder (PGS, VobSub, VC-1, Dolby Vision) still need small fixture files. A changed FFmpeg version shows up as failing tests. The first run found two wrong entries in the draft matrix (AV1 in MPEG-TS, MP2 in MP4).

## 9. User interface

FFRux follows FFApp's look and feel: the shared tokens from `Tokens.axaml` (dark and light theme), the card styles and compact control sizes from `Styles.axaml`, and FFApp's three-column layout. MKVToolNix is only a rough model for the workflow. The mock in [mockups/ffrux-main.html](mockups/ffrux-main.html) shows the layout and its main states.

### 9.1 Layout

| Area | Content |
|---|---|
| Left column | Source files, chapters (one radio choice per source with chapters, plus "No chapters"), output (format, title, file) |
| Center | Track table as the main area, the compatibility list below it, a collapsible command preview at the bottom |
| Right column | Properties of the selected track |
| Status bar | Summary of blocking problems, estimated size and the start button; progress and cancel while a job runs |

### 9.2 Track table

- Grouped by type with a header row per group (section 2.3).
- Each source has a numbered, colored marker (1, 2, 3) used in the source list and the track table. The number keeps it usable without color vision.
- Only flags that are set are shown, as small chips.
- The status column pairs a symbol with text and describes what the job will do with the track: "Copy", "Convert to …", a warning, or "Not supported in …". Disabled tracks show "Not included".
- The table is read-only apart from the enabled checkbox and drag and drop; editing happens in the properties pane.

### 9.3 Properties pane

- Fields: name, language, timeshift (audio and subtitles), character encoding (standalone text subtitles only), flags.
- Flags the chosen format cannot store are disabled, with a one-line reason.
- A compatibility box explains the status of the track; a source box shows the file, the `codec_name` and which values were prefilled (for example from the file name).
- **Multiple selection:** with several tracks selected, the pane edits them together. Fields whose values differ show "Multiple values" and are only changed when the user edits them.

### 9.4 Output format

- The format list shows the number of problems next to each format, so the user sees which formats fit before choosing. Without problems it shows the number of warnings, if any.
- The closed field shows only the format name. The problems of the chosen format are shown in the compatibility list and the status bar.

### 9.5 States

- **Empty:** without sources, the center shows a drop area with a button to add files.
- **Ready or blocked:** the status bar shows the blocking problems; the start button is enabled only without blocking problems.
- **Running:** the status bar shows progress, remaining time and a cancel button. Sources, tracks and properties are locked until the job ends.
- **Finished or failed:** the status bar shows the result, with actions to open the output folder or the log.

## After V1

Planned, but deliberately not part of the first version:

- **Cover art:** carry attached pictures over into audio outputs. Each format stores them differently (a flagged video stream in MP4 and MP3, an attachment in Matroska, a metadata block in FLAC and Ogg), so this needs its own compatibility rules.
- **BCP 47 language tags** for Matroska outputs through mkvpropedit (section 6.3).
- **Ogg video** with Theora or VP8, for old `.ogv` sources.
