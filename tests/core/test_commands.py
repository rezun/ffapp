from __future__ import annotations

import os
import tempfile
import unittest
from pathlib import Path

from ffapp.core.commands import DefaultCommandBuilder, suggest_output_path
from ffapp.core.models import (
    AudioCodec,
    AudioMode,
    AudioOptions,
    AudioStreamInfo,
    DataStreamInfo,
    EncodingStrategy,
    H264QualityOptions,
    H265EncoderOptions,
    JobDefinition,
    OutputContainer,
    ResolutionPreset,
    SourceMediaInfo,
    StreamHandlingOptions,
    SubtitleStreamInfo,
    TargetBitrateOptions,
    TargetFileSizeOptions,
    VideoCodec,
    VideoStreamInfo,
    Vp9QualityOptions,
    VpxEncoderOptions,
)


def _media_info(
    *,
    width: int = 1920,
    height: int = 1080,
    duration_seconds: float = 120.0,
    audio_bit_rate: int | None = 128_000,
    extra_video_streams: int = 0,
    include_subtitle: bool = False,
    include_data: bool = False,
) -> SourceMediaInfo:
    video_streams = [VideoStreamInfo(index=0, codec_name="h264", width=width, height=height)]
    for index in range(extra_video_streams):
        video_streams.append(
            VideoStreamInfo(index=index + 10, codec_name="h264", width=width, height=height)
        )

    subtitle_streams = (
        (SubtitleStreamInfo(index=2, codec_name="mov_text"),) if include_subtitle else ()
    )
    data_streams = (DataStreamInfo(index=3, codec_name="bin_data"),) if include_data else ()

    return SourceMediaInfo(
        source_path=Path("/tmp/source clip.mov"),
        duration_seconds=duration_seconds,
        video_streams=tuple(video_streams),
        audio_streams=(
            AudioStreamInfo(index=1, codec_name="aac", bit_rate=audio_bit_rate, channels=2),
        ),
        subtitle_streams=subtitle_streams,
        data_streams=data_streams,
    )


