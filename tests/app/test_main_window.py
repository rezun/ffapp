from __future__ import annotations

import os
import unittest
from pathlib import Path

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PySide6.QtWidgets import QApplication

from ffapp.app.main_window import (
    JobDetailsWidget,
    PreparedExecution,
    _format_command_preview_text,
)
from ffapp.core.models import (
    EncodingStrategy,
    GeneratedCommand,
    H264QualityOptions,
    JobDefinition,
    JobResult,
    JobState,
    OutputContainer,
    ValidationResult,
    VideoCodec,
)


def _command(text: str) -> GeneratedCommand:
    return GeneratedCommand(
        executable_path=Path("/usr/local/bin/ffmpeg"),
        arguments=(),
        display_string=text,
    )


class CommandPreviewFormattingTests(unittest.TestCase):
    def test_two_pass_preview_can_render_as_separate_commands(self) -> None:
        text = _format_command_preview_text(
            (_command("ffmpeg pass1"), _command("ffmpeg pass2")),
            single_command=False,
        )

        self.assertEqual(text, "Pass 1\n\nffmpeg pass1\n\nPass 2\n\nffmpeg pass2")

    def test_two_pass_preview_can_render_as_single_command(self) -> None:
        text = _format_command_preview_text(
            (_command("ffmpeg pass1"), _command("ffmpeg pass2")),
            single_command=True,
        )

        self.assertEqual(text, "ffmpeg pass1 && ffmpeg pass2")


def _prepared_execution() -> PreparedExecution:
    return PreparedExecution(
        job=JobDefinition(
            source_path=Path("/tmp/input.mov"),
            output_path=Path("/tmp/output.mp4"),
            container=OutputContainer.MP4,
            video_codec=VideoCodec.H264,
            encoding_strategy=EncodingStrategy.TARGET_QUALITY,
            target_quality=H264QualityOptions(),
        ),
        commands=(_command("ffmpeg pass1"), _command("ffmpeg pass2")),
        validation=ValidationResult(),
    )


class JobDetailsWidgetTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.application = QApplication.instance() or QApplication([])

    def test_prepared_job_renders_command_and_ready_state(self) -> None:
        widget = JobDetailsWidget()
        prepared = _prepared_execution()

        widget.set_prepared_job(prepared)
        widget.set_start_enabled(True)

        self.assertEqual(widget.state_label.text(), "State: Ready")
        self.assertTrue(widget.start_button.isEnabled())
        self.assertFalse(widget.command_toggle.isChecked())
        self.assertFalse(widget.command_output.isVisible())
        self.assertIn("Pass 1", widget.command_output.toPlainText())
        self.assertIn(str(prepared.job.output_path), widget.output_path_label.text())

    def test_show_result_updates_state_progress_and_log_output(self) -> None:
        widget = JobDetailsWidget()
        prepared = _prepared_execution()
        widget.start_run(prepared)

        result = JobResult(
            job=prepared.job,
            state=JobState.COMPLETED,
            commands=prepared.commands,
            process_results=(),
            elapsed_seconds=12.4,
            log_output="encoded successfully\n",
        )

        widget.show_result(result, can_start=True)

        self.assertEqual(widget.state_label.text(), "State: Completed")
        self.assertTrue(widget.start_button.isEnabled())
        self.assertFalse(widget.cancel_button.isEnabled())
        self.assertEqual(widget.progress_bar.value(), 1000)
        self.assertIn("encoded successfully", widget.log_output.toPlainText())


if __name__ == "__main__":
    unittest.main()
