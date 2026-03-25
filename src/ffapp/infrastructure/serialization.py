from __future__ import annotations

from collections.abc import Mapping
from typing import Any, cast

from ffapp.core.models import (
    AudioCodec,
    AudioMode,
    AudioOptions,
    EncodingStrategy,
    H264EncoderOptions,
    H264QualityOptions,
    H265EncoderOptions,
    H265QualityOptions,
    OutputContainer,
    OutputSettings,
    PresetDefinition,
    QualityOptions,
    ResolutionPreset,
    StreamHandlingOptions,
    TargetBitrateOptions,
    TargetFileSizeOptions,
    VideoCodec,
    VideoEncoderOptions,
    Vp8QualityOptions,
    Vp9QualityOptions,
    VpxEncoderOptions,
    default_vpx_cpu_used,
)
from ffapp.core.validation import (
    VPX_SPEED_RANGE,
    quality_preset_options,
    quality_profile_options,
)

SETTINGS_SCHEMA_VERSION = 4
PRESET_SCHEMA_VERSION = 1
PRESET_LIBRARY_SCHEMA_VERSION = 1


class SettingsDecodeError(ValueError):
    pass


class PresetDecodeError(ValueError):
    pass


def serialize_output_settings(settings: OutputSettings) -> dict[str, object]:
    return {
        "container": settings.container.value,
        "video_codec": settings.video_codec.value,
        "encoding_strategy": settings.encoding_strategy.value,
        "resolution_preset": settings.resolution_preset.value,
        "video_encoder_options": _serialize_video_encoder_options(settings.video_encoder_options),
        "audio_options": {
            "mode": settings.audio_options.mode.value,
            "codec": _enum_value(settings.audio_options.codec),
            "bitrate": settings.audio_options.bitrate,
            "remix_to_stereo": settings.audio_options.remix_to_stereo,
        },
        "stream_handling": {
            "remove_subtitles": settings.stream_handling.remove_subtitles,
            "remove_data": settings.stream_handling.remove_data,
            "remove_audio": settings.stream_handling.remove_audio,
        },
        "target_file_size": {
            "target_size_mb": settings.target_file_size.target_size_mb,
            "two_pass": settings.target_file_size.two_pass,
            "muxing_overhead_ratio": settings.target_file_size.muxing_overhead_ratio,
        },
        "target_bitrate": {
            "video_bitrate": settings.target_bitrate.video_bitrate,
            "two_pass": settings.target_bitrate.two_pass,
        },
        "target_quality": _serialize_quality_options(settings.target_quality),
    }


def deserialize_output_settings(payload: Mapping[str, Any]) -> OutputSettings:
    container = _read_enum(
        OutputContainer,
        payload.get("container"),
        default=OutputContainer.MP4,
        error_type=SettingsDecodeError,
    )
    video_codec = _read_enum(
        VideoCodec,
        payload.get("video_codec"),
        default=VideoCodec.H264,
        error_type=SettingsDecodeError,
    )
    encoding_strategy = _read_enum(
        EncodingStrategy,
        payload.get("encoding_strategy"),
        default=EncodingStrategy.TARGET_QUALITY,
        error_type=SettingsDecodeError,
    )
    resolution_preset = _read_enum(
        ResolutionPreset,
        payload.get("resolution_preset"),
        default=ResolutionPreset.ORIGINAL,
        error_type=SettingsDecodeError,
    )
    _validate_container_codecs(container, video_codec, error_type=SettingsDecodeError)

    audio_payload = _mapping(payload.get("audio_options"))
    audio_mode = _read_enum(
        AudioMode,
        audio_payload.get("mode"),
        default=AudioMode.COPY,
        error_type=SettingsDecodeError,
    )
    audio_codec = _read_optional_enum(
        AudioCodec,
        audio_payload.get("codec"),
        error_type=SettingsDecodeError,
    )
    if audio_codec is not None:
        _validate_audio_codec(container, audio_codec, error_type=SettingsDecodeError)

    audio_bitrate = _read_optional_int(audio_payload.get("bitrate"))
    remix_to_stereo = bool(audio_payload.get("remix_to_stereo", False))
    if audio_mode is not AudioMode.REENCODE:
        audio_codec = None
        audio_bitrate = None
        remix_to_stereo = False

    stream_payload = _mapping(payload.get("stream_handling"))
    stream_handling = StreamHandlingOptions(
        remove_subtitles=bool(stream_payload.get("remove_subtitles", False)),
        remove_data=bool(stream_payload.get("remove_data", False)),
        remove_audio=bool(stream_payload.get("remove_audio", False)),
    )

    file_size_payload = _mapping(payload.get("target_file_size"))
    target_file_size = TargetFileSizeOptions(
        target_size_mb=_read_int(file_size_payload.get("target_size_mb"), default=50),
        two_pass=bool(file_size_payload.get("two_pass", False)),
        muxing_overhead_ratio=_read_float(
            file_size_payload.get("muxing_overhead_ratio"),
            default=0.015,
        ),
    )

    bitrate_payload = _mapping(payload.get("target_bitrate"))
    target_bitrate = TargetBitrateOptions(
        video_bitrate=_read_int(bitrate_payload.get("video_bitrate"), default=3_000_000),
        two_pass=bool(bitrate_payload.get("two_pass", False)),
    )

    video_encoder_payload = payload.get("video_encoder_options")
    video_encoder_options = (
        _deserialize_video_encoder_options(video_codec, _mapping(video_encoder_payload))
        if video_encoder_payload not in (None, "")
        else None
    )
    target_quality_payload = _mapping(payload.get("target_quality"))
    target_quality = _deserialize_quality_options(
        video_codec,
        target_quality_payload,
    )
    if video_encoder_options is None and video_codec in {VideoCodec.VP8, VideoCodec.VP9}:
        cpu_used = _read_int(
            target_quality_payload.get("speed"),
            default=default_vpx_cpu_used(video_codec),
        )
        if cpu_used not in VPX_SPEED_RANGE:
            raise SettingsDecodeError("Invalid VP8/VP9 CPU used setting.")
        video_encoder_options = VpxEncoderOptions(
            codec=video_codec,
            cpu_used=cpu_used,
        )

    return OutputSettings(
        container=container,
        video_codec=video_codec,
        encoding_strategy=encoding_strategy,
        resolution_preset=resolution_preset,
        video_encoder_options=video_encoder_options,
        audio_options=AudioOptions(
            mode=audio_mode,
            codec=audio_codec,
            bitrate=audio_bitrate,
            remix_to_stereo=remix_to_stereo,
        ),
        stream_handling=stream_handling,
        target_file_size=target_file_size,
        target_bitrate=target_bitrate,
        target_quality=target_quality,
    )


