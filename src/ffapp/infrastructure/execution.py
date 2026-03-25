from __future__ import annotations

import logging
import subprocess
import threading
import time
from collections.abc import Callable, Mapping, Sequence
from pathlib import Path
from typing import TextIO

from ffapp.core.models import (
    GeneratedCommand,
    JobDefinition,
    JobProgress,
    JobResult,
    JobState,
    ProcessExecutionResult,
    ProcessProgress,
    SourceMediaInfo,
)
from ffapp.core.services import CommandBuilder, JobExecutionService, ProcessRunner

LOGGER = logging.getLogger(__name__)
_PROGRESS_POLL_INTERVAL_SECONDS = 0.1
_TERMINATE_GRACE_PERIOD_SECONDS = 2.0


class SubprocessProcessRunner(ProcessRunner):
    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._active_process: subprocess.Popen[str] | None = None
        self._cancel_requested = False

    def run(
        self,
        command: GeneratedCommand,
        *,
        expected_duration_seconds: float | None = None,
        expected_frame_count: int | None = None,
        progress_callback: Callable[[ProcessProgress], None] | None = None,
        log_callback: Callable[[str], None] | None = None,
    ) -> ProcessExecutionResult:
        started_at = time.monotonic()
        stderr_chunks: list[str] = []

        process = subprocess.Popen(
            _command_arguments_for_execution(command),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            stdin=subprocess.DEVNULL,
            text=True,
            bufsize=1,
        )

        with self._lock:
            self._active_process = process
            self._cancel_requested = False

        stdout_thread = threading.Thread(
            target=_consume_progress_stream,
            args=(
                process.stdout,
                started_at,
                expected_duration_seconds,
                expected_frame_count,
                progress_callback,
            ),
            daemon=True,
        )
        stderr_thread = threading.Thread(
            target=_consume_log_stream,
            args=(process.stderr, stderr_chunks, log_callback),
            daemon=True,
        )
        stdout_thread.start()
        stderr_thread.start()

        try:
            return_code = self._wait_for_exit(process)
            stdout_thread.join()
            stderr_thread.join()
        finally:
            with self._lock:
                was_canceled = self._cancel_requested
                self._active_process = None
                self._cancel_requested = False

        elapsed_seconds = time.monotonic() - started_at
        state = _resolve_process_state(return_code, was_canceled)
        return ProcessExecutionResult(
            command=command,
            state=state,
            return_code=return_code,
            elapsed_seconds=elapsed_seconds,
            stderr_output="".join(stderr_chunks),
        )

    def cancel(self) -> None:
        with self._lock:
            self._cancel_requested = True
            process = self._active_process
        if process is None or process.poll() is not None:
            return
        try:
            process.terminate()
        except OSError:
            LOGGER.debug("Failed to terminate ffmpeg process during cancellation.", exc_info=True)

    def _wait_for_exit(self, process: subprocess.Popen[str]) -> int:
        cancel_deadline: float | None = None

        while True:
            return_code = process.poll()
            if return_code is not None:
                return return_code

            with self._lock:
                cancel_requested = self._cancel_requested

            if cancel_requested:
                if cancel_deadline is None:
                    cancel_deadline = time.monotonic() + _TERMINATE_GRACE_PERIOD_SECONDS
                elif time.monotonic() >= cancel_deadline:
                    try:
                        process.kill()
                    except OSError:
                        LOGGER.debug(
                            "Failed to kill ffmpeg process after terminate timeout.",
                            exc_info=True,
                        )
                    cancel_deadline = float("inf")

            time.sleep(_PROGRESS_POLL_INTERVAL_SECONDS)


