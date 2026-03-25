from __future__ import annotations

import unittest
from collections.abc import Callable
from pathlib import Path

from ffapp.core.models import (
    AudioStreamInfo,
    EncodingStrategy,
    GeneratedCommand,
    H264QualityOptions,
    JobDefinition,
    JobState,
    OutputContainer,
    ProcessExecutionResult,
    ProcessProgress,
    SourceMediaInfo,
    VideoCodec,
    VideoStreamInfo,
)
from ffapp.infrastructure.execution import DefaultJobExecutionService, parse_progress_update


def _command(name: str) -> GeneratedCommand:
    return GeneratedCommand(
        executable_path=Path("/usr/local/bin/ffmpeg"),
        arguments=("-i", "input.mov", name),
        display_string=f"ffmpeg {name}",
    )


def _job_definition() -> JobDefinition:
    return JobDefinition(
        source_path=Path("/tmp/input.mov"),
        output_path=Path("/tmp/output.mp4"),
        container=OutputContainer.MP4,
        video_codec=VideoCodec.H264,
        encoding_strategy=EncodingStrategy.TARGET_QUALITY,
        target_quality=H264QualityOptions(),
    )


def _media_info(
    *,
    frame_rate: float | None = 30.0,
    frame_count: int | None = 3600,
) -> SourceMediaInfo:
    return SourceMediaInfo(
        source_path=Path("/tmp/input.mov"),
        duration_seconds=120.0,
        video_streams=(
            VideoStreamInfo(
                index=0,
                codec_name="h264",
                width=1920,
                height=1080,
                frame_rate=frame_rate,
                frame_count=frame_count,
            ),
        ),
        audio_streams=(AudioStreamInfo(index=1, codec_name="aac", bit_rate=128_000),),
    )


class FakeCommandBuilder:
    def __init__(self, commands: tuple[GeneratedCommand, ...]) -> None:
        self.commands = commands

    def build(
        self,
        job: JobDefinition,
        media_info: SourceMediaInfo,
    ) -> tuple[GeneratedCommand, ...]:
        return self.commands


class FakeProcessRunner:
    def __init__(
        self,
        results: tuple[ProcessExecutionResult, ...],
        progress_updates: tuple[ProcessProgress | None, ...] = (),
        log_lines: tuple[str | None, ...] = (),
    ) -> None:
        self.results = results
        self.progress_updates = progress_updates
        self.log_lines = log_lines
        self.calls: list[GeneratedCommand] = []
        self.expected_frame_counts: list[int | None] = []
        self.cancel_called = False

    def run(
        self,
        command: GeneratedCommand,
        *,
        expected_duration_seconds: float | None = None,
        expected_frame_count: int | None = None,
        progress_callback: Callable[[ProcessProgress], None] | None = None,
        log_callback: Callable[[str], None] | None = None,
    ) -> ProcessExecutionResult:
        self.calls.append(command)
        self.expected_frame_counts.append(expected_frame_count)
        call_index = len(self.calls) - 1
        progress = (
            self.progress_updates[call_index]
            if call_index < len(self.progress_updates)
            else None
        )
        if progress is not None and progress_callback is not None:
            progress_callback(progress)
        log_line = self.log_lines[call_index] if call_index < len(self.log_lines) else None
        if log_line is not None and log_callback is not None:
            log_callback(log_line)
        return self.results[call_index]

    def cancel(self) -> None:
        self.cancel_called = True


class ProgressParsingTests(unittest.TestCase):
    def test_parse_progress_update_extracts_timing_percentage_and_speed(self) -> None:
        progress = parse_progress_update(
            {
                "out_time": "00:01:30.500000",
                "speed": "1.75x",
            },
            elapsed_seconds=12.0,
            expected_duration_seconds=181.0,
        )

        self.assertEqual(progress.elapsed_seconds, 12.0)
        self.assertAlmostEqual(progress.processed_seconds or 0.0, 90.5)
        self.assertAlmostEqual(progress.percentage or 0.0, 50.0)
        self.assertEqual(progress.speed, 1.75)

    def test_parse_progress_update_falls_back_to_frame_percentage(self) -> None:
        progress = parse_progress_update(
            {
                "frame": "900",
                "speed": "2.00x",
            },
            elapsed_seconds=8.0,
            expected_duration_seconds=120.0,
            expected_frame_count=3600,
        )

        self.assertEqual(progress.elapsed_seconds, 8.0)
        self.assertIsNone(progress.processed_seconds)
        self.assertAlmostEqual(progress.percentage or 0.0, 25.0)
        self.assertEqual(progress.speed, 2.0)


