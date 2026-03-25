from __future__ import annotations

import logging
from collections.abc import Iterable
from dataclasses import dataclass
from pathlib import Path
from typing import cast

from ffapp.app.formatting import (
    _format_command_preview_text,
    _format_job_progress,
    _format_job_result_summary,
    _format_queue_item_details,
    _format_queue_item_label,
    _format_queue_summary,
    _job_state_label,
)
from ffapp.app.qt import (
    QApplication,
    QCheckBox,
    QFileDialog,
    QHBoxLayout,
    QLabel,
    QListWidget,
    QListWidgetItem,
    QMessageBox,
    QPlainTextEdit,
    QProgressBar,
    QPushButton,
    QToolButton,
    QVBoxLayout,
    QWidget,
    Qt,
    Signal,
    _UI_GROUP_MARGINS,
    _UI_INLINE_SPACING,
    _UI_OUTER_MARGINS,
    _UI_SECTION_SPACING,
)
from ffapp.core.models import GeneratedCommand, JobDefinition, JobProgress, JobResult, JobState, QueueItem, ValidationResult

LOGGER = logging.getLogger(__name__)


@dataclass(slots=True, frozen=True)
class PreparedExecution:
    job: JobDefinition
    commands: tuple[GeneratedCommand, ...]
    validation: ValidationResult