class DefaultJobExecutionService(JobExecutionService):
    def __init__(
        self,
        command_builder: CommandBuilder,
        process_runner: ProcessRunner,
    ) -> None:
        self._command_builder = command_builder
        self._process_runner = process_runner

    def execute(
        self,
        job: JobDefinition,
        media_info: SourceMediaInfo,
        *,
        progress_callback: Callable[[JobProgress], None] | None = None,
        log_callback: Callable[[str], None] | None = None,
    ) -> JobResult:
        commands = tuple(self._command_builder.build(job, media_info))
        started_at = time.monotonic()
        process_results: list[ProcessExecutionResult] = []

        if progress_callback is not None:
            progress_callback(
                JobProgress(
                    state=JobState.RUNNING,
                    pass_index=1,
                    pass_count=len(commands),
                    elapsed_seconds=0.0,
                    percentage=0.0,
                )
            )

        try:
            expected_frame_count = _estimate_total_frame_count(media_info)
            for pass_index, command in enumerate(commands, start=1):
                process_result = self._process_runner.run(
                    command,
                    expected_duration_seconds=media_info.duration_seconds,
                    expected_frame_count=expected_frame_count,
                    progress_callback=_wrap_process_progress_callback(
                        progress_callback,
                        started_at=started_at,
                        pass_index=pass_index,
                        pass_count=len(commands),
                    ),
                    log_callback=_wrap_process_log_callback(
                        log_callback,
                        pass_index=pass_index,
                        pass_count=len(commands),
                    ),
                )
                process_results.append(process_result)

                if process_result.state is not JobState.COMPLETED:
                    break
        finally:
            _cleanup_two_pass_logs(job.output_path)

        elapsed_seconds = time.monotonic() - started_at
        result_state = _resolve_job_state(process_results)
        job_result = JobResult(
            job=job,
            state=result_state,
            commands=commands,
            process_results=tuple(process_results),
            elapsed_seconds=elapsed_seconds,
            log_output=_combine_process_logs(process_results),
        )

        if progress_callback is not None:
            progress_callback(
                JobProgress(
                    state=result_state,
                    pass_index=len(process_results) if process_results else 1,
                    pass_count=len(commands),
                    elapsed_seconds=elapsed_seconds,
                    processed_seconds=media_info.duration_seconds,
                    percentage=100.0 if result_state is JobState.COMPLETED else None,
                )
            )

        return job_result

    def cancel(self) -> None:
        self._process_runner.cancel()


def parse_progress_update(
    packet: Mapping[str, str],
    *,
    elapsed_seconds: float,
    expected_duration_seconds: float | None,
    expected_frame_count: int | None = None,
) -> ProcessProgress:
    processed_seconds = _parse_processed_seconds(packet)
    percentage = _calculate_percentage(
        processed_seconds,
        expected_duration_seconds,
        _parse_frame_count(packet),
        expected_frame_count,
    )
    speed = _parse_speed(packet.get("speed"))
    return ProcessProgress(
        elapsed_seconds=elapsed_seconds,
        processed_seconds=processed_seconds,
        percentage=percentage,
        speed=speed,
    )


def _command_arguments_for_execution(command: GeneratedCommand) -> list[str]:
    return [
        str(command.executable_path),
        "-progress",
        "pipe:1",
        "-nostats",
        *command.arguments,
    ]


def _consume_progress_stream(
    stream: TextIO | None,
    started_at: float,
    expected_duration_seconds: float | None,
    expected_frame_count: int | None,
    progress_callback: Callable[[ProcessProgress], None] | None,
) -> None:
    if stream is None:
        return

    packet: dict[str, str] = {}
    try:
        for raw_line in stream:
            line = raw_line.strip()
            if not line or "=" not in line:
                continue
            key, value = line.split("=", 1)
            packet[key] = value
            if key != "progress":
                continue
            if progress_callback is not None:
                progress_callback(
                    parse_progress_update(
                        packet,
                        elapsed_seconds=time.monotonic() - started_at,
                        expected_duration_seconds=expected_duration_seconds,
                        expected_frame_count=expected_frame_count,
                    )
                )
            packet = {}
    finally:
        stream.close()


def _consume_log_stream(
    stream: TextIO | None,
    stderr_chunks: list[str],
    log_callback: Callable[[str], None] | None,
) -> None:
    if stream is None:
        return

    try:
        for line in stream:
            stderr_chunks.append(line)
            if log_callback is not None:
                log_callback(line)
    finally:
        stream.close()


def _parse_processed_seconds(packet: Mapping[str, str]) -> float | None:
    out_time = packet.get("out_time")
    if out_time:
        parsed_time = _parse_timestamp(out_time)
        if parsed_time is not None:
            return parsed_time

    out_time_us = packet.get("out_time_us")
    if out_time_us:
        try:
            return int(out_time_us) / 1_000_000
        except ValueError:
            return None

    out_time_ms = packet.get("out_time_ms")
    if out_time_ms:
        try:
            return int(out_time_ms) / 1_000_000
        except ValueError:
            return None

    return None


def _parse_frame_count(packet: Mapping[str, str]) -> int | None:
    frame = packet.get("frame")
    if frame is None or frame == "":
        return None
    try:
        parsed_frame = int(frame)
    except ValueError:
        return None
    return parsed_frame if parsed_frame >= 0 else None