class CommandBuilderTests(unittest.TestCase):
    def setUp(self) -> None:
        self.builder = DefaultCommandBuilder(Path("/usr/local/bin/ffmpeg"))

    def test_builds_single_pass_quality_command(self) -> None:
        command = self.builder.build(
            JobDefinition(
                source_path=Path("/tmp/source clip.mov"),
                output_path=Path("/tmp/source clip.mp4"),
                container=OutputContainer.MP4,
                video_codec=VideoCodec.H264,
                encoding_strategy=EncodingStrategy.TARGET_QUALITY,
                resolution_preset=ResolutionPreset.P720,
                target_quality=H264QualityOptions(crf=22, preset="slow", profile="high"),
            ),
            _media_info(),
        )[0]

        self.assertEqual(command.executable_path, Path("/usr/local/bin/ffmpeg"))
        self.assertIn("-map", command.arguments)
        self.assertIn("libx264", command.arguments)
        self.assertIn("copy", command.arguments)
        self.assertIn(
            "scale=1280:720:force_original_aspect_ratio=decrease:force_divisible_by=2",
            command.arguments,
        )
        self.assertEqual(command.arguments[-1], "/tmp/source clip.mp4")
        self.assertIn("'/tmp/source clip.mov'", command.display_string)

    def test_builds_portrait_reencode_command_with_stream_removal(self) -> None:
        command = self.builder.build(
            JobDefinition(
                source_path=Path("/tmp/source clip.mov"),
                output_path=Path("/tmp/source clip.ogg"),
                container=OutputContainer.OGG,
                video_codec=VideoCodec.VP9,
                encoding_strategy=EncodingStrategy.TARGET_QUALITY,
                resolution_preset=ResolutionPreset.P720,
                audio_options=AudioOptions(
                    mode=AudioMode.REENCODE,
                    codec=AudioCodec.OPUS,
                    bitrate=160_000,
                    remix_to_stereo=True,
                ),
                stream_handling=StreamHandlingOptions(
                    remove_subtitles=True,
                    remove_data=True,
                ),
                video_encoder_options=VpxEncoderOptions(codec=VideoCodec.VP9, cpu_used=2),
                target_quality=Vp9QualityOptions(quality=30),
            ),
            _media_info(width=1080, height=1920, extra_video_streams=1, include_subtitle=True),
        )[0]

        self.assertIn("-0:10", command.arguments)
        self.assertIn("-0:s?", command.arguments)
        self.assertIn("-0:d?", command.arguments)
        self.assertIn("libvpx-vp9", command.arguments)
        self.assertIn("libopus", command.arguments)
        self.assertIn("160000", command.arguments)
        self.assertIn("2", command.arguments)
        self.assertIn("good", command.arguments)
        self.assertIn("-row-mt", command.arguments)
        self.assertIn(
            "scale=720:1280:force_original_aspect_ratio=decrease:force_divisible_by=2",
            command.arguments,
        )

    def test_vp8_bitrate_mode_uses_shared_vpx_tuning(self) -> None:
        command = self.builder.build(
            JobDefinition(
                source_path=Path("/tmp/source clip.mov"),
                output_path=Path("/tmp/source clip.ogv"),
                container=OutputContainer.OGG,
                video_codec=VideoCodec.VP8,
                encoding_strategy=EncodingStrategy.TARGET_BITRATE,
                video_encoder_options=VpxEncoderOptions(codec=VideoCodec.VP8, cpu_used=5),
                target_bitrate=TargetBitrateOptions(video_bitrate=1_800_000),
            ),
            _media_info(),
        )[0]

        self.assertIn("libvpx", command.arguments)
        self.assertIn("good", command.arguments)
        self.assertIn("5", command.arguments)
        self.assertNotIn("row-mt", command.arguments)

    def test_vp9_quality_two_pass_uses_alt_ref_and_faster_first_pass(self) -> None:
        commands = self.builder.build(
            JobDefinition(
                source_path=Path("/tmp/source clip.mov"),
                output_path=Path("/tmp/source clip.webm"),
                container=OutputContainer.WEBM,
                video_codec=VideoCodec.VP9,
                encoding_strategy=EncodingStrategy.TARGET_QUALITY,
                video_encoder_options=VpxEncoderOptions(codec=VideoCodec.VP9, cpu_used=2),
                target_quality=Vp9QualityOptions(quality=28, two_pass=True),
            ),
            _media_info(),
        )

        self.assertEqual(len(commands), 2)
        first_pass, second_pass = commands
        self.assertIn("-pass", first_pass.arguments)
        self.assertIn("1", first_pass.arguments)
        self.assertIn("-cpu-used", first_pass.arguments)
        self.assertIn("4", first_pass.arguments)
        self.assertNotIn("-auto-alt-ref", first_pass.arguments)
        self.assertIn("-pass", second_pass.arguments)
        self.assertIn("2", second_pass.arguments)
        self.assertIn("-cpu-used", second_pass.arguments)
        self.assertIn("2", second_pass.arguments)
        self.assertIn("-auto-alt-ref", second_pass.arguments)
        self.assertIn("-row-mt", second_pass.arguments)

    def test_target_file_size_uses_computed_video_bitrate(self) -> None:
        command = self.builder.build(
            JobDefinition(
                source_path=Path("/tmp/source clip.mov"),
                output_path=Path("/tmp/source clip.mkv"),
                container=OutputContainer.MKV,
                video_codec=VideoCodec.H265,
                encoding_strategy=EncodingStrategy.TARGET_FILE_SIZE,
                video_encoder_options=H265EncoderOptions(preset="slow", profile="main"),
                audio_options=AudioOptions(
                    mode=AudioMode.REENCODE,
                    codec=AudioCodec.AAC,
                    bitrate=128_000,
                ),
                target_file_size=TargetFileSizeOptions(target_size_mb=50),
            ),
            _media_info(duration_seconds=200.0),
        )[0]

        total_budget_bits = 50 * 1024 * 1024 * 8
        usable_budget_bits = int(total_budget_bits * 0.985)
        audio_budget_bits = 128_000 * 200
        expected_bitrate = int((usable_budget_bits - audio_budget_bits) / 200.0)

        bitrate_index = command.arguments.index("-b:v") + 1
        self.assertIn("slow", command.arguments)
        self.assertIn("main", command.arguments)
        self.assertEqual(command.arguments[bitrate_index], f"{round(expected_bitrate / 1_000)}k")

    def test_target_bitrate_two_pass_returns_both_commands(self) -> None:
        commands = self.builder.build(
            JobDefinition(
                source_path=Path("/tmp/source clip.mov"),
                output_path=Path("/tmp/source clip.mp4"),
                container=OutputContainer.MP4,
                video_codec=VideoCodec.H264,
                encoding_strategy=EncodingStrategy.TARGET_BITRATE,
                target_bitrate=TargetBitrateOptions(video_bitrate=2_500_000, two_pass=True),
            ),
            _media_info(),
        )

        self.assertEqual(len(commands), 2)
        first_pass, second_pass = commands
        self.assertIn("1", first_pass.arguments)
        self.assertIn("2", second_pass.arguments)
        self.assertIn("null", first_pass.arguments)
        self.assertIn("copy", second_pass.arguments)
        self.assertIn("2500k", first_pass.arguments)
        self.assertIn("2500k", second_pass.arguments)
        self.assertIn("medium", first_pass.arguments)
        self.assertIn("high", first_pass.arguments)
        self.assertEqual(first_pass.arguments[-1], "NUL" if os.name == "nt" else "/dev/null")
        self.assertEqual(second_pass.arguments[-1], "/tmp/source clip.mp4")


class OutputPathTests(unittest.TestCase):
    def test_suggest_output_path_appends_numeric_suffix(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            source_path = Path(temp_dir) / "example.mov"
            source_path.touch()
            first_output = Path(temp_dir) / "example.mp4"
            first_output.touch()

            suggested = suggest_output_path(source_path, OutputContainer.MP4)

            self.assertEqual(suggested, Path(temp_dir) / "example-1.mp4")

    def test_suggest_output_path_uses_webm_extension(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            source_path = Path(temp_dir) / "example.mov"
            source_path.touch()

            suggested = suggest_output_path(source_path, OutputContainer.WEBM)

            self.assertEqual(suggested, Path(temp_dir) / "example.webm")


if __name__ == "__main__":
    unittest.main()