def serialize_preset(preset: PresetDefinition) -> dict[str, object]:
    return {
        "name": preset.name,
        "schema_version": preset.schema_version,
        "settings": serialize_output_settings(preset.settings),
    }


def deserialize_preset(payload: Mapping[str, Any]) -> PresetDefinition:
    name = str(payload.get("name", "")).strip()
    if not name:
        raise PresetDecodeError("Preset name is required.")

    schema_version = _read_int(
        payload.get("schema_version"),
        default=PRESET_SCHEMA_VERSION,
        error_type=PresetDecodeError,
    )
    if schema_version != PRESET_SCHEMA_VERSION:
        raise PresetDecodeError(
            f"Unsupported preset schema version: {schema_version}."
        )

    settings_payload = _mapping(payload.get("settings"))
    try:
        settings = deserialize_output_settings(settings_payload)
    except SettingsDecodeError as exc:
        raise PresetDecodeError(str(exc)) from exc

    return PresetDefinition(
        name=name,
        settings=settings,
        schema_version=schema_version,
    )


def _serialize_video_encoder_options(
    options: VideoEncoderOptions | None,
) -> dict[str, object] | None:
    if options is None:
        return None
    if isinstance(options, VpxEncoderOptions):
        return {
            "codec": options.codec.value,
            "cpu_used": options.cpu_used,
        }
    return {
        "codec": options.codec.value,
        "preset": options.preset,
        "profile": options.profile,
    }


def _serialize_quality_options(options: QualityOptions) -> dict[str, object]:
    if isinstance(options, H264QualityOptions | H265QualityOptions):
        return {
            "codec": options.codec.value,
            "crf": options.crf,
            "preset": options.preset,
            "profile": options.profile,
        }
    return {
        "codec": options.codec.value,
        "quality": options.quality,
        "two_pass": options.two_pass,
    }


def _deserialize_video_encoder_options(
    video_codec: VideoCodec,
    payload: Mapping[str, Any],
) -> VideoEncoderOptions | None:
    if video_codec is VideoCodec.H264:
        preset = str(payload.get("preset", "medium"))
        profile = str(payload.get("profile", "high"))
        if preset not in quality_preset_options(video_codec):
            raise SettingsDecodeError("Invalid H.264 encoder preset.")
        if profile not in quality_profile_options(video_codec):
            raise SettingsDecodeError("Invalid H.264 encoder profile.")
        return H264EncoderOptions(preset=preset, profile=profile)
    if video_codec is VideoCodec.H265:
        preset = str(payload.get("preset", "medium"))
        profile = str(payload.get("profile", "main"))
        if preset not in quality_preset_options(video_codec):
            raise SettingsDecodeError("Invalid H.265 encoder preset.")
        if profile not in quality_profile_options(video_codec):
            raise SettingsDecodeError("Invalid H.265 encoder profile.")
        return H265EncoderOptions(preset=preset, profile=profile)
    cpu_used = _read_int(
        payload.get("cpu_used"),
        default=default_vpx_cpu_used(video_codec),
    )
    if cpu_used not in VPX_SPEED_RANGE:
        raise SettingsDecodeError("Invalid VP8/VP9 CPU used setting.")
    return VpxEncoderOptions(codec=video_codec, cpu_used=cpu_used)


