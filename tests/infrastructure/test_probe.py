from __future__ import annotations

import json
import subprocess
import unittest
from pathlib import Path
from unittest.mock import patch

from ffapp.infrastructure.probe import FfprobeMediaProbeService, MediaProbeError, parse_media_info


class ParseMediaInfoTests(unittest.TestCase):
    def test_parse_media_info_extracts_expected_fields(self) -> None:
        payload = {
            "format": {
                "format_name": "mov,mp4,m4a,3gp,3g2,mj2",
                "duration": "12.500000",
                "bit_rate": "456789",
            },
            "streams": [
                {
                    "index": 0,
                    "codec_type": "video",
                    "codec_name": "h264",
                    "bit_rate": "345678",
                    "width": 1920,
                    "height": 1080,
                    "avg_frame_rate": "30000/1001",
                    "nb_frames": "375",
                },
                {
                    "index": 1,
                    "codec_type": "audio",
                    "codec_name": "aac",
                    "bit_rate": "128000",
                    "channels": 2,
                    "tags": {
                        "language": "eng",
                    },
                },
                {
                    "index": 2,
                    "codec_type": "subtitle",
                    "codec_name": "mov_text",
                    "tags": {
                        "language": "deu",
                    },
                },
            ],
        }

        media_info = parse_media_info(Path("/tmp/example.mp4"), payload)

        self.assertEqual(media_info.container_format, "mov,mp4,m4a,3gp,3g2,mj2")
        self.assertEqual(media_info.duration_seconds, 12.5)
        self.assertEqual(media_info.bit_rate, 456789)
        self.assertEqual(len(media_info.video_streams), 1)
        self.assertEqual(len(media_info.audio_streams), 1)
        self.assertEqual(len(media_info.subtitle_streams), 1)
        self.assertEqual(media_info.audio_streams[0].language, "eng")
        self.assertEqual(media_info.subtitle_streams[0].language, "deu")
        self.assertAlmostEqual(media_info.video_streams[0].frame_rate or 0.0, 29.97, places=2)
        self.assertEqual(media_info.video_streams[0].frame_count, 375)

    def test_parse_media_info_ignores_r_frame_rate_for_playback_fps(self) -> None:
        payload = {
            "format": {
                "format_name": "matroska",
                "duration": "10.0",
            },
            "streams": [
                {
                    "index": 0,
                    "codec_type": "video",
                    "codec_name": "h264",
                    "avg_frame_rate": "0/0",
                    "r_frame_rate": "90000/1",
                    "nb_frames": "240",
                },
            ],
        }

        media_info = parse_media_info(Path("/tmp/example.mkv"), payload)

        self.assertIsNone(media_info.video_streams[0].frame_rate)
        self.assertEqual(media_info.video_streams[0].frame_count, 240)


class ProbeServiceTests(unittest.TestCase):
    def test_probe_raises_when_ffprobe_fails(self) -> None:
        service = FfprobeMediaProbeService(Path("/usr/bin/ffprobe"))

        with patch.object(Path, "exists", return_value=True), patch("subprocess.run") as run_mock:
            run_mock.return_value = subprocess.CompletedProcess(
                args=[],
                returncode=1,
                stdout="",
                stderr="failure",
            )

            with self.assertRaises(MediaProbeError):
                service.probe(Path("/tmp/example.mp4"))

    def test_probe_returns_parsed_media_info(self) -> None:
        service = FfprobeMediaProbeService(Path("/usr/bin/ffprobe"))
        payload = {
            "format": {"format_name": "matroska", "duration": "4.0"},
            "streams": [],
        }

        with patch.object(Path, "exists", return_value=True), patch("subprocess.run") as run_mock:
            run_mock.return_value = subprocess.CompletedProcess(
                args=[],
                returncode=0,
                stdout=json.dumps(payload),
                stderr="",
            )

            media_info = service.probe(Path("/tmp/example.mkv"))

        self.assertEqual(media_info.container_format, "matroska")
        self.assertEqual(media_info.duration_seconds, 4.0)


if __name__ == "__main__":
    unittest.main()