class JobDetailsWidget(QWidget):
    start_requested = Signal()
    cancel_requested = Signal()

    def __init__(self) -> None:
        super().__init__()
        self._running = False

        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(_UI_SECTION_SPACING)

        header_row = QHBoxLayout()
        header_row.setSpacing(_UI_INLINE_SPACING)
        self.state_label = QLabel("Idle")
        self.state_label.setStyleSheet("font-weight: 600;")
        header_row.addWidget(self.state_label)
        header_row.addStretch(1)

        self.start_button = QPushButton("Start Job")
        self.start_button.clicked.connect(self.start_requested.emit)
        self.cancel_button = QPushButton("Cancel Job")
        self.cancel_button.clicked.connect(self.cancel_requested.emit)
        header_row.addWidget(self.start_button)
        header_row.addWidget(self.cancel_button)
        layout.addLayout(header_row)

        self.output_path_label = QLabel("No job prepared.")
        self.output_path_label.setWordWrap(True)
        layout.addWidget(self.output_path_label)

        self.progress_summary_label = QLabel(
            "Load a source file and choose valid settings to prepare a job."
        )
        self.progress_summary_label.setWordWrap(True)
        layout.addWidget(self.progress_summary_label)

        self.progress_bar = QProgressBar()
        self.progress_bar.setRange(0, 1000)
        self.progress_bar.setValue(0)
        self.progress_bar.setTextVisible(False)
        self.progress_bar.setFixedHeight(10)
        layout.addWidget(self.progress_bar)

        self.command_toggle = QToolButton()
        self.command_toggle.setText("Job Command")
        self.command_toggle.setCheckable(True)
        self.command_toggle.setChecked(False)
        self.command_toggle.setToolButtonStyle(Qt.ToolButtonStyle.ToolButtonTextBesideIcon)
        self.command_toggle.setArrowType(Qt.ArrowType.RightArrow)
        self.command_toggle.toggled.connect(self._set_command_section_visible)
        layout.addWidget(self.command_toggle)

        self.command_output = QPlainTextEdit()
        self.command_output.setReadOnly(True)
        self.command_output.setMinimumHeight(80)
        self.command_output.setPlaceholderText("Prepared commands will appear here.")
        layout.addWidget(self.command_output)
        self.command_output.setVisible(False)

        self.log_toggle = QToolButton()
        self.log_toggle.setText("ffmpeg Log")
        self.log_toggle.setCheckable(True)
        self.log_toggle.setChecked(False)
        self.log_toggle.setToolButtonStyle(Qt.ToolButtonStyle.ToolButtonTextBesideIcon)
        self.log_toggle.setArrowType(Qt.ArrowType.RightArrow)
        self.log_toggle.toggled.connect(self._set_log_section_visible)
        layout.addWidget(self.log_toggle)

        self.log_output = QPlainTextEdit()
        self.log_output.setReadOnly(True)
        self.log_output.setMinimumHeight(90)
        self.log_output.setPlaceholderText("ffmpeg stderr output will appear here during a job.")
        layout.addWidget(self.log_output)
        self.log_output.setVisible(False)

        self.set_start_enabled(False)
        self.cancel_button.setEnabled(False)

    def _set_command_section_visible(self, visible: bool) -> None:
        self.command_toggle.setArrowType(
            Qt.ArrowType.DownArrow if visible else Qt.ArrowType.RightArrow
        )
        self.command_output.setVisible(visible)

    def _set_log_section_visible(self, visible: bool) -> None:
        self.log_toggle.setArrowType(
            Qt.ArrowType.DownArrow if visible else Qt.ArrowType.RightArrow
        )
        self.log_output.setVisible(visible)

    def set_idle_message(self, message: str) -> None:
        self._running = False
        self.state_label.setText("Idle")
        self.output_path_label.setText("No job prepared.")
        self.progress_summary_label.setText(message)
        self.progress_bar.setRange(0, 1000)
        self.progress_bar.setValue(0)
        self.command_output.clear()
        self.command_output.setPlaceholderText("Prepared commands will appear here.")
        self.command_toggle.setChecked(False)
        self.log_output.clear()
        self.log_toggle.setChecked(False)
        self.start_button.setEnabled(False)
        self.cancel_button.setEnabled(False)

    def set_prepared_job(self, prepared: PreparedExecution) -> None:
        if self._running:
            return

        warning_text = ""
        if prepared.validation.has_warnings:
            warning_count = len(prepared.validation.warnings)
            warning_text = f" {warning_count} warning(s) will not block execution."

        self.state_label.setText("Ready")
        self.output_path_label.setText(str(prepared.job.output_path))
        self.progress_summary_label.setText(
            "Ready to run."
            f"{warning_text} "
            f"Passes: {len(prepared.commands)}."
        )
        self.progress_bar.setRange(0, 1000)
        self.progress_bar.setValue(0)
        self.command_output.setPlainText(
            _format_command_preview_text(prepared.commands, single_command=False)
        )
        self.log_output.clear()
        self.log_output.setPlaceholderText("ffmpeg stderr output will appear here during a job.")
        self.log_toggle.setChecked(False)
        self.cancel_button.setEnabled(False)

    def start_run(self, prepared: PreparedExecution) -> None:
        self._running = True
        self.state_label.setText("Running")
        self.output_path_label.setText(str(prepared.job.output_path))
        self.progress_summary_label.setText(
            f"Running pass 1 of {len(prepared.commands)}."
        )
        self.progress_bar.setRange(0, 0)
        self.command_output.setPlainText(
            _format_command_preview_text(prepared.commands, single_command=False)
        )
        self.log_output.clear()
        self.log_output.setPlaceholderText("ffmpeg stderr output will appear here during a job.")
        self.start_button.setEnabled(False)
        self.cancel_button.setEnabled(True)

    def update_progress(self, progress: JobProgress) -> None:
        if progress.state is JobState.RUNNING:
            self.state_label.setText(f"Running {progress.pass_index}/{progress.pass_count}")
            if progress.percentage is None:
                self.progress_bar.setRange(0, 0)
            else:
                self.progress_bar.setRange(0, 1000)
                self.progress_bar.setValue(round(progress.percentage * 10))
        self.progress_summary_label.setText(_format_job_progress(progress))

    def append_log_line(self, line: str) -> None:
        self.log_output.appendPlainText(line.rstrip("\n"))
        self.log_output.ensureCursorVisible()

    def show_result(self, result: JobResult, *, can_start: bool) -> None:
        self._running = False
        self.state_label.setText(_job_state_label(result.state))
        self.output_path_label.setText(str(result.job.output_path))
        self.progress_summary_label.setText(_format_job_result_summary(result))
        self.progress_bar.setRange(0, 1000)
        self.progress_bar.setValue(1000 if result.state is JobState.COMPLETED else 0)
        self.command_output.setPlainText(
            _format_command_preview_text(result.commands, single_command=False)
        )
        self.log_output.setPlainText(
            result.log_output or "No ffmpeg stderr output was captured for this job."
        )
        self.log_toggle.setChecked(bool(result.log_output))
        self.start_button.setEnabled(can_start)
        self.cancel_button.setEnabled(False)

    def show_failure(self, message: str, *, can_start: bool) -> None:
        self._running = False
        self.state_label.setText("Failed")
        self.progress_summary_label.setText(message)
        self.progress_bar.setRange(0, 1000)
        self.progress_bar.setValue(0)
        self.start_button.setEnabled(can_start)
        self.cancel_button.setEnabled(False)

    def set_start_enabled(self, enabled: bool) -> None:
        if not self._running:
            self.start_button.setEnabled(enabled)

    @property
    def is_running(self) -> bool:
        return self._running


