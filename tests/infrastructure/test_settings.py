from __future__ import annotations

import unittest
from pathlib import Path

from ffapp.core.models import (
    AppSettings,
    AudioCodec,
    AudioMode,
    AudioOptions,
    ExecutablePaths,
    OutputContainer,
    OutputSettings,
    SplitterLayout,
    VideoCodec,
    Vp9QualityOptions,
    VpxEncoderOptions,
)
from ffapp.infrastructure.settings import JsonSettingsService


class JsonSettingsServiceTests(unittest.TestCase):
    def test_load_returns_defaults_when_settings_file_is_missing(self) -> None:
        service = JsonSettingsService(path=Path("/tmp/this-file-should-not-exist-ffapp.json"))

        settings = service.load()

        self.assertIsNone(settings.executable_paths.ffmpeg)
        self.assertIsNone(settings.executable_paths.ffprobe)

    def test_save_round_trips_paths(self) -> None:
        import tempfile

        with tempfile.TemporaryDirectory() as temp_dir:
            settings_path = Path(temp_dir) / "settings.json"
            service = JsonSettingsService(path=settings_path)
            expected = AppSettings(
                executable_paths=ExecutablePaths(
                    ffmpeg=Path("/usr/local/bin/ffmpeg"),
                    ffprobe=Path("/usr/local/bin/ffprobe"),
                ),
                last_used_settings=OutputSettings(
                    container=OutputContainer.WEBM,
                    video_codec=VideoCodec.VP9,
                    video_encoder_options=VpxEncoderOptions(codec=VideoCodec.VP9, cpu_used=3),
                    audio_options=AudioOptions(
                        mode=AudioMode.REENCODE,
                        codec=AudioCodec.OPUS,
                        bitrate=192_000,
                        remix_to_stereo=True,
                    ),
                    target_quality=Vp9QualityOptions(two_pass=True),
                ),
                splitter_layout=SplitterLayout(
                    main_splitter_sizes=(320, 840),
                    right_splitter_sizes=(540, 300),
                    queue_splitter_sizes=(350, 290),
                ),
            )

            service.save(expected)
            actual = service.load()

        self.assertEqual(actual, expected)

    def test_load_defaults_last_used_settings_when_legacy_payload_omits_it(self) -> None:
        import tempfile

        with tempfile.TemporaryDirectory() as temp_dir:
            settings_path = Path(temp_dir) / "settings.json"
            settings_path.write_text(
                """
                {
                  "schema_version": 1,
                  "executable_paths": {
                    "ffmpeg": "/usr/local/bin/ffmpeg",
                    "ffprobe": "/usr/local/bin/ffprobe"
                  }
                }
                """.strip(),
                encoding="utf-8",
            )
            service = JsonSettingsService(path=settings_path)

            actual = service.load()

        self.assertEqual(actual.last_used_settings, OutputSettings())

    def test_load_migrates_legacy_vpx_speed_to_video_encoder_options(self) -> None:
        import tempfile

        with tempfile.TemporaryDirectory() as temp_dir:
            settings_path = Path(temp_dir) / "settings.json"
            settings_path.write_text(
                """
                {
                  "schema_version": 2,
                  "last_used_settings": {
                    "container": "mkv",
                    "video_codec": "vp9",
                    "encoding_strategy": "target_quality",
                    "resolution_preset": "original",
                    "audio_options": {
                      "mode": "copy",
                      "codec": null,
                      "bitrate": null,
                      "remix_to_stereo": false
                    },
                    "stream_handling": {
                      "remove_subtitles": false,
                      "remove_data": false,
                      "remove_audio": false
                    },
                    "target_file_size": {
                      "target_size_mb": 50,
                      "two_pass": false,
                      "muxing_overhead_ratio": 0.015
                    },
                    "target_bitrate": {
                      "video_bitrate": 3000000,
                      "two_pass": false
                    },
                    "target_quality": {
                      "codec": "vp9",
                      "quality": 30,
                      "speed": 4
                    }
                  }
                }
                """.strip(),
                encoding="utf-8",
            )
            service = JsonSettingsService(path=settings_path)

            actual = service.load()

        self.assertEqual(
            actual.last_used_settings.video_encoder_options,
            VpxEncoderOptions(codec=VideoCodec.VP9, cpu_used=4),
        )
        self.assertEqual(actual.last_used_settings.target_quality, Vp9QualityOptions(quality=30))
        self.assertEqual(actual.splitter_layout, SplitterLayout())


if __name__ == "__main__":
    unittest.main()
