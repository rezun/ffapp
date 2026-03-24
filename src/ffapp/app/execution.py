from __future__ import annotations

import logging

from ffapp.core.models import JobDefinition, JobProgress, SourceMediaInfo
from ffapp.core.services import JobExecutionService

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
