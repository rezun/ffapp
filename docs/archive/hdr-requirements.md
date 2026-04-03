# HDR Support - Tier 1 Requirements

## Overview

Add HDR-aware encoding to support two primary workflows:

1. **HDR to HDR** - Re-encode HDR content while preserving color volume and metadata
2. **HDR to SDR** - Tone map HDR content to SDR with user-selectable tone mapping algorithm

This covers HDR10 (PQ/static metadata) and HLG transfer functions with BT.2020 color primaries. Dynamic metadata formats (HDR10+, Dolby Vision) are out of scope for Tier 1.

## Background

HDR video is characterized by three properties that differ from SDR:

- **Bit depth**: 10-bit per channel (vs 8-bit SDR)
- **Transfer function**: PQ (SMPTE 2084) for HDR10, or HLG (ARIB STD-B67) for broadcast
- **Color primaries**: BT.2020 wide gamut (vs BT.709 for SDR)

These properties are carried as stream-level metadata in the container and must be explicitly detected, preserved, or converted during encoding.

## 1. Source HDR Detection

### 1.1 Extend ffprobe parsing

`MediaProbeService.ParseVideoStream` must extract additional fields from ffprobe JSON:

| ffprobe field        | Description                  | Example values                     |
|----------------------|------------------------------|------------------------------------|
| `pix_fmt`            | Pixel format                 | `yuv420p10le`, `yuv420p`           |
| `color_primaries`    | Color primaries              | `bt2020`, `bt709`                  |
| `color_transfer`     | Transfer function (EOTF)     | `smpte2084` (PQ), `arib-std-b67` (HLG), `bt709` |
| `color_space`        | Color matrix coefficients    | `bt2020nc`, `bt709`               |
| `color_range`        | Signal range                 | `tv` (limited), `pc` (full)       |
| `bits_per_raw_sample`| Bit depth                    | `10`, `8`                          |

These are top-level properties on each stream object in ffprobe JSON output (not inside `tags`).

### 1.2 Extend `VideoStreamInfo` model

Add the following optional properties to the `VideoStreamInfo` record:

```
string? PixelFormat
string? ColorPrimaries
string? ColorTransfer
string? ColorSpace
string? ColorRange
int? BitsPerRawSample
```

### 1.3 Derive HDR status

Add a computed property or helper that classifies a video stream's HDR status:

| Classification | Criteria |
|---|---|
| **HDR10** | `color_transfer == "smpte2084"` AND `color_primaries == "bt2020"` |
| **HLG**   | `color_transfer == "arib-std-b67"` AND `color_primaries == "bt2020"` |
| **SDR**   | Everything else (typically `bt709` / `bt709` / `bt709`) |

This classification drives downstream decisions (metadata preservation, filter chain, codec constraints).

### 1.4 HDR10 static metadata (MaxCLL / MaxFALL)

When the source is HDR10, extract mastering display and content light level metadata from `side_data_list`:

- `side_data_type == "Mastering display metadata"` - contains `red_x`, `red_y`, `green_x`, `green_y`, `blue_x`, `blue_y`, `white_point_x`, `white_point_y`, `min_luminance`, `max_luminance`
- `side_data_type == "Content light level metadata"` - contains `max_content`, `max_average`

These are needed to pass through `x265-params` or as encoder metadata for faithful HDR10 re-encoding - and as input parameters for tone mapping.

## 2. HDR to HDR Re-encoding

### 2.1 Supported output codecs

Only H.265 and AV1 support HDR output. H.264, VP8, and VP9 are not valid targets for HDR re-encoding.

| Codec | Encoder | HDR support |
|---|---|---|
| H.265 | libx265 | Profile `main10`, `yuv420p10le` pixel format |
| AV1 | libsvtav1 | `yuv420p10le` pixel format |

### 2.2 Command builder changes

When the source is HDR and the output codec is HDR-capable, the command builder must:

**Pixel format**: Set `-pix_fmt yuv420p10le` for both H.265 and AV1.

**Color metadata flags** (on the output):
```
-color_primaries bt2020
-color_trc smpte2084    (or arib-std-b67 for HLG)
-colorspace bt2020nc
```

**H.265 specific**: Set profile to `main10` and pass HDR parameters via `-x265-params`:
```
-x265-params "hdr-opt=1:repeat-headers=1:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc"
```

