from __future__ import annotations

from collections.abc import Callable, Iterable, Sequence
from dataclasses import replace
from itertools import count
from threading import Lock

from ffapp.core.models import (
    GeneratedCommand,
    JobDefinition,
    JobProgress,
    JobResult,
    JobState,
    QueuedJob,
    QueueItem,
    SourceMediaInfo,
)
from ffapp.core.services import CommandBuilder, JobExecutionService, QueueService
from ffapp.infrastructure.execution import DefaultJobExecutionService, SubprocessProcessRunner


class InMemoryQueueService(QueueService):
    def __init__(
        self,
        execution_service_factory: (
            Callable[[QueuedJob], JobExecutionService] | None
        ) = None,
    ) -> None:
        self._execution_service_factory = (
            execution_service_factory or _default_execution_service_factory
        )
        self._lock = Lock()
        self._items: list[QueueItem] = []
        self._next_id = count(1)
        self._active_item_id: str | None = None
        self._active_execution_service: JobExecutionService | None = None
        self._cancel_requested = False
        self._running = False

    def enqueue(self, jobs: Iterable[QueuedJob]) -> Sequence[QueueItem]:
        created_items: list[QueueItem] = []
        with self._lock:
            for queued_job in jobs:
                item = QueueItem(
                    id=f"queue-{next(self._next_id)}",
                    queued_job=queued_job,
                )
                self._items.append(item)
                created_items.append(item)
        return tuple(created_items)

    def items(self) -> Sequence[QueueItem]:
        with self._lock:
            return tuple(self._items)

    def has_pending(self) -> bool:
        with self._lock:
            return any(item.state is JobState.PENDING for item in self._items)

    def remove(self, item_ids: Iterable[str]) -> Sequence[QueueItem]:
        removable_ids = set(item_ids)
        if not removable_ids:
            return self.items()

        with self._lock:
            self._items = [
                item
                for item in self._items
                if item.id not in removable_ids or item.state is JobState.RUNNING
            ]
            return tuple(self._items)

    def execute_all(
        self,
        *,
        item_started_callback: Callable[[QueueItem], None] | None = None,
        item_progress_callback: Callable[[QueueItem], None] | None = None,
        item_finished_callback: Callable[[QueueItem], None] | None = None,
        log_callback: Callable[[QueueItem, str], None] | None = None,
    ) -> Sequence[QueueItem]:
        with self._lock:
            if self._running:
                raise RuntimeError("Queue execution is already in progress.")
            self._running = True
            self._cancel_requested = False

        try:
            while True:
                item = self._start_next_item()
                if item is None:
                    break

                if item_started_callback is not None:
                    item_started_callback(item)

                execution_service = self._execution_service_factory(item.queued_job)
                with self._lock:
                    self._active_execution_service = execution_service

                try:
                    result = execution_service.execute(
                        item.queued_job.job,
                        item.queued_job.media_info,
                        progress_callback=self._make_progress_callback(
                            item.id,
                            item_progress_callback,
                        ),
                        log_callback=self._make_log_callback(item.id, log_callback),
                    )
                except Exception as exc:
                    failed_item = self._finalize_item(
                        item.id,
                        state=JobState.FAILED,
                        failure_message=str(exc),
                    )
                    if item_finished_callback is not None:
                        item_finished_callback(failed_item)
                    raise
                finally:
                    with self._lock:
                        self._active_execution_service = None
                        self._active_item_id = None

                finished_item = self._finalize_item(item.id, state=result.state, result=result)
                if item_finished_callback is not None:
                    item_finished_callback(finished_item)

                with self._lock:
                    should_stop = self._cancel_requested
                if should_stop:
                    break
        finally:
            with self._lock:
                self._running = False
                self._active_execution_service = None
                self._active_item_id = None

        return self.items()

    def cancel_active(self) -> None:
        with self._lock:
            self._cancel_requested = True
            active_execution_service = self._active_execution_service
            updated_items: list[QueueItem] = []
            for item in self._items:
                if item.state is JobState.PENDING:
                    updated_items.append(
                        replace(
                            item,
                            state=JobState.CANCELED,
                            failure_message="Canceled before execution started.",
                        )
                    )
                else:
                    updated_items.append(item)
            self._items = updated_items

        if active_execution_service is not None:
            active_execution_service.cancel()

    def _start_next_item(self) -> QueueItem | None:
        with self._lock:
            for index, item in enumerate(self._items):
                if item.state is not JobState.PENDING:
                    continue
                running_item = replace(item, state=JobState.RUNNING)
                self._items[index] = running_item
                self._active_item_id = running_item.id
                return running_item
        return None

    def _handle_progress(
        self,
        item_id: str,
        progress: JobProgress,
        callback: Callable[[QueueItem], None] | None,
    ) -> None:
        updated_item = self._update_item(
            item_id,
            transform=lambda item: replace(
                item,
                state=JobState.RUNNING,
                progress=progress,
            ),
        )
        if updated_item is not None and callback is not None:
            callback(updated_item)

    def _handle_log_line(
        self,
        item_id: str,
        line: str,
        callback: Callable[[QueueItem, str], None] | None,
    ) -> None:
        item = self._find_item(item_id)
        if item is not None and callback is not None:
            callback(item, line)

    def _make_progress_callback(
        self,
        item_id: str,
        callback: Callable[[QueueItem], None] | None,
    ) -> Callable[[JobProgress], None] | None:
        if callback is None:
            return None

        def progress_callback(progress: JobProgress) -> None:
            self._handle_progress(item_id, progress, callback)

        return progress_callback

    def _make_log_callback(
        self,
        item_id: str,
        callback: Callable[[QueueItem, str], None] | None,
    ) -> Callable[[str], None] | None:
        if callback is None:
            return None

        def log_callback(line: str) -> None:
            self._handle_log_line(item_id, line, callback)

        return log_callback

    def _finalize_item(
        self,
        item_id: str,
        *,
        state: JobState,
        result: JobResult | None = None,
        failure_message: str | None = None,
    ) -> QueueItem:
        updated_item = self._update_item(
            item_id,
            transform=lambda item: replace(
                item,
                state=state,
                result=result,
                failure_message=failure_message,
                progress=item.progress
                if item.progress is not None
                else _terminal_progress(result),
            ),
        )
        if updated_item is None:
            raise RuntimeError(f"Queue item {item_id} no longer exists.")
        return updated_item

    def _update_item(
        self,
        item_id: str,
        *,
        transform: Callable[[QueueItem], QueueItem],
    ) -> QueueItem | None:
        with self._lock:
            for index, item in enumerate(self._items):
                if item.id != item_id:
                    continue
                updated_item = transform(item)
                self._items[index] = updated_item
                return updated_item
        return None

    def _find_item(self, item_id: str) -> QueueItem | None:
        with self._lock:
            for item in self._items:
                if item.id == item_id:
                    return item
        return None


class _StaticCommandBuilder(CommandBuilder):
    def __init__(self, commands: Sequence[GeneratedCommand]) -> None:
        self._commands = tuple(commands)

    def build(
        self,
        job: JobDefinition,
        media_info: SourceMediaInfo,
    ) -> Sequence[GeneratedCommand]:
        return self._commands


def _default_execution_service_factory(queued_job: QueuedJob) -> JobExecutionService:
    return DefaultJobExecutionService(
        command_builder=_StaticCommandBuilder(queued_job.commands),
        process_runner=SubprocessProcessRunner(),
    )


def _terminal_progress(result: JobResult | None) -> JobProgress | None:
    if result is None:
        return None
    return JobProgress(
        state=result.state,
        pass_index=len(result.process_results) if result.process_results else 1,
        pass_count=len(result.commands),
        elapsed_seconds=result.elapsed_seconds,
        processed_seconds=None,
        percentage=100.0 if result.state is JobState.COMPLETED else None,
    )
