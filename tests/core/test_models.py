from __future__ import annotations

import unittest
from pathlib import Path

from ffapp.core.models import SourceMediaInfo, ValidationIssue, ValidationResult, ValidationSeverity


class SourceMediaInfoTests(unittest.TestCase):
    def test_stream_count_sums_all_known_streams(self) -> None:
        media_info = SourceMediaInfo(
            source_path=Path("/tmp/example.mp4"),
            video_streams=(),
            audio_streams=(),
            subtitle_streams=(),
            data_streams=(),
        )

        self.assertEqual(media_info.stream_count, 0)
        self.assertIsNone(media_info.primary_video_stream)

    def test_validation_result_reports_errors_and_warnings(self) -> None:
        result = ValidationResult(
            issues=(
                ValidationIssue(
                    severity=ValidationSeverity.WARNING,
                    code="warn",
                    message="warning",
                ),
                ValidationIssue(
                    severity=ValidationSeverity.ERROR,
                    code="err",
                    message="error",
                ),
            )
        )

        self.assertTrue(result.has_errors)
        self.assertTrue(result.has_warnings)


if __name__ == "__main__":
    unittest.main()