class QueueDetailsWidget(QWidget):
    def __init__(self) -> None:
        super().__init__()

        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(_UI_SECTION_SPACING)
        intro = QLabel(
            "Inspect the selected queued item's prepared command, retained state, and log output."
        )
        intro.setWordWrap(True)
        layout.addWidget(intro)

        self.detail_summary_label = QLabel("Select a queued item to inspect it.")
        self.detail_summary_label.setWordWrap(True)
        layout.addWidget(self.detail_summary_label)

        self.detail_command_output = QPlainTextEdit()
        self.detail_command_output.setReadOnly(True)
        self.detail_command_output.setMinimumHeight(80)
        self.detail_command_output.setPlaceholderText("Selected commands will appear here.")
        layout.addWidget(self.detail_command_output)

        self.detail_log_output = QPlainTextEdit()
        self.detail_log_output.setReadOnly(True)
        self.detail_log_output.setMinimumHeight(100)
        self.detail_log_output.setPlaceholderText("Selected queue item logs will appear here.")
        layout.addWidget(self.detail_log_output)

    def show_item_details(self, item: QueueItem | None, *, live_log: str | None = None) -> None:
        if item is None:
            self._show_empty_details()
            return

        self.detail_summary_label.setText(_format_queue_item_details(item))
        self.detail_command_output.setPlainText(
            _format_command_preview_text(item.queued_job.commands, single_command=False)
        )

        if item.result is not None and item.result.log_output:
            log_text = item.result.log_output
        elif item.failure_message:
            log_text = item.failure_message
        else:
            log_text = live_log or ""

        if log_text:
            self.detail_log_output.setPlainText(log_text)
        else:
            self.detail_log_output.clear()
            self.detail_log_output.setPlaceholderText("Selected queue item logs will appear here.")

    def _show_empty_details(self) -> None:
        self.detail_summary_label.setText("Select a queued item to inspect it.")
        self.detail_command_output.clear()
        self.detail_command_output.setPlaceholderText("Selected commands will appear here.")
        self.detail_log_output.clear()
        self.detail_log_output.setPlaceholderText("Selected queue item logs will appear here.")


class QueueWidget(QWidget):
    add_current_requested = Signal()
    add_files_requested = Signal()
    add_folder_requested = Signal()
    remove_selected_requested = Signal()
    start_requested = Signal()
    cancel_requested = Signal()
    selection_changed = Signal(str)

    def __init__(self) -> None:
        super().__init__()
        self._items_by_id: dict[str, QueueItem] = {}

        layout = QVBoxLayout(self)
        layout.setContentsMargins(*_UI_OUTER_MARGINS)
        layout.setSpacing(_UI_SECTION_SPACING)
        intro = QLabel(
            "Queue multiple files with the current settings snapshot and run them sequentially. "
            "Inspect the selected item below."
        )
        intro.setWordWrap(True)
        layout.addWidget(intro)

        primary_button_row = QHBoxLayout()
        secondary_button_row = QHBoxLayout()
        primary_button_row.setSpacing(_UI_INLINE_SPACING)
        secondary_button_row.setSpacing(_UI_INLINE_SPACING)
        self.add_current_button = QPushButton("Add Current Source")
        self.add_current_button.clicked.connect(self.add_current_requested.emit)
        self.add_files_button = QPushButton("Add Files")
        self.add_files_button.clicked.connect(self.add_files_requested.emit)
        self.add_folder_button = QPushButton("Add Folder")
        self.add_folder_button.clicked.connect(self.add_folder_requested.emit)
        self.remove_selected_button = QPushButton("Remove Selected")
        self.remove_selected_button.clicked.connect(self.remove_selected_requested.emit)
        self.start_button = QPushButton("Start Queue")
        self.start_button.clicked.connect(self.start_requested.emit)
        self.cancel_button = QPushButton("Cancel Queue")
        self.cancel_button.clicked.connect(self.cancel_requested.emit)
        for button in (
            self.add_current_button,
            self.add_files_button,
            self.add_folder_button,
        ):
            primary_button_row.addWidget(button)
        primary_button_row.addStretch(1)
        layout.addLayout(primary_button_row)

        for button in (
            self.remove_selected_button,
            self.start_button,
            self.cancel_button,
        ):
            secondary_button_row.addWidget(button)
        secondary_button_row.addStretch(1)
        layout.addLayout(secondary_button_row)

        self.summary_label = QLabel("Queue is empty.")
        self.summary_label.setWordWrap(True)
        layout.addWidget(self.summary_label)

        self.queue_list = QListWidget()
        self.queue_list.currentItemChanged.connect(self._handle_selection_changed)
        self.queue_list.setMinimumHeight(120)
        layout.addWidget(self.queue_list)

        self.set_running(False)
        self.set_add_current_enabled(False)
        self.set_remove_enabled(False)

    def set_add_current_enabled(self, enabled: bool) -> None:
        self.add_current_button.setEnabled(enabled)

    def set_running(self, running: bool) -> None:
        self.start_button.setEnabled(not running)
        self.cancel_button.setEnabled(running)

    def set_remove_enabled(self, enabled: bool) -> None:
        self.remove_selected_button.setEnabled(enabled)

    def set_items(self, items: Iterable[QueueItem], *, selected_id: str | None = None) -> None:
        item_list = tuple(items)
        self._items_by_id = {item.id: item for item in item_list}
        if selected_id is None and self.queue_list.currentItem() is not None:
            selected_id = cast(
                str | None,
                self.queue_list.currentItem().data(Qt.ItemDataRole.UserRole),
            )

        self.queue_list.clear()
        for item in item_list:
            list_item = QListWidgetItem(_format_queue_item_label(item))
            list_item.setData(Qt.ItemDataRole.UserRole, item.id)
            self.queue_list.addItem(list_item)

        self.summary_label.setText(_format_queue_summary(item_list))
        if selected_id is not None:
            self.select_item(selected_id)
        elif item_list:
            self.queue_list.setCurrentRow(len(item_list) - 1)

    def select_item(self, item_id: str) -> None:
        for row in range(self.queue_list.count()):
            item = self.queue_list.item(row)
            if item.data(Qt.ItemDataRole.UserRole) == item_id:
                self.queue_list.setCurrentRow(row)
                return

    def selected_item_id(self) -> str | None:
        if self.queue_list.currentRow() < 0:
            return None
        return cast(
            str | None,
            self.queue_list.currentItem().data(Qt.ItemDataRole.UserRole),
        )

    def _handle_selection_changed(
        self,
        current: QListWidgetItem | None,
        previous: QListWidgetItem | None,
    ) -> None:
        del previous
        item_id = ""
        if current is not None:
            item_id = cast(str, current.data(Qt.ItemDataRole.UserRole))
        self.selection_changed.emit(item_id)


