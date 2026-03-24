from __future__ import annotations

import unittest
from pathlib import Path

from ffapp.core.models import (
    AudioCodec,
    AudioMode,
    AudioOptions,
    EncodingStrategy,
    OutputContainer,
    OutputSettings,
    PresetDefinition,
    ResolutionPreset,
    VideoCodec,
    Vp9QualityOptions,
    VpxEncoderOptions,
)
from ffapp.infrastructure.presets import JsonPresetService, PresetStorageError


class JsonPresetServiceTests(unittest.TestCase):
    def test_save_load_rename_and_delete_round_trip(self) -> None:
        import tempfile

        with tempfile.TemporaryDirectory() as temp_dir:
            service = JsonPresetService(path=Path(temp_dir) / "presets.json")
            preset = PresetDefinition(
                name="Web Delivery",
                settings=OutputSettings(
                    container=OutputContainer.WEBM,
                    video_codec=VideoCodec.VP9,
                    encoding_strategy=EncodingStrategy.TARGET_QUALITY,
                    resolution_preset=ResolutionPreset.P1080,
                    video_encoder_options=VpxEncoderOptions(codec=VideoCodec.VP9, cpu_used=3),
                    audio_options=AudioOptions(
                        mode=AudioMode.REENCODE,
                        codec=AudioCodec.OPUS,
                        bitrate=160_000,
                    ),
                    target_quality=Vp9QualityOptions(quality=30, two_pass=True),
                ),
            )

            service.save(preset)
            renamed = service.rename("Web Delivery", "Archive")
            loaded = service.load_all()
            service.delete("Archive")
            after_delete = service.load_all()

        self.assertEqual(renamed.name, "Archive")
        self.assertEqual(len(loaded), 1)
        self.assertEqual(loaded[0].name, "Archive")
        self.assertEqual(loaded[0].settings, preset.settings)
        self.assertEqual(after_delete, ())

    def test_load_skips_invalid_or_outdated_preset_entries(self) -> None:
        import tempfile

        with tempfile.TemporaryDirectory() as temp_dir:
            presets_path = Path(temp_dir) / "presets.json"
            presets_path.write_text(
                """
                {
                  "schema_version": 1,
                  "presets": [
                    {
                      "name": "Valid",
                      "schema_version": 1,
                      "settings": {
                        "container": "mp4",
                        "video_codec": "h264",
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
                          "codec": "h264",
                          "crf": 23,
                          "preset": "medium",
                          "profile": "high"
                        }
                      }
                    },
                    {
                      "name": "Outdated",
                      "schema_version": 99,
                      "settings": {}
                    },
                    {
                      "name": "Broken",
                      "schema_version": 1,
                      "settings": {
                        "container": "mp4",
                        "video_codec": "vp9"
                      }
                    }
                  ]
                }
                """.strip(),
                encoding="utf-8",
            )
            service = JsonPresetService(path=presets_path)

            loaded = service.load_all()

        self.assertEqual(tuple(preset.name for preset in loaded), ("Valid",))

    def test_rename_rejects_duplicate_target_name(self) -> None:
        import tempfile

        with tempfile.TemporaryDirectory() as temp_dir:
            service = JsonPresetService(path=Path(temp_dir) / "presets.json")
            service.save(PresetDefinition(name="One"))
            service.save(PresetDefinition(name="Two"))

            with self.assertRaises(PresetStorageError):
                service.rename("One", "Two")


if __name__ == "__main__":
    unittest.main()
