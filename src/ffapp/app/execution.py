from __future__ import annotations

import logging

from ffapp.core.models import JobDefinition, JobProgress, QueueItem, SourceMediaInfo
from ffapp.core.services import JobExecutionService, QueueService

LOGGER = logging.getLogger(__name__)

try:
    from PySide6.QtCore import QObject, Signal, Slot
except ModuleNotFoundError as exc:  # pragma: no cover - imported only when PySide6 is missing
    raise RuntimeError("PySide6 must be installed to import the app layer.") from exc


class JobExecutionWorker(QObject):
    progress = Signal(object)
    log_line = Signal(str)
    finished = Signal(object)
    failed = Signal(str)

    def __init__(
        self,
        execution_service: JobExecutionService,
        job: JobDefinition,
        media_info: SourceMediaInfo,
    ) -> None:
        super().__init__()
        self._execution_service = execution_service
        self._job = job
        self._media_info = media_info

    @Slot()
    def run(self) -> None:
        try:
            result = self._execution_service.execute(
                self._job,
                self._media_info,
                progress_callback=self._emit_progress,
                log_callback=self.log_line.emit,
            )
        except Exception as exc:  # pragma: no cover - defensive UI boundary
            LOGGER.exception("Job execution crashed.")
            self.failed.emit(str(exc))
            return

        self.finished.emit(result)

    def cancel(self) -> None:
        self._execution_service.cancel()

    def _emit_progress(self, progress: JobProgress) -> None:
        self.progress.emit(progress)


class QueueExecutionWorker(QObject):
    item_started = Signal(object)
    item_progress = Signal(object)
    item_finished = Signal(object)
    log_line = Signal(str, str)
    finished = Signal(object)
    failed = Signal(str)

    def __init__(self, queue_service: QueueService) -> None:
        super().__init__()
        self._queue_service = queue_service

    @Slot()
    def run(self) -> None:
        try:
            result = self._queue_service.execute_all(
                item_started_callback=self._emit_item_started,
                item_progress_callback=self._emit_item_progress,
                item_finished_callback=self._emit_item_finished,
                log_callback=self._emit_log_line,
            )
        except Exception as exc:  # pragma: no cover - defensive UI boundary
            LOGGER.exception("Queue execution crashed.")
            self.failed.emit(str(exc))
            return

        self.finished.emit(tuple(result))

    def cancel(self) -> None:
        self._queue_service.cancel_active()

    def _emit_item_started(self, item: QueueItem) -> None:
        self.item_started.emit(item)

    def _emit_item_progress(self, item: QueueItem) -> None:
        self.item_progress.emit(item)

    def _emit_item_finished(self, item: QueueItem) -> None:
        self.item_finished.emit(item)

    def _emit_log_line(self, item: QueueItem, line: str) -> None:
        self.log_line.emit(item.id, line)
