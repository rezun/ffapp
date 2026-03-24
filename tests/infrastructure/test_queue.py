from __future__ import annotations

import unittest
from collections.abc import Callable
from dataclasses import replace
from pathlib import Path

from ffapp.core.models import (
    EncodingStrategy,
    GeneratedCommand,
    H264QualityOptions,
    JobDefinition,
    JobProgress,
    JobResult,
    JobState,
    OutputContainer,
    QueuedJob,
    SourceMediaInfo,
    ValidationResult,
    VideoCodec,
    VideoStreamInfo,
)
from ffapp.infrastructure.queue import InMemoryQueueService


def _command(name: str) -> GeneratedCommand:
    return GeneratedCommand(
        executable_path=Path("/usr/local/bin/ffmpeg"),
        arguments=("-i", "input.mov", name),
        display_string=f"ffmpeg {name}",
    )


def _media_info(name: str) -> SourceMediaInfo:
    return SourceMediaInfo(
        source_path=Path(f"/tmp/{name}.mov"),
        duration_seconds=120.0,
        video_streams=(VideoStreamInfo(index=0, codec_name="h264", width=1920, height=1080),),
    )


def _queued_job(name: str) -> QueuedJob:
    media_info = _media_info(name)
    return QueuedJob(
        job=JobDefinition(
            source_path=media_info.source_path,
            output_path=Path(f"/tmp/{name}.mp4"),
            container=OutputContainer.MP4,
            video_codec=VideoCodec.H264,
            encoding_strategy=EncodingStrategy.TARGET_QUALITY,
            target_quality=H264QualityOptions(),
        ),
        media_info=media_info,
        commands=(_command(name),),
        validation=ValidationResult(),
    )


class FakeJobExecutionService:
    def __init__(
        self,
        result: JobResult,
        *,
        on_execute: Callable[[], None] | None = None,
        log_line: str | None = None,
    ) -> None:
        self.result = result
        self.on_execute = on_execute
        self.log_line = log_line
        self.cancel_called = False

    def execute(
        self,
        job: JobDefinition,
        media_info: SourceMediaInfo,
        *,
        progress_callback: Callable[[JobProgress], None] | None = None,
        log_callback: Callable[[str], None] | None = None,
    ) -> JobResult:
        del job, media_info
        if progress_callback is not None:
            progress_callback(
                JobProgress(
                    state=JobState.RUNNING,
                    pass_index=1,
                    pass_count=1,
                    elapsed_seconds=2.0,
                    percentage=50.0,
                )
            )
        if log_callback is not None and self.log_line is not None:
            log_callback(self.log_line)
        if self.on_execute is not None:
            self.on_execute()
        return self.result

    def cancel(self) -> None:
        self.cancel_called = True


def _job_result(queued_job: QueuedJob, state: JobState, *, log_output: str = "") -> JobResult:
    return JobResult(
        job=queued_job.job,
        state=state,
        commands=queued_job.commands,
        process_results=(),
        elapsed_seconds=4.0,
        log_output=log_output,
    )


class QueueServiceTests(unittest.TestCase):
    def test_remove_drops_non_running_items_and_keeps_running_items(self) -> None:
        first = _queued_job("first")
        second = _queued_job("second")
        queue_service = InMemoryQueueService()
        queue_service.enqueue((first, second))

        remaining_after_first_remove = queue_service.remove(("queue-1",))
        self.assertEqual([item.id for item in remaining_after_first_remove], ["queue-2"])

        queue_service.enqueue((first,))
        running_items = queue_service.items()
        queue_service._items = [
            replace(running_items[0], state=JobState.RUNNING),
            running_items[1],
        ]

        remaining_after_running_remove = queue_service.remove((running_items[0].id,))
        self.assertEqual(len(remaining_after_running_remove), 2)
        self.assertEqual(remaining_after_running_remove[0].state, JobState.RUNNING)

    def test_execute_all_runs_items_sequentially_and_preserves_results(self) -> None:
        first = _queued_job("first")
        second = _queued_job("second")
        services = {
            first.job.source_path: FakeJobExecutionService(
                _job_result(first, JobState.COMPLETED, log_output="first log\n"),
                log_line="frame=1\n",
            ),
            second.job.source_path: FakeJobExecutionService(
                _job_result(second, JobState.COMPLETED, log_output="second log\n"),
                log_line="frame=2\n",
            ),
        }
        queue_service = InMemoryQueueService(
            execution_service_factory=lambda queued_job: services[queued_job.job.source_path]
        )
        queue_service.enqueue((first, second))

        started: list[str] = []
        progressed: list[str] = []
        finished: list[str] = []
        log_lines: list[tuple[str, str]] = []
        items = queue_service.execute_all(
            item_started_callback=lambda item: started.append(item.id),
            item_progress_callback=lambda item: progressed.append(item.id),
            item_finished_callback=lambda item: finished.append(item.id),
            log_callback=lambda item, line: log_lines.append((item.id, line)),
        )

        self.assertEqual(started, ["queue-1", "queue-2"])
        self.assertEqual(progressed, ["queue-1", "queue-2"])
        self.assertEqual(finished, ["queue-1", "queue-2"])
        self.assertEqual(log_lines, [("queue-1", "frame=1\n"), ("queue-2", "frame=2\n")])
        self.assertEqual([item.state for item in items], [JobState.COMPLETED, JobState.COMPLETED])
        self.assertEqual(items[0].result.log_output, "first log\n")
        self.assertEqual(items[1].result.log_output, "second log\n")

    def test_cancel_active_cancels_running_item_and_marks_remaining_items_canceled(self) -> None:
        first = _queued_job("first")
        second = _queued_job("second")
        first_service: FakeJobExecutionService | None = None
        queue_service: InMemoryQueueService

        def factory(queued_job: QueuedJob) -> FakeJobExecutionService:
            nonlocal first_service
            if queued_job.job.source_path == first.job.source_path:
                first_service = FakeJobExecutionService(
                    _job_result(first, JobState.CANCELED),
                    on_execute=queue_service.cancel_active,
                )
                return first_service
            return FakeJobExecutionService(_job_result(second, JobState.COMPLETED))

        queue_service = InMemoryQueueService(execution_service_factory=factory)
        queue_service.enqueue((first, second))

        items = queue_service.execute_all()

        self.assertIsNotNone(first_service)
        self.assertTrue(first_service.cancel_called)
        self.assertEqual([item.state for item in items], [JobState.CANCELED, JobState.CANCELED])
        self.assertIsNone(items[1].result)
        self.assertEqual(items[1].failure_message, "Canceled before execution started.")


if __name__ == "__main__":
    unittest.main()