def _deserialize_quality_options(
    video_codec: VideoCodec,
    payload: Mapping[str, Any],
) -> QualityOptions:
    if video_codec is VideoCodec.H264:
        preset = str(payload.get("preset", "medium"))
        profile = str(payload.get("profile", "high"))
        if preset not in quality_preset_options(video_codec):
            raise SettingsDecodeError("Invalid H.264 quality preset.")
        if profile not in quality_profile_options(video_codec):
            raise SettingsDecodeError("Invalid H.264 quality profile.")
        return H264QualityOptions(
            crf=_read_int(payload.get("crf"), default=23),
            preset=preset,
            profile=profile,
        )
    if video_codec is VideoCodec.H265:
        preset = str(payload.get("preset", "medium"))
        profile = str(payload.get("profile", "main"))
        if preset not in quality_preset_options(video_codec):
            raise SettingsDecodeError("Invalid H.265 quality preset.")
        if profile not in quality_profile_options(video_codec):
            raise SettingsDecodeError("Invalid H.265 quality profile.")
        return H265QualityOptions(
            crf=_read_int(payload.get("crf"), default=28),
            preset=preset,
            profile=profile,
        )
    if video_codec is VideoCodec.VP8:
        return Vp8QualityOptions(
            quality=_read_int(payload.get("quality"), default=31),
            two_pass=bool(payload.get("two_pass", False)),
        )
    return Vp9QualityOptions(
        quality=_read_int(payload.get("quality"), default=32),
        two_pass=bool(payload.get("two_pass", False)),
    )


def _validate_container_codecs(
    container: OutputContainer,
    video_codec: VideoCodec,
    *,
    error_type: type[ValueError],
) -> None:
    allowed_video_codecs = {
        OutputContainer.MP4: {VideoCodec.H264, VideoCodec.H265},
        OutputContainer.MKV: {VideoCodec.H264, VideoCodec.H265, VideoCodec.VP8, VideoCodec.VP9},
        OutputContainer.WEBM: {VideoCodec.VP8, VideoCodec.VP9},
        OutputContainer.OGG: {VideoCodec.VP8, VideoCodec.VP9},
    }
    if video_codec not in allowed_video_codecs[container]:
        raise error_type(
            f"{video_codec.value} is not supported in the {container.value} container."
        )


def _validate_audio_codec(
    container: OutputContainer,
    audio_codec: AudioCodec,
    *,
    error_type: type[ValueError],
) -> None:
    allowed_audio_codecs = {
        OutputContainer.MP4: {AudioCodec.AAC, AudioCodec.MP3},
        OutputContainer.MKV: {AudioCodec.MP3, AudioCodec.AAC, AudioCodec.VORBIS, AudioCodec.OPUS},
        OutputContainer.WEBM: {AudioCodec.VORBIS, AudioCodec.OPUS},
        OutputContainer.OGG: {AudioCodec.VORBIS, AudioCodec.OPUS},
    }
    if audio_codec not in allowed_audio_codecs[container]:
        raise error_type(
            f"{audio_codec.value} is not supported in the {container.value} container."
        )


def _mapping(value: object) -> Mapping[str, Any]:
    if value is None:
        return {}
    if not isinstance(value, Mapping):
        raise SettingsDecodeError("Expected a mapping object in serialized settings.")
    return cast(Mapping[str, Any], value)


def _read_enum[T](
    enum_type: type[T],
    value: object,
    *,
    default: T,
    error_type: type[ValueError],
) -> T:
    if value in (None, ""):
        return default
    try:
        return enum_type(value)  # type: ignore[call-arg]
    except ValueError as exc:
        raise error_type(f"Invalid value '{value}' for {enum_type.__name__}.") from exc


def _read_optional_enum[T](
    enum_type: type[T],
    value: object,
    *,
    error_type: type[ValueError],
) -> T | None:
    if value in (None, ""):
        return None
    try:
        return enum_type(value)  # type: ignore[call-arg]
    except ValueError as exc:
        raise error_type(f"Invalid value '{value}' for {enum_type.__name__}.") from exc


def _read_int(
    value: object,
    *,
    default: int,
    error_type: type[ValueError] = SettingsDecodeError,
) -> int:
    if value in (None, ""):
        return default
    try:
        return int(cast(int | float | str, value))
    except (TypeError, ValueError) as exc:
        raise error_type(f"Invalid integer value: {value!r}.") from exc


def _read_optional_int(value: object) -> int | None:
    if value in (None, ""):
        return None
    return _read_int(value, default=0)


def _read_float(value: object, *, default: float) -> float:
    if value in (None, ""):
        return default
    try:
        return float(cast(float | int | str, value))
    except (TypeError, ValueError) as exc:
        raise SettingsDecodeError(f"Invalid floating-point value: {value!r}.") from exc


def _enum_value(value: AudioCodec | None) -> str | None:
    if value is None:
        return None
    return value.value
