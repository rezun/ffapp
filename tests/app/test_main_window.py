from __future__ import annotations

import unittest
from pathlib import Path

from ffapp.app.main_window import _format_command_preview_text
from ffapp.core.models import GeneratedCommand


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


if __name__ == "__main__":
    unittest.main()