If MaxCLL/MaxFALL metadata is available from the source, append:
```
:max-cll={max_content},{max_average}
```

If mastering display metadata is available, append:
```
:master-display=G({gx},{gy})B({bx},{by})R({rx},{ry})WP({wpx},{wpy})L({max_lum},{min_lum})
```

**AV1 specific**: Pass `-svtav1-params "enable-hdr=1"`.

**HLG variant**: Same structure but with `transfer=arib-std-b67` / `-color_trc arib-std-b67`. HLG does not use MaxCLL/MaxFALL or mastering display metadata.

### 2.3 Profile auto-selection

When the source is HDR and the user selects H.265:
- Automatically set the profile to `main10` (override any user selection of `main`)
- Display a note in the UI explaining this is required for HDR

### 2.4 Filter chain considerations

The existing `scale` filter works for HDR-to-HDR as long as no color space conversion occurs. No tone mapping filters are needed. Crop and scale can remain as-is.

However: if we later need `zscale` for HDR-to-SDR (see section 3), consider using `zscale` consistently for all scaling operations to avoid maintaining two scale filter paths. This is a design decision to make during implementation.

## 3. HDR to SDR Tone Mapping

### 3.1 Tone mapping filter chain

When the source is HDR and the output is SDR (H.264, or user explicitly requests SDR), insert the following filter chain **before** any existing crop/scale filters:

```
zscale=t=linear,
format=gbrpf32le,
zscale=p=bt709,
tonemap={algorithm}:{algorithm_parameters},
zscale=t=bt709:m=bt709:r=tv,
format=yuv420p
```

The chain performs these steps:
1. Linearize the PQ/HLG transfer function to linear light
2. Convert to float precision for tone mapping math
3. Convert color primaries from BT.2020 to BT.709
4. Apply the selected tone mapping curve
5. Apply BT.709 transfer function and matrix
6. Convert back to 8-bit 4:2:0

**Note on `npl` (nominal peak luminance):** Many FFmpeg tone mapping recipes include `npl=100` on the linearization step. This value (100 nits = SDR reference white) anchors the linear scale. However, the `tonemap` filter performs its own peak detection from HDR10 metadata (MaxCLL) when available, which is more reliable. During implementation, validate whether explicitly setting `npl` improves results or can be safely omitted. If needed, derive it from source metadata rather than hardcoding.

### 3.2 Tone mapping algorithms

The user must be able to select the tone mapping algorithm. FFmpeg's `tonemap` filter supports:

| Algorithm | Description | Best for |
|---|---|---|
| `hable`    | Filmic curve (Uncharted 2). Good highlight rolloff, natural look | General purpose, film-like content |
| `reinhard` | Classic global operator. Simple, preserves colors well | Evenly-lit content, moderate brightness |
| `mobius`   | Attempt to preserve as much SDR range as possible, smooth rolloff | Content close to SDR range, subtle HDR |
| `bt2390`   | ITU-R BT.2390 reference curve. Standards-compliant | Broadcast content, HLG sources |
| `clip`     | Hard clip - no curve, just clamp values | Testing, already-graded content |

Default: `hable` (widely considered the best general-purpose choice).

### 3.3 Tone mapping parameters

Expose the following optional parameters:

| Parameter | Description | Default |
|---|---|---|
| `desat` | Desaturation strength for highlights (0 = none, higher = more) | `2.0` (FFmpeg default) |
| `peak` | Override detected signal peak (in nits). Useful when metadata is missing or inaccurate | Auto-detected from source metadata |

`peak` should default to auto-detection from source MaxCLL metadata when available. Manual override is useful when metadata is absent or inaccurate (common with screen recordings or camera footage).

### 3.4 Pixel format for SDR output

After tone mapping, the output pixel format should be:
- `yuv420p` for H.264 (as currently hardcoded)
- `yuv420p` for H.265 when tone mapping to SDR (not `yuv420p10le`)
- `yuv420p10le` for AV1 even in SDR mode (see section 3.5)
- Let FFmpeg choose defaults for VP8/VP9

### 3.5 AV1 10-bit default for all sources

AV1 should always encode in 10-bit (`yuv420p10le`), regardless of whether the source is SDR or HDR. This is because:

- SVT-AV1 produces measurably better quality at the same CRF/bitrate in 10-bit mode, even from 8-bit sources. The encoder's transform and prediction math benefits from the extra precision, reducing rounding errors.
- Banding in gradients (skies, dark scenes, fades) is significantly reduced.
- There is no meaningful penalty in file size or encoding speed.

This is the SVT-AV1 developers' own recommendation and the default in tools like HandBrake.

**Implementation**: In `CommandBuilder`, set `-pix_fmt yuv420p10le` for all AV1 encodes unconditionally, in the same location where H.264 currently forces `yuv420p`. This applies to both SDR and HDR sources - for HDR it is already required, and for SDR it is simply the optimal default.

## 4. User Interface

### 4.1 Source info display

Display the detected HDR status in the source media information area:
- Show "HDR10", "HLG", or "SDR" badge/label
- Show bit depth (e.g., "10-bit")
- Show color space info (e.g., "BT.2020 / PQ")

### 4.2 HDR output mode selection

When the source is HDR and the user is re-encoding video, present an HDR handling option:

- **Preserve HDR** (default when output codec supports it) - re-encode maintaining all HDR metadata
- **Convert to SDR** (default when output codec is H.264) - apply tone mapping

This control should be contextual:
- Hidden when source is SDR
- Forced to "Convert to SDR" when codec is H.264 (with explanation)
- User choice when codec is H.265 or AV1

### 4.3 Tone mapping settings

When "Convert to SDR" is selected, show:
- Tone mapping algorithm dropdown (hable, reinhard, mobius, bt2390, clip)
- Desaturation slider or input (0.0 - 10.0, default 2.0)
- Peak override input (optional, nits, blank = auto-detect)

These settings can be in a collapsible "Tone Mapping" section to avoid clutter for the common case (hable with defaults).

## 5. Validation

### 5.1 New validation rules

| Rule | Severity | Message |
|---|---|---|
| HDR source + H.264 + "Preserve HDR" | Error | H.264 does not support HDR. Use H.265 or AV1, or convert to SDR. |
| HDR source + VP8/VP9 + "Preserve HDR" | Error | VP8/VP9 does not support HDR output. Use H.265 or AV1, or convert to SDR. |
| HDR source + H.265 + profile not main10 + "Preserve HDR" | Warning | HDR output requires the main10 profile. It will be set automatically. |
| SDR source + tone mapping enabled | Warning | Source is not HDR. Tone mapping settings will be ignored. |

### 5.2 Codec-HDR compatibility matrix

| Codec | HDR Input | SDR Output | HDR Output |
|---|---|---|---|
| H.264 | Tone map to SDR | Yes | No |
| H.265 | Preserve or tone map | Yes | Yes (main10) |
| VP8 | Tone map to SDR | Yes | No |
| VP9 | Tone map to SDR | Yes | No |
| AV1 | Preserve or tone map | Yes | Yes |

## 6. Model Changes Summary

### New/modified types needed

```
// New enum
enum HdrMode { Preserve, TonemapToSdr }

// New enum
enum ToneMapAlgorithm { Hable, Reinhard, Mobius, Bt2390, Clip }

// New record for tone mapping settings
record ToneMapSettings(
    ToneMapAlgorithm Algorithm = ToneMapAlgorithm.Hable,
    double Desaturation = 2.0,
    int? PeakNits = null)

// New record for HDR static metadata (parsed from source)
record HdrMetadata(
    string? MaxContentLightLevel,    // MaxCLL
    string? MaxAverageLightLevel,    // MaxFALL
    string? MasteringDisplayMetadata)

// Extended VideoStreamInfo - add color properties + derived HDR classification
// Extended JobDefinition - add HdrMode and ToneMapSettings
// Extended OutputSettings - add HdrMode and ToneMapSettings
```

## 7. Scope Boundaries

### In scope (Tier 1)
- HDR10 (PQ + BT.2020) detection, preservation, and tone mapping
- HLG detection, preservation, and tone mapping
- User-selectable tone mapping algorithm
- Static metadata passthrough (MaxCLL, MaxFALL, mastering display)
- H.265 and AV1 as HDR output codecs

### Out of scope (future tiers)
- HDR10+ dynamic metadata
- Dolby Vision
- HLG to HDR10 conversion (or vice versa)
- Custom tone mapping LUTs
- HDR preview/thumbnails
- 12-bit encoding
- Hardware-accelerated tone mapping (e.g., Vulkan/OpenCL tonemap)