class JobExecutionServiceTests(unittest.TestCase):
    def test_execute_maps_multi_pass_progress_and_combines_logs(self) -> None:
        commands = (_command("pass1"), _command("pass2"))
        runner = FakeProcessRunner(
            results=(
                ProcessExecutionResult(
                    command=commands[0],
                    state=JobState.COMPLETED,
                    return_code=0,
                    elapsed_seconds=10.0,
                    stderr_output="first pass log\n",
                ),
                ProcessExecutionResult(
                    command=commands[1],
                    state=JobState.COMPLETED,
                    return_code=0,
                    elapsed_seconds=15.0,
                    stderr_output="second pass log\n",
                ),
            ),
            progress_updates=(
                ProcessProgress(
                    elapsed_seconds=5.0,
                    processed_seconds=48.0,
                    percentage=40.0,
                    speed=1.2,
                ),
                ProcessProgress(
                    elapsed_seconds=9.0,
                    processed_seconds=30.0,
                    percentage=25.0,
                    speed=0.9,
                ),
            ),
            log_lines=("frame=1\n", "frame=2\n"),
        )
        service = DefaultJobExecutionService(FakeCommandBuilder(commands), runner)
        progress_events = []
        log_lines = []

        result = service.execute(
            _job_definition(),
            _media_info(),
            progress_callback=progress_events.append,
            log_callback=log_lines.append,
        )

        self.assertEqual(result.state, JobState.COMPLETED)
        self.assertEqual(runner.calls, [commands[0], commands[1]])
        self.assertEqual(progress_events[0].percentage, 0.0)
        self.assertAlmostEqual(progress_events[1].percentage or 0.0, 20.0)
        self.assertAlmostEqual(progress_events[2].percentage or 0.0, 62.5)
        self.assertEqual(progress_events[-1].state, JobState.COMPLETED)
        self.assertEqual(progress_events[-1].percentage, 100.0)
        self.assertEqual(log_lines, ["[Pass 1] frame=1\n", "[Pass 2] frame=2\n"])
        self.assertEqual(runner.expected_frame_counts, [3600, 3600])
        self.assertIn("Pass 1", result.log_output)
        self.assertIn("first pass log", result.log_output)
        self.assertIn("Pass 2", result.log_output)
        self.assertIn("second pass log", result.log_output)

    def test_execute_stops_after_failed_pass(self) -> None:
        commands = (_command("pass1"), _command("pass2"))
        runner = FakeProcessRunner(
            results=(
                ProcessExecutionResult(
                    command=commands[0],
                    state=JobState.FAILED,
                    return_code=1,
                    elapsed_seconds=3.0,
                    stderr_output="boom\n",
                ),
                ProcessExecutionResult(
                    command=commands[1],
                    state=JobState.COMPLETED,
                    return_code=0,
                    elapsed_seconds=1.0,
                    stderr_output="should not run\n",
                ),
            ),
        )
        service = DefaultJobExecutionService(FakeCommandBuilder(commands), runner)

        result = service.execute(_job_definition(), _media_info())

        self.assertEqual(result.state, JobState.FAILED)
        self.assertEqual(runner.calls, [commands[0]])
        self.assertEqual(len(result.process_results), 1)

    def test_execute_prefers_reported_frame_count_for_progress_estimate(self) -> None:
        command = _command("single-pass")
        runner = FakeProcessRunner(
            results=(
                ProcessExecutionResult(
                    command=command,
                    state=JobState.COMPLETED,
                    return_code=0,
                    elapsed_seconds=3.0,
                    stderr_output="",
                ),
            ),
        )
        service = DefaultJobExecutionService(FakeCommandBuilder((command,)), runner)

        service.execute(_job_definition(), _media_info(frame_rate=None, frame_count=240))

        self.assertEqual(runner.expected_frame_counts, [240])

    def test_execute_skips_frame_fallback_without_frame_count_or_average_fps(self) -> None:
        command = _command("single-pass")
        runner = FakeProcessRunner(
            results=(
                ProcessExecutionResult(
                    command=command,
                    state=JobState.COMPLETED,
                    return_code=0,
                    elapsed_seconds=3.0,
                    stderr_output="",
                ),
            ),
        )
        service = DefaultJobExecutionService(FakeCommandBuilder((command,)), runner)

        service.execute(_job_definition(), _media_info(frame_rate=None, frame_count=None))

        self.assertEqual(runner.expected_frame_counts, [None])

    def test_cancel_forwards_to_process_runner(self) -> None:
        runner = FakeProcessRunner(results=())
        service = DefaultJobExecutionService(FakeCommandBuilder(()), runner)

        service.cancel()

        self.assertTrue(runner.cancel_called)


if __name__ == "__main__":
    unittest.main()
