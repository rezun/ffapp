from __future__ import annotations

import unittest
from pathlib import Path

from ffapp.core.models import (
    AudioCodec,
    AudioMode,
    AudioOptions,
    AudioStreamInfo,
    DataStreamInfo,
    EncodingStrategy,
    H264QualityOptions,
    H265EncoderOptions,
    H265QualityOptions,
    OutputContainer,
    OutputSettings,
    ResolutionPreset,
    SourceMediaInfo,
    StreamHandlingOptions,
    SubtitleStreamInfo,
    TargetBitrateOptions,
    TargetFileSizeOptions,
    ValidationSeverity,
    VideoCodec,
    VideoStreamInfo,
)
from ffapp.core.validation import DefaultCompatibilityService, DefaultValidationService


def _media_info(
    *,
    width: int = 1920,
    height: int = 1080,
    duration_seconds: float = 120.0,
    audio_codec: str = "aac",
    audio_bit_rate: int | None = 128_000,
    subtitle_codec: str | None = None,
    data_streams: int = 0,
    extra_video_streams: int = 0,
) -> SourceMediaInfo:
    video_streams = [VideoStreamInfo(index=0, codec_name="h264", width=width, height=height)]
    for index in range(extra_video_streams):
        video_streams.append(
            VideoStreamInfo(index=index + 10, codec_name="h264", width=width, height=height)
        )

    subtitle_streams = ()
    if subtitle_codec is not None:
        subtitle_streams = (SubtitleStreamInfo(index=2, codec_name=subtitle_codec),)

    return SourceMediaInfo(
        source_path=Path("/tmp/example.mp4"),
        duration_seconds=duration_seconds,
        video_streams=tuple(video_streams),
        audio_streams=(
            AudioStreamInfo(index=1, codec_name=audio_codec, bit_rate=audio_bit_rate, channels=2),
        ),
        subtitle_streams=subtitle_streams,
        data_streams=tuple(
            DataStreamInfo(index=20 + index, codec_name="bin_data")
            for index in range(data_streams)
        ),
    )


class CompatibilityServiceTests(unittest.TestCase):
    def test_allowed_codecs_follow_container_matrix(self) -> None:
        service = DefaultCompatibilityService()

        self.assertEqual(
            service.allowed_video_codecs(OutputContainer.OGG),
            (VideoCodec.VP8, VideoCodec.VP9),
        )
        self.assertEqual(
            service.allowed_audio_codecs(OutputContainer.MP4),
            (AudioCodec.AAC, AudioCodec.MP3),
        )


class ValidationServiceTests(unittest.TestCase):
    def setUp(self) -> None:
        self.service = DefaultValidationService()

    def test_resolve_output_resolution_swaps_dimensions_for_portrait_sources(self) -> None:
        resolution = self.service.resolve_output_resolution(
            OutputSettings(resolution_preset=ResolutionPreset.P720),
            _media_info(width=1080, height=1920),
        )

        self.assertIsNotNone(resolution)
        self.assertEqual((resolution.width, resolution.height), (720, 1280))

    def test_audio_copy_is_blocked_when_container_is_incompatible(self) -> None:
        result = self.service.validate(
            OutputSettings(
                container=OutputContainer.MP4,
                video_codec=VideoCodec.H264,
                audio_options=AudioOptions(mode=AudioMode.COPY),
            ),
            _media_info(audio_codec="opus"),
        )

        self.assertTrue(result.has_errors)
        self.assertIn("audio_copy_incompatible", {issue.code for issue in result.errors})

    def test_mp4_subtitle_copy_requires_supported_subtitle_codec(self) -> None:
        result = self.service.validate(
            OutputSettings(),
            _media_info(subtitle_codec="subrip"),
        )

        self.assertTrue(result.has_errors)
        self.assertIn("subtitle_copy_incompatible", {issue.code for issue in result.errors})

    def test_target_file_size_requires_audio_reencode_when_copy_bitrate_unknown(self) -> None:
        result = self.service.validate(
            OutputSettings(
                encoding_strategy=EncodingStrategy.TARGET_FILE_SIZE,
                target_file_size=TargetFileSizeOptions(target_size_mb=25),
                audio_options=AudioOptions(mode=AudioMode.COPY),
            ),
            _media_info(audio_bit_rate=None),
        )

        self.assertTrue(result.has_errors)
        self.assertIn("audio_copy_size_unknown", {issue.code for issue in result.errors})

    def test_target_file_size_warns_when_video_budget_is_low_for_resolution(self) -> None:
        result = self.service.validate(
            OutputSettings(
                encoding_strategy=EncodingStrategy.TARGET_FILE_SIZE,
                target_file_size=TargetFileSizeOptions(target_size_mb=20),
                resolution_preset=ResolutionPreset.P1080,
                audio_options=AudioOptions(
                    mode=AudioMode.REENCODE,
                    codec=AudioCodec.AAC,
                    bitrate=128_000,
                ),
            ),
            _media_info(duration_seconds=300.0),
        )

        warning_codes = {issue.code for issue in result.warnings}
        self.assertIn("video_bitrate_low", warning_codes)

    def test_target_bitrate_requires_positive_bitrate(self) -> None:
        result = self.service.validate(
            OutputSettings(
                encoding_strategy=EncodingStrategy.TARGET_BITRATE,
                target_bitrate=TargetBitrateOptions(video_bitrate=0),
            ),
            _media_info(),
        )

        self.assertTrue(result.has_errors)
        self.assertEqual(result.errors[0].severity, ValidationSeverity.ERROR)
        self.assertEqual(result.errors[0].code, "video_bitrate_invalid")

    def test_h264_bitrate_mode_requires_matching_encoder_options(self) -> None:
        result = self.service.validate(
            OutputSettings(
                video_codec=VideoCodec.H264,
                encoding_strategy=EncodingStrategy.TARGET_BITRATE,
                video_encoder_options=H265EncoderOptions(),
                target_bitrate=TargetBitrateOptions(video_bitrate=2_000_000),
            ),
            _media_info(),
        )

        self.assertTrue(result.has_errors)
        self.assertIn("video_encoder_options_required", {issue.code for issue in result.errors})

    def test_quality_options_must_match_selected_video_codec(self) -> None:
        result = self.service.validate(
            OutputSettings(
                video_codec=VideoCodec.H265,
                encoding_strategy=EncodingStrategy.TARGET_QUALITY,
                target_quality=H264QualityOptions(),
            ),
            _media_info(),
        )

        self.assertTrue(result.has_errors)
        self.assertIn("quality_codec_mismatch", {issue.code for issue in result.errors})

    def test_valid_h265_quality_settings_pass_validation(self) -> None:
        result = self.service.validate(
            OutputSettings(
                container=OutputContainer.MKV,
                video_codec=VideoCodec.H265,
                encoding_strategy=EncodingStrategy.TARGET_QUALITY,
                video_encoder_options=H265EncoderOptions(preset="slow", profile="main"),
                target_quality=H265QualityOptions(crf=26, preset="slow", profile="main"),
                audio_options=AudioOptions(
                    mode=AudioMode.REENCODE,
                    codec=AudioCodec.AAC,
                    bitrate=160_000,
                ),
                stream_handling=StreamHandlingOptions(remove_subtitles=True, remove_data=True),
            ),
            _media_info(subtitle_codec="subrip", data_streams=1, extra_video_streams=1),
        )

        self.assertFalse(result.has_errors)
        self.assertIn("multiple_video_streams", {issue.code for issue in result.warnings})


if __name__ == "__main__":
    unittest.main()