class CommandPreviewWidget(QWidget):
    def __init__(self) -> None:
        super().__init__()
        self._commands: tuple[GeneratedCommand, ...] = ()
        self._preview_text = ""

        layout = QVBoxLayout(self)
        layout.setContentsMargins(*_UI_GROUP_MARGINS)
        layout.setSpacing(_UI_SECTION_SPACING)
        intro = QLabel(
            "The preview below reflects the effective ffmpeg command for the current source "
            "and settings. It stays read-only, but you can copy or export it."
        )
        intro.setWordWrap(True)
        layout.addWidget(intro)

        button_row = QHBoxLayout()
        button_row.setSpacing(_UI_INLINE_SPACING)
        self.single_command_checkbox = QCheckBox("Single Command")
        self.single_command_checkbox.toggled.connect(self._refresh_preview_text)
        button_row.addWidget(self.single_command_checkbox)
        self.copy_button = QPushButton("Copy Command")
        self.copy_button.clicked.connect(self._copy_command)
        self.export_button = QPushButton("Export Command")
        self.export_button.clicked.connect(self._export_command)
        button_row.addWidget(self.copy_button)
        button_row.addWidget(self.export_button)
        button_row.addStretch(1)
        layout.addLayout(button_row)

        self.preview = QPlainTextEdit()
        self.preview.setReadOnly(True)
        self.preview.setMinimumHeight(110)
        self.preview.setPlaceholderText("No command preview is available yet.")
        layout.addWidget(self.preview)

        self._refresh_actions()

    def show_message(self, message: str) -> None:
        self._commands = ()
        self._preview_text = ""
        self.preview.clear()
        self.preview.setPlaceholderText(message)
        self._refresh_actions()

    def set_commands(self, commands: Iterable[GeneratedCommand]) -> None:
        self._commands = tuple(commands)
        if not self._commands:
            self.show_message("No command preview is available yet.")
            return

        self._refresh_preview_text()
        self._refresh_actions()

    def _copy_command(self) -> None:
        if not self._preview_text:
            return
        QApplication.clipboard().setText(self._preview_text)

    def _export_command(self) -> None:
        if not self._preview_text:
            return
        filename, _ = QFileDialog.getSaveFileName(
            self,
            "Export Command Preview",
            "ffmpeg-command.txt",
            "Text Files (*.txt);;All Files (*)",
        )
        if not filename:
            return

        try:
            Path(filename).write_text(self._preview_text, encoding="utf-8")
        except OSError as exc:
            LOGGER.exception("Failed to export command preview.")
            QMessageBox.critical(self, "Export Failed", str(exc))

    def _refresh_actions(self) -> None:
        has_text = bool(self._preview_text)
        can_merge = len(self._commands) > 1
        self.single_command_checkbox.setEnabled(can_merge)
        self.copy_button.setEnabled(has_text)
        self.export_button.setEnabled(has_text)

    def _refresh_preview_text(self) -> None:
        if not self._commands:
            return

        self._preview_text = _format_command_preview_text(
            self._commands,
            single_command=self.single_command_checkbox.isChecked(),
        )
        self.preview.setPlainText(self._preview_text)


__all__ = [
    "CommandPreviewWidget",
    "JobDetailsWidget",
    "PreparedExecution",
    "QueueDetailsWidget",
    "QueueWidget",
]