def _parse_timestamp(value: str) -> float | None:
    parts = value.split(":")
    if len(parts) != 3:
        return None
    try:
        hours = int(parts[0])
        minutes = int(parts[1])
        seconds = float(parts[2])
    except ValueError:
        return None
    return (hours * 3600) + (minutes * 60) + seconds


def _calculate_percentage(
    processed_seconds: float | None,
    expected_duration_seconds: float | None,
    current_frame: int | None,
    expected_frame_count: int | None,
) -> float | None:
    if (
        processed_seconds is not None
        and expected_duration_seconds is not None
        and expected_duration_seconds > 0
    ):
        return min(100.0, (processed_seconds / expected_duration_seconds) * 100.0)

    if current_frame is None or expected_frame_count is None or expected_frame_count <= 0:
        return None
    return min(100.0, (current_frame / expected_frame_count) * 100.0)


def _parse_speed(value: str | None) -> float | None:
    if value in {None, "", "N/A"}:
        return None
    normalized_value = value[:-1] if isinstance(value, str) and value.endswith("x") else value
    if normalized_value is None:
        return None
    try:
        return float(normalized_value)
    except ValueError:
        return None


def _estimate_total_frame_count(media_info: SourceMediaInfo) -> int | None:
    primary_video = media_info.primary_video_stream
    if (
        primary_video is None
    ):
        return None
    if primary_video.frame_count is not None and primary_video.frame_count > 0:
        return primary_video.frame_count
    if (
        primary_video.frame_rate is None
        or primary_video.frame_rate <= 0
        or media_info.duration_seconds is None
        or media_info.duration_seconds <= 0
    ):
        return None
    return max(1, round(primary_video.frame_rate * media_info.duration_seconds))


def _resolve_process_state(return_code: int, was_canceled: bool) -> JobState:
    if was_canceled:
        return JobState.CANCELED
    if return_code == 0:
        return JobState.COMPLETED
    return JobState.FAILED


def _wrap_process_progress_callback(
    callback: Callable[[JobProgress], None] | None,
    *,
    started_at: float,
    pass_index: int,
    pass_count: int,
) -> Callable[[ProcessProgress], None] | None:
    if callback is None:
        return None

    def wrapped(progress: ProcessProgress) -> None:
        percentage = progress.percentage
        if percentage is not None:
            percentage = ((pass_index - 1) + (percentage / 100.0)) / pass_count * 100.0
        callback(
            JobProgress(
                state=JobState.RUNNING,
                pass_index=pass_index,
                pass_count=pass_count,
                elapsed_seconds=time.monotonic() - started_at,
                processed_seconds=progress.processed_seconds,
                percentage=percentage,
                speed=progress.speed,
            )
        )

    return wrapped


def _wrap_process_log_callback(
    callback: Callable[[str], None] | None,
    *,
    pass_index: int,
    pass_count: int,
) -> Callable[[str], None] | None:
    if callback is None:
        return None
    if pass_count == 1:
        return callback

    def wrapped(line: str) -> None:
        callback(f"[Pass {pass_index}] {line}")

    return wrapped


def _resolve_job_state(process_results: Sequence[ProcessExecutionResult]) -> JobState:
    if not process_results:
        return JobState.FAILED
    last_result = process_results[-1]
    if last_result.state is JobState.CANCELED:
        return JobState.CANCELED
    if all(result.state is JobState.COMPLETED for result in process_results):
        return JobState.COMPLETED
    return JobState.FAILED


def _combine_process_logs(process_results: Sequence[ProcessExecutionResult]) -> str:
    if not process_results:
        return ""

    if len(process_results) == 1:
        return process_results[0].stderr_output

    sections: list[str] = []
    for index, result in enumerate(process_results, start=1):
        sections.append(f"Pass {index}")
        sections.append(result.stderr_output.rstrip())
    return "\n\n".join(section for section in sections if section)


def _cleanup_two_pass_logs(output_path: Path) -> None:
    pass_log_prefix = output_path.with_name(f".{output_path.stem}-ffpass")
    for candidate in pass_log_prefix.parent.glob(f"{pass_log_prefix.name}*"):
        try:
            if candidate.is_file():
                candidate.unlink()
        except OSError:
            LOGGER.debug("Failed to remove ffmpeg pass log file: %s", candidate, exc_info=True)
