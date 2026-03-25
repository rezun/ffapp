from __future__ import annotations

import logging
from collections.abc import Iterable
from dataclasses import replace
from pathlib import Path
from typing import cast

from ffapp import get_app_version
from ffapp.app.execution import JobExecutionWorker, QueueExecutionWorker
from ffapp.app.formatting import (
    _audio_codec_label,
    _audio_mode_label,
    _build_job_definition,
    _build_media_summary_text,
    _extract_path_from_mime_data,
    _format_command_preview_text,
    _format_dependency_status,
    _format_output_overview,
    _format_queue_summary,
    _format_validation_result,
    _job_state_label,
    _resolution_preset_label,
    _suggest_queue_output_path,
    _video_codec_label,
)
from ffapp.app.output_settings import (
    DependencySetupWidget,
    MediaSummaryWidget,
    OutputSettingsWidget,
)
from ffapp.app.qt import (
    QAction,
    QApplication,
    QCloseEvent,
    QDragEnterEvent,
    QDropEvent,
    QFileDialog,
    QFrame,
    QGuiApplication,
    QGroupBox,
    QHBoxLayout,
    QInputDialog,
    QLabel,
    QMainWindow,
    QMessageBox,
    QPushButton,
    QRect,
    QScrollArea,
    QSignalBlocker,
    QSizePolicy,
    QSplitter,
    QStackedWidget,
    QTabWidget,
    QThread,
    QVBoxLayout,
    QWidget,
    Qt,
    _INFO_GLYPH,
    _NoWheelComboBox,
    _UI_GROUP_MARGINS,
    _UI_GROUP_STACK_SPACING,
    _UI_INLINE_SPACING,
    _UI_OUTER_MARGINS,
    _UI_SECTION_SPACING,
)
from ffapp.app.workflow_widgets import (
    CommandPreviewWidget,
    JobDetailsWidget,
    PreparedExecution,
    QueueDetailsWidget,
    QueueWidget,
)
from ffapp.core.commands import CommandBuildError, DefaultCommandBuilder, suggest_output_path
from ffapp.core.models import (
    DependencyStatus,
    ExecutablePaths,
    JobProgress,
    JobResult,
    JobState,
    OutputSettings,
    PresetDefinition,
    QueueItem,
    QueuedJob,
    SourceMediaInfo,
    SplitterLayout,
    WindowGeometry,
)
from ffapp.core.validation import DefaultCompatibilityService, DefaultValidationService
from ffapp.infrastructure.dependencies import DependencyManager
from ffapp.infrastructure.execution import DefaultJobExecutionService, SubprocessProcessRunner
from ffapp.infrastructure.presets import JsonPresetService, PresetStorageError
from ffapp.infrastructure.probe import FfprobeMediaProbeService, MediaProbeError
from ffapp.infrastructure.queue import InMemoryQueueService
from ffapp.infrastructure.settings import JsonSettingsService

LOGGER = logging.getLogger(__name__)
_CUSTOM_PRESET_VALUE = "__custom__"
_PRESETS_TOOLTIP = (
    "Save reusable output configurations and reload them without rebuilding the form."
)
_OUTPUT_SETTINGS_TOOLTIP = (
    "Choose a container, codec set, resolution preset, and encoding strategy. "
    "The form only offers valid container/codec combinations and validates against "
    "the currently loaded source."
)
_FOLDER_IMPORT_SUFFIXES: frozenset[str] = frozenset(
    {
        ".aac",
        ".avi",
        ".flac",
        ".m4a",
        ".mkv",
        ".mov",
        ".mp3",
        ".mp4",
        ".mpeg",
        ".mpg",
        ".ogg",
        ".opus",
        ".wav",
        ".webm",
    }
)


class MainWindow(QMainWindow):
    def __init__(
        self,
        settings_service: JsonSettingsService,
        preset_service: JsonPresetService,
        dependency_manager: DependencyManager,
    ) -> None:
        super().__init__()
        self._settings_service = settings_service
        self._preset_service = preset_service
        self._dependency_manager = dependency_manager
        self._validation_service = DefaultValidationService(DefaultCompatibilityService())
        self._settings = self._settings_service.load()
        self._dependency_status = self._dependency_manager.discover(self._settings)
        self._presets: tuple[PresetDefinition, ...] = ()
        self._media_info: SourceMediaInfo | None = None
        self._prepared_execution: PreparedExecution | None = None
        self._execution_thread: QThread | None = None
        self._execution_worker: JobExecutionWorker | None = None
        self._queue_service = InMemoryQueueService()
        self._queue_thread: QThread | None = None
        self._queue_worker: QueueExecutionWorker | None = None
        self._queue_live_logs: dict[str, str] = {}
        self._main_splitter: QSplitter | None = None
        self._right_splitter: QSplitter | None = None
        self._queue_splitter: QSplitter | None = None

        self.setWindowTitle(f"FFApp [{get_app_version()}]")
        self.resize(1180, 760)
        self.setAcceptDrops(True)

        self._stack = QStackedWidget()
        self.setCentralWidget(self._stack)
        self._setup_widget = DependencySetupWidget(self._save_dependency_paths)
        self._main_widget = self._build_main_panel()
        self._stack.addWidget(self._setup_widget)
        self._stack.addWidget(self._main_widget)

        self._restore_window_geometry()
        self._configure_actions()
        self.output_settings_widget.apply_settings(self._settings.last_used_settings)
        self._reload_presets()
        self._refresh_dependency_ui()

    def closeEvent(self, event: QCloseEvent) -> None:
        self._persist_window_geometry()
        self._persist_splitter_layout()
        super().closeEvent(event)

    def dragEnterEvent(self, event: QDragEnterEvent) -> None:
        mime_data = event.mimeData()
        if mime_data.hasUrls() or mime_data.hasText():
            event.acceptProposedAction()
            return
        event.ignore()

    def dropEvent(self, event: QDropEvent) -> None:
        path = _extract_path_from_mime_data(event.mimeData())
        if path is not None:
            self._load_source(path)
            event.acceptProposedAction()
            return
        event.ignore()

    def _configure_actions(self) -> None:
        open_action = QAction("Open Source", self)
        open_action.triggered.connect(self._choose_source_file)
        self.addAction(open_action)

        paste_action = QAction("Paste Source Path", self)
        paste_action.setShortcut("Ctrl+V")
        paste_action.triggered.connect(self._paste_source)
        self.addAction(paste_action)

        dependencies_action = QAction("Configure Dependencies", self)
        dependencies_action.triggered.connect(
            lambda: self._stack.setCurrentWidget(self._setup_widget)
        )
        self.addAction(dependencies_action)

    def _handle_output_settings_changed(self) -> None:
        self._persist_last_used_settings()
        self._sync_preset_selection()
        self._refresh_command_preview()

    def _persist_last_used_settings(self) -> None:
        if not hasattr(self, "output_settings_widget"):
            return
        self._settings = replace(
            self._settings,
            last_used_settings=self.output_settings_widget.current_settings(),
        )
        self._settings_service.save(self._settings)

    def _persist_splitter_layout(self) -> None:
        if (
            self._main_splitter is None
            or self._right_splitter is None
            or self._queue_splitter is None
        ):
            return

        self._settings = replace(
            self._settings,
            splitter_layout=SplitterLayout(
                main_splitter_sizes=tuple(self._main_splitter.sizes()),
                right_splitter_sizes=tuple(self._right_splitter.sizes()),
                queue_splitter_sizes=tuple(self._queue_splitter.sizes()),
            ),
        )
        self._settings_service.save(self._settings)

    def _persist_window_geometry(self) -> None:
        geometry = self.normalGeometry() if self.isMaximized() else self.geometry()
        if not geometry.isValid():
            return

        self._settings = replace(
            self._settings,
            window_geometry=WindowGeometry(
                x=geometry.x(),
                y=geometry.y(),
                width=geometry.width(),
                height=geometry.height(),
                is_maximized=self.isMaximized(),
            ),
        )
        self._settings_service.save(self._settings)

    def _restore_splitter_layout(self) -> None:
        splitter_layout = self._settings.splitter_layout
        if self._main_splitter is not None and len(splitter_layout.main_splitter_sizes) >= 2:
            self._main_splitter.setSizes(list(splitter_layout.main_splitter_sizes))
        if self._right_splitter is not None and len(splitter_layout.right_splitter_sizes) >= 2:
            self._right_splitter.setSizes(list(splitter_layout.right_splitter_sizes))
        if self._queue_splitter is not None and len(splitter_layout.queue_splitter_sizes) >= 2:
            self._queue_splitter.setSizes(list(splitter_layout.queue_splitter_sizes))

    def _restore_window_geometry(self) -> None:
        geometry = self._settings.window_geometry
        if geometry.width is not None and geometry.height is not None:
            self.resize(geometry.width, geometry.height)

        if None not in (geometry.x, geometry.y, geometry.width, geometry.height):
            saved_rect = QRect(geometry.x, geometry.y, geometry.width, geometry.height)
            if any(
                screen.availableGeometry().intersects(saved_rect)
                for screen in QGuiApplication.screens()
            ):
                self.move(geometry.x, geometry.y)

        if geometry.is_maximized:
            self.setWindowState(self.windowState() | Qt.WindowState.WindowMaximized)

    def _reload_presets(self, *, selected_name: str | None = None) -> None:
        self._presets = tuple(self._preset_service.load_all())
        with QSignalBlocker(self.preset_combo):
            self.preset_combo.clear()
            self.preset_combo.addItem("Current Settings", _CUSTOM_PRESET_VALUE)
            for preset in self._presets:
                self.preset_combo.addItem(preset.name, preset.name)

        if selected_name is not None:
            self._set_combo_value(self.preset_combo, selected_name)
        else:
            self._sync_preset_selection()
        self._refresh_preset_controls()

    def _sync_preset_selection(self) -> None:
        current_settings = self.output_settings_widget.current_settings()
        selected_value = _CUSTOM_PRESET_VALUE
        for preset in self._presets:
            if preset.settings == current_settings:
                selected_value = preset.name
                break
        self._set_combo_value(self.preset_combo, selected_value)
        self._refresh_preset_controls()

    def _selected_preset_name(self) -> str | None:
        selected = self.preset_combo.currentData()
        if selected in (None, _CUSTOM_PRESET_VALUE):
            return None
        return cast(str, selected)

    def _selected_preset(self) -> PresetDefinition | None:
        selected_name = self._selected_preset_name()
        if selected_name is None:
            return None
        for preset in self._presets:
            if preset.name == selected_name:
                return preset
        return None

    def _refresh_preset_controls(self) -> None:
        has_selection = self._selected_preset_name() is not None
        self.rename_preset_button.setEnabled(has_selection)
        self.delete_preset_button.setEnabled(has_selection)

    def _handle_preset_selection_changed(self) -> None:
        self._refresh_preset_controls()
        preset = self._selected_preset()
        if preset is None:
            return
        if preset.settings == self.output_settings_widget.current_settings():
            return
        self._apply_preset(preset)

    def _apply_preset(self, preset: PresetDefinition) -> None:
        self.output_settings_widget.apply_settings(preset.settings)

        if self._media_info is not None:
            validation = self._validation_service.validate(preset.settings, self._media_info)
            if validation.has_errors:
                issues = "\n".join(f"- {issue.message}" for issue in validation.errors)
                QMessageBox.warning(
                    self,
                    "Preset Loaded With Validation Errors",
                    f"'{preset.name}' was loaded, but the current source does not satisfy it.\n\n"
                    f"{issues}",
                )

        self._persist_last_used_settings()
        self._refresh_command_preview()
        self._refresh_preset_controls()

    def _save_preset_as(self) -> None:
        suggested_name = self._selected_preset_name() or ""
        name, accepted = QInputDialog.getText(
            self,
            "Save Preset",
            "Preset name",
            text=suggested_name,
        )
        if not accepted:
            return

        try:
            self._preset_service.save(
                PresetDefinition(
                    name=name,
                    settings=self.output_settings_widget.current_settings(),
                )
            )
        except PresetStorageError as exc:
            QMessageBox.warning(self, "Preset Not Saved", str(exc))
            return

        self._reload_presets(selected_name=name.strip())

    def _rename_selected_preset(self) -> None:
        preset = self._selected_preset()
        if preset is None:
            return

        new_name, accepted = QInputDialog.getText(
            self,
            "Rename Preset",
            "Preset name",
            text=preset.name,
        )
        if not accepted:
            return

        try:
            renamed = self._preset_service.rename(preset.name, new_name)
        except PresetStorageError as exc:
            QMessageBox.warning(self, "Preset Not Renamed", str(exc))
            return

        self._reload_presets(selected_name=renamed.name)

    def _delete_selected_preset(self) -> None:
        preset = self._selected_preset()
        if preset is None:
            return

        answer = QMessageBox.question(
            self,
            "Delete Preset",
            f"Delete preset '{preset.name}'?",
        )
        if answer is not QMessageBox.StandardButton.Yes:
            return

        try:
            self._preset_service.delete(preset.name)
        except PresetStorageError as exc:
            QMessageBox.warning(self, "Preset Not Deleted", str(exc))
            return

        self._reload_presets()

    def _set_combo_value(self, combo_box: _NoWheelComboBox, value: object) -> None:
        index = combo_box.findData(value)
        if index >= 0:
            with QSignalBlocker(combo_box):
                combo_box.setCurrentIndex(index)

    def _build_main_panel(self) -> QWidget:
        container = QWidget()
        outer_layout = QVBoxLayout(container)
        outer_layout.setContentsMargins(*_UI_OUTER_MARGINS)

        self._main_splitter = QSplitter(Qt.Orientation.Horizontal)
        self._main_splitter.setChildrenCollapsible(False)
        self._main_splitter.setHandleWidth(10)
        self._main_splitter.splitterMoved.connect(
            lambda _pos, _index: self._persist_splitter_layout()
        )
        outer_layout.addWidget(self._main_splitter)

        left_panel = QWidget()
        left_layout = QVBoxLayout(left_panel)
        left_layout.setContentsMargins(0, 0, 0, 0)

        self.workflow_tabs = QTabWidget()
        self.workflow_tabs.setStyleSheet("QTabWidget::pane { border: 0; }")
        self._source_workspace = self._build_source_workspace()
        self._queue_workspace = self._build_queue_workspace()
        self.workflow_tabs.addTab(self._source_workspace, "Source")
        self.workflow_tabs.addTab(self._queue_workspace, "Queue")
        left_layout.addWidget(self.workflow_tabs)

        right_panel = QWidget()
        right_layout = QVBoxLayout(right_panel)
        right_layout.setContentsMargins(0, 0, 0, 0)
        self._right_splitter = QSplitter(Qt.Orientation.Vertical)
        self._right_splitter.setChildrenCollapsible(False)
        self._right_splitter.setHandleWidth(10)
        self._right_splitter.splitterMoved.connect(
            lambda _pos, _index: self._persist_splitter_layout()
        )
        right_layout.addWidget(self._right_splitter)

        top_panel = QWidget()
        top_layout = QVBoxLayout(top_panel)
        top_layout.setContentsMargins(0, 0, 0, 0)
        top_layout.setSpacing(0)

        presets_box = QGroupBox(f"Presets{_INFO_GLYPH}")
        presets_box.setToolTip(_PRESETS_TOOLTIP)
        presets_layout = QVBoxLayout(presets_box)
        presets_layout.setContentsMargins(*_UI_GROUP_MARGINS)
        presets_layout.setSpacing(_UI_SECTION_SPACING)

        preset_row = QHBoxLayout()
        preset_row.setContentsMargins(0, 0, 0, 0)
        preset_row.setSpacing(_UI_INLINE_SPACING)
        self.preset_combo = _NoWheelComboBox()
        self.preset_combo.currentIndexChanged.connect(self._handle_preset_selection_changed)
        preset_row.addWidget(self.preset_combo, 2)

        self.save_preset_button = QPushButton("Save As")
        self.save_preset_button.clicked.connect(self._save_preset_as)
        preset_row.addWidget(self.save_preset_button)

        self.rename_preset_button = QPushButton("Rename")
        self.rename_preset_button.clicked.connect(self._rename_selected_preset)
        preset_row.addWidget(self.rename_preset_button)

        self.delete_preset_button = QPushButton("Delete")
        self.delete_preset_button.clicked.connect(self._delete_selected_preset)
        preset_row.addWidget(self.delete_preset_button)
        presets_layout.addLayout(preset_row)
        top_layout.addWidget(presets_box)
        top_layout.addSpacing(_UI_GROUP_STACK_SPACING)

        summary_box = QGroupBox("Current Output Summary")
        summary_layout = QVBoxLayout(summary_box)
        summary_layout.setContentsMargins(*_UI_GROUP_MARGINS)
        summary_layout.setSpacing(_UI_SECTION_SPACING)
        self.output_summary_label = QLabel(
            "Load a source and adjust output settings to see the current encode summary."
        )
        self.output_summary_label.setWordWrap(True)
        summary_layout.addWidget(self.output_summary_label)
        top_layout.addWidget(summary_box)
        top_layout.addSpacing(_UI_GROUP_STACK_SPACING)

        output_box = QGroupBox(f"Output Settings{_INFO_GLYPH}")
        output_box.setToolTip(_OUTPUT_SETTINGS_TOOLTIP)
        output_layout = QVBoxLayout(output_box)
        output_layout.setContentsMargins(*_UI_GROUP_MARGINS)
        output_layout.setSpacing(_UI_SECTION_SPACING)
        self.output_settings_widget = OutputSettingsWidget()
        output_layout.addWidget(self.output_settings_widget)
        top_layout.addWidget(output_box)
        top_layout.addStretch(1)

        top_scroll = QScrollArea()
        top_scroll.setFrameShape(QFrame.Shape.NoFrame)
        top_scroll.setWidgetResizable(True)
        top_scroll.setWidget(top_panel)

        self.inspector_tabs = QTabWidget()
        self.inspector_tabs.setStyleSheet("QTabWidget::pane { border: 0; }")
        self.command_preview_widget = CommandPreviewWidget()
        self.execution_inspector_tab = self._build_execution_inspector()
        self.inspector_tabs.addTab(self.command_preview_widget, "Preview")
        self.inspector_tabs.addTab(self.execution_inspector_tab, "Execution")

        self._right_splitter.addWidget(top_scroll)
        self._right_splitter.addWidget(self.inspector_tabs)
        self._right_splitter.setStretchFactor(0, 3)
        self._right_splitter.setStretchFactor(1, 2)
        self._right_splitter.setSizes([560, 280])

        self._main_splitter.addWidget(left_panel)
        self._main_splitter.addWidget(right_panel)
        self._main_splitter.setStretchFactor(0, 2)
        self._main_splitter.setStretchFactor(1, 5)
        self._main_splitter.setSizes([300, 860])

        self.output_settings_widget.settings_changed.connect(self._handle_output_settings_changed)
        self._refresh_preset_controls()
        self._refresh_queue_ui()
        self._restore_splitter_layout()
        return container

    def _build_source_workspace(self) -> QWidget:
        panel = QWidget()
        layout = QVBoxLayout(panel)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(0)

        source_box = QGroupBox("Source Selection")
        source_layout = QVBoxLayout(source_box)
        source_layout.setContentsMargins(*_UI_GROUP_MARGINS)
        source_layout.setSpacing(_UI_SECTION_SPACING)
        button_row = QHBoxLayout()
        button_row.setSpacing(_UI_INLINE_SPACING)
        open_button = QPushButton("Choose File")
        open_button.clicked.connect(self._choose_source_file)
        button_row.addWidget(open_button)
        paste_button = QPushButton("Paste Path")
        paste_button.clicked.connect(self._paste_source)
        button_row.addWidget(paste_button)
        configure_button = QPushButton("Dependencies")
        configure_button.clicked.connect(lambda: self._stack.setCurrentWidget(self._setup_widget))
        button_row.addWidget(configure_button)
        button_row.addStretch(1)
        source_layout.addLayout(button_row)

        self.source_path_label = QLabel(
            "Drop a file here, choose one, or paste a file path from the clipboard."
        )
        self.source_path_label.setWordWrap(True)
        source_layout.addWidget(self.source_path_label)
        layout.addWidget(source_box)
        layout.addSpacing(_UI_GROUP_STACK_SPACING)

        media_box = QGroupBox("Media Info Summary")
        media_layout = QVBoxLayout(media_box)
        media_layout.setContentsMargins(*_UI_GROUP_MARGINS)
        media_layout.setSpacing(_UI_SECTION_SPACING)
        self.media_summary_widget = MediaSummaryWidget()
        media_layout.addWidget(self.media_summary_widget)
        layout.addWidget(media_box, 1)

        return panel

    def _build_queue_workspace(self) -> QWidget:
        panel = QWidget()
        layout = QVBoxLayout(panel)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(_UI_GROUP_STACK_SPACING)

        self.queue_widget = QueueWidget()
        self.queue_widget.add_current_requested.connect(self._queue_current_source)
        self.queue_widget.add_files_requested.connect(self._queue_files)
        self.queue_widget.add_folder_requested.connect(self._queue_folder)
        self.queue_widget.remove_selected_requested.connect(self._remove_selected_queue_item)
        self.queue_widget.start_requested.connect(self._start_queue)
        self.queue_widget.cancel_requested.connect(self._cancel_queue)
        self.queue_widget.selection_changed.connect(self._handle_queue_selection_changed)

        queue_details_box = QGroupBox("Selected Queue Item")
        queue_details_layout = QVBoxLayout(queue_details_box)
        queue_details_layout.setContentsMargins(*_UI_GROUP_MARGINS)
        queue_details_layout.setSpacing(_UI_SECTION_SPACING)
        self.queue_details_widget = QueueDetailsWidget()
        queue_details_layout.addWidget(self.queue_details_widget)

        self._queue_splitter = QSplitter(Qt.Orientation.Vertical)
        self._queue_splitter.setChildrenCollapsible(False)
        self._queue_splitter.splitterMoved.connect(
            lambda _pos, _index: self._persist_splitter_layout()
        )
        self._queue_splitter.addWidget(self.queue_widget)
        self._queue_splitter.addWidget(queue_details_box)
        self._queue_splitter.setStretchFactor(0, 3)
        self._queue_splitter.setStretchFactor(1, 2)
        self._queue_splitter.setSizes([360, 280])

        layout.addWidget(self._queue_splitter)
        return panel

    def _build_execution_inspector(self) -> QWidget:
        panel = QWidget()
        panel.setSizePolicy(
            QSizePolicy.Policy.Expanding,
            QSizePolicy.Policy.Preferred,
        )
        layout = QVBoxLayout(panel)
        layout.setContentsMargins(*_UI_GROUP_MARGINS)
        layout.setSpacing(0)

        self.job_details_widget = JobDetailsWidget()
        self.job_details_widget.start_requested.connect(self._start_job)
        self.job_details_widget.cancel_requested.connect(self._cancel_job)
        layout.addWidget(self.job_details_widget)

        scroll = QScrollArea()
        scroll.setFrameShape(QFrame.Shape.NoFrame)
        scroll.setWidgetResizable(True)
        scroll.setSizePolicy(
            QSizePolicy.Policy.Expanding,
            QSizePolicy.Policy.Ignored,
        )
        scroll.setWidget(panel)
        return scroll

    def _refresh_dependency_ui(self) -> None:
        self._dependency_status = self._dependency_manager.discover(self._settings)
        configured_paths = ExecutablePaths(
            ffmpeg=self._dependency_status.ffmpeg.path or self._settings.executable_paths.ffmpeg,
            ffprobe=self._dependency_status.ffprobe.path or self._settings.executable_paths.ffprobe,
        )
        self._setup_widget.set_paths(configured_paths)
        self._setup_widget.set_status(_format_dependency_status(self._dependency_status))

        if self._dependency_status.is_ready:
            self._stack.setCurrentWidget(self._main_widget)
        else:
            self._stack.setCurrentWidget(self._setup_widget)
        self._refresh_command_preview()
        self._refresh_queue_ui()

    def _save_dependency_paths(self, executable_paths: ExecutablePaths) -> None:
        status = self._dependency_manager.validate_paths(executable_paths)
        self._setup_widget.set_status(_format_dependency_status(status))
        if not status.is_ready:
            return

        self._settings = replace(self._settings, executable_paths=status.executable_paths)
        self._settings_service.save(self._settings)
        self._refresh_dependency_ui()

    def _choose_source_file(self) -> None:
        filename, _ = QFileDialog.getOpenFileName(self, "Select Source Media")
        if filename:
            self._load_source(Path(filename))

    def _paste_source(self) -> None:
        path = self._extract_path_from_mime()
        if path is None:
            QMessageBox.warning(
                self,
                "No File Path",
                "The clipboard does not contain a file reference or a readable file path.",
            )
            return
        self._load_source(path)

    def _extract_path_from_mime(self) -> Path | None:
        return _extract_path_from_mime_data(QApplication.clipboard().mimeData())

    def _load_source(self, path: Path) -> None:
        if not self._dependency_status.is_ready or self._dependency_status.ffprobe.path is None:
            QMessageBox.warning(
                self,
                "Dependencies Required",
                "Configure valid ffprobe and ffmpeg paths before loading media.",
            )
            self._stack.setCurrentWidget(self._setup_widget)
            return

        source_path = path.expanduser()
        if not source_path.exists() or not source_path.is_file():
            QMessageBox.warning(self, "Invalid Source", f"{source_path} is not a readable file.")
            return

        probe_service = FfprobeMediaProbeService(self._dependency_status.ffprobe.path)
        try:
            media_info = probe_service.probe(source_path)
        except MediaProbeError as exc:
            LOGGER.exception("Failed to probe source media: %s", source_path)
            QMessageBox.critical(self, "Probe Failed", str(exc))
            return

        self._media_info = media_info
        self.source_path_label.setText(str(source_path))
        self.media_summary_widget.set_media_info(media_info)
        self.output_settings_widget.set_media_info(media_info)
        self._stack.setCurrentWidget(self._main_widget)
        self.workflow_tabs.setCurrentWidget(self._source_workspace)
        self._refresh_command_preview()
        self.queue_widget.set_add_current_enabled(True)
        self._refresh_queue_ui()

    def _queue_current_source(self) -> None:
        if self._media_info is None:
            QMessageBox.warning(
                self,
                "No Source Loaded",
                "Load and probe a source file before adding the current item to the queue.",
            )
            return

        settings = self.output_settings_widget.current_settings()
        queued_jobs, errors = self._build_queued_jobs(
            ((self._media_info.source_path, self._media_info),),
            settings,
        )
        self._enqueue_jobs(queued_jobs, errors)

    def _queue_files(self) -> None:
        filenames, _ = QFileDialog.getOpenFileNames(self, "Select Files for Queue")
        if not filenames:
            return
        self._queue_source_paths(Path(filename) for filename in filenames)

    def _queue_folder(self) -> None:
        directory = QFileDialog.getExistingDirectory(self, "Select Folder for Queue")
        if not directory:
            return

        source_paths = sorted(
            path
            for path in Path(directory).iterdir()
            if path.is_file() and path.suffix.lower() in _FOLDER_IMPORT_SUFFIXES
        )
        if not source_paths:
            QMessageBox.information(
                self,
                "No Supported Files",
                "The selected folder does not contain any supported media file extensions.",
            )
            return

        self._queue_source_paths(source_paths)

    def _queue_source_paths(self, paths: Iterable[Path]) -> None:
        settings = self.output_settings_widget.current_settings()
        cached_media_info = (
            {self._media_info.source_path.resolve(): self._media_info}
            if self._media_info is not None
            else {}
        )
        sources: list[tuple[Path, SourceMediaInfo]] = []
        errors: list[str] = []

        for path in paths:
            source_path = path.expanduser()
            cached = cached_media_info.get(source_path.resolve())
            if cached is not None:
                sources.append((source_path, cached))
                continue

            media_info, error = self._probe_media_for_queue(source_path)
            if media_info is None:
                if error is not None:
                    errors.append(error)
                continue
            sources.append((source_path, media_info))

        queued_jobs, queue_errors = self._build_queued_jobs(sources, settings)
        self._enqueue_jobs(queued_jobs, [*errors, *queue_errors])

    def _probe_media_for_queue(
        self,
        source_path: Path,
    ) -> tuple[SourceMediaInfo | None, str | None]:
        if not self._dependency_status.is_ready or self._dependency_status.ffprobe.path is None:
            return None, "Configure valid ffmpeg and ffprobe paths before using the queue."
        if not source_path.exists() or not source_path.is_file():
            return None, f"{source_path} is not a readable file."

        probe_service = FfprobeMediaProbeService(self._dependency_status.ffprobe.path)
        try:
            return probe_service.probe(source_path), None
        except MediaProbeError as exc:
            LOGGER.exception("Failed to probe queued source media: %s", source_path)
            return None, f"{source_path.name}: {exc}"

    def _build_queued_jobs(
        self,
        sources: Iterable[tuple[Path, SourceMediaInfo]],
        settings: OutputSettings,
    ) -> tuple[list[QueuedJob], list[str]]:
        if not self._dependency_status.is_ready or self._dependency_status.ffmpeg.path is None:
            return [], ["Configure a valid ffmpeg executable before queueing jobs."]

        queued_jobs: list[QueuedJob] = []
        errors: list[str] = []
        reserved_outputs = {
            item.queued_job.job.output_path.resolve()
            for item in self._queue_service.items()
        }
        builder = DefaultCommandBuilder(self._dependency_status.ffmpeg.path)

        for source_path, media_info in sources:
            validation = self._validation_service.validate(settings, media_info)
            if validation.has_errors:
                message = "; ".join(issue.message for issue in validation.errors)
                errors.append(f"{source_path.name}: {message}")
                continue

            output_path = _suggest_queue_output_path(
                source_path,
                settings.container,
                reserved_outputs=reserved_outputs,
            )
            reserved_outputs.add(output_path.resolve())
            job = _build_job_definition(
                source_path=source_path,
                output_path=output_path,
                settings=settings,
            )
            try:
                commands = tuple(builder.build(job, media_info))
            except CommandBuildError as exc:
                errors.append(f"{source_path.name}: {exc}")
                continue

            queued_jobs.append(
                QueuedJob(
                    job=job,
                    media_info=media_info,
                    commands=commands,
                    validation=validation,
                )
            )

        return queued_jobs, errors

    def _enqueue_jobs(self, queued_jobs: list[QueuedJob], errors: list[str]) -> None:
        created_items = self._queue_service.enqueue(queued_jobs) if queued_jobs else ()
        if created_items:
            newest_item_id = created_items[-1].id
            self.workflow_tabs.setCurrentWidget(self._queue_workspace)
            self._refresh_queue_ui(selected_id=newest_item_id)
        else:
            self._refresh_queue_ui()

        if errors:
            QMessageBox.warning(
                self,
                "Queue Import Issues",
                "\n".join(errors[:12]),
            )

    def _refresh_queue_ui(self, *, selected_id: str | None = None) -> None:
        items = tuple(self._queue_service.items())
        self.queue_widget.set_items(items, selected_id=selected_id)
        self.queue_widget.set_running(self._queue_worker is not None)
        self.queue_widget.start_button.setEnabled(
            self._queue_worker is None and self._queue_service.has_pending()
        )
        self.queue_widget.set_add_current_enabled(self._media_info is not None)
        selected_item = self._selected_queue_item()
        self.queue_widget.set_remove_enabled(
            selected_item is not None and selected_item.state is not JobState.RUNNING
        )
        self._show_selected_queue_item()

    def _handle_queue_selection_changed(self, item_id: str) -> None:
        del item_id
        selected_item = self._selected_queue_item()
        self.queue_widget.set_remove_enabled(
            selected_item is not None and selected_item.state is not JobState.RUNNING
        )
        self._show_selected_queue_item()

    def _selected_queue_item(self) -> QueueItem | None:
        selected_item_id = self.queue_widget.selected_item_id()
        for item in self._queue_service.items():
            if item.id == selected_item_id:
                return item
        return None

    def _show_selected_queue_item(self) -> None:
        selected_item = self._selected_queue_item()
        selected_item_id = selected_item.id if selected_item is not None else ""
        live_log = self._queue_live_logs.get(selected_item_id or "", "")
        self.queue_details_widget.show_item_details(selected_item, live_log=live_log)

    def _remove_selected_queue_item(self) -> None:
        selected_item = self._selected_queue_item()
        if selected_item is None or selected_item.state is JobState.RUNNING:
            self._refresh_queue_ui()
            return
        self._queue_service.remove((selected_item.id,))
        self._queue_live_logs.pop(selected_item.id, None)
        self._refresh_queue_ui()

    def _start_queue(self) -> None:
        if self._queue_worker is not None or not self._queue_service.has_pending():
            self._refresh_queue_ui()
            return

        worker = QueueExecutionWorker(self._queue_service)
        thread = QThread(self)
        worker.moveToThread(thread)

        thread.started.connect(worker.run)
        worker.item_started.connect(self._handle_queue_item_update)
        worker.item_progress.connect(self._handle_queue_item_update)
        worker.item_finished.connect(self._handle_queue_item_update)
        worker.log_line.connect(self._handle_queue_log_line)
        worker.finished.connect(self._handle_queue_finished)
        worker.failed.connect(self._handle_queue_failure)
        worker.finished.connect(thread.quit)
        worker.failed.connect(thread.quit)
        worker.finished.connect(worker.deleteLater)
        worker.failed.connect(worker.deleteLater)
        thread.finished.connect(thread.deleteLater)
        thread.finished.connect(self._clear_queue_worker)

        self._queue_thread = thread
        self._queue_worker = worker
        self.inspector_tabs.setCurrentWidget(self.execution_inspector_tab)
        self._refresh_queue_ui()
        thread.start()

    def _cancel_queue(self) -> None:
        if self._queue_worker is None:
            return
        self._queue_worker.cancel()
        self._refresh_queue_ui()

    def _handle_queue_item_update(self, item: object) -> None:
        if not isinstance(item, QueueItem):
            return
        if item.state is JobState.RUNNING:
            self._queue_live_logs.setdefault(item.id, "")
        elif item.result is not None:
            self._queue_live_logs[item.id] = item.result.log_output
        self._refresh_queue_ui(selected_id=item.id)

    def _handle_queue_log_line(self, item_id: str, line: str) -> None:
        existing = self._queue_live_logs.get(item_id, "")
        self._queue_live_logs[item_id] = f"{existing}{line}"
        if self.queue_widget.selected_item_id() == item_id:
            self._show_selected_queue_item()

    def _handle_queue_finished(self, result: object) -> None:
        del result
        self._refresh_queue_ui()

    def _handle_queue_failure(self, message: str) -> None:
        self._refresh_queue_ui()
        QMessageBox.critical(
            self,
            "Queue Failed",
            message or "Queue execution failed unexpectedly.",
        )

    def _clear_queue_worker(self) -> None:
        self._queue_thread = None
        self._queue_worker = None
        self._refresh_queue_ui()

    def _refresh_output_summary(self) -> None:
        settings = self.output_settings_widget.current_settings()
        self.output_summary_label.setText(
            _format_output_overview(settings, self._media_info, self._validation_service)
        )

    def _refresh_command_preview(self) -> None:
        self._refresh_output_summary()
        prepared, message = self._prepare_execution()
        self._prepared_execution = prepared

        if prepared is None:
            self.command_preview_widget.show_message(
                message or "No command preview is available yet."
            )
            if not self.job_details_widget.is_running:
                self.job_details_widget.set_idle_message(
                    message or "No command preview is available yet."
                )
            return

        self.command_preview_widget.set_commands(prepared.commands)
        if not self.job_details_widget.is_running:
            self.job_details_widget.set_prepared_job(prepared)
            self.job_details_widget.set_start_enabled(True)

    def _prepare_execution(self) -> tuple[PreparedExecution | None, str | None]:
        if not self._dependency_status.is_ready or self._dependency_status.ffmpeg.path is None:
            return None, "Configure a valid ffmpeg executable to preview commands."

        if self._media_info is None:
            return None, "Load a source file to generate a command preview."

        settings = self.output_settings_widget.current_settings()
        validation = self._validation_service.validate(settings, self._media_info)
        if validation.has_errors:
            error_lines = "\n".join(f"- {issue.message}" for issue in validation.errors)
            return (
                None,
                "Resolve the current validation errors before a command can be generated.\n\n"
                f"{error_lines}",
            )

        output_path = suggest_output_path(self._media_info.source_path, settings.container)
        job = _build_job_definition(
            source_path=self._media_info.source_path,
            output_path=output_path,
            settings=settings,
        )
        builder = DefaultCommandBuilder(self._dependency_status.ffmpeg.path)
        try:
            commands = tuple(builder.build(job, self._media_info))
        except CommandBuildError as exc:
            return None, str(exc)

        return PreparedExecution(job=job, commands=commands, validation=validation), None

    def _start_job(self) -> None:
        if self.job_details_widget.is_running:
            return

        prepared = self._prepared_execution
        if prepared is None:
            prepared, message = self._prepare_execution()
            self._prepared_execution = prepared
            if prepared is None:
                QMessageBox.warning(
                    self,
                    "Job Not Ready",
                    message or "The current source and settings cannot be executed yet.",
                )
                self._refresh_command_preview()
                return

        if self._media_info is None or self._dependency_status.ffmpeg.path is None:
            QMessageBox.warning(
                self,
                "Job Not Ready",
                "Load a source file and configure a valid ffmpeg path before starting a job.",
            )
            return

        execution_service = DefaultJobExecutionService(
            command_builder=DefaultCommandBuilder(self._dependency_status.ffmpeg.path),
            process_runner=SubprocessProcessRunner(),
        )
        worker = JobExecutionWorker(execution_service, prepared.job, self._media_info)
        thread = QThread(self)
        worker.moveToThread(thread)

        thread.started.connect(worker.run)
        worker.progress.connect(self._handle_job_progress)
        worker.log_line.connect(self.job_details_widget.append_log_line)
        worker.finished.connect(self._handle_job_finished)
        worker.failed.connect(self._handle_job_failure)
        worker.finished.connect(thread.quit)
        worker.failed.connect(thread.quit)
        worker.finished.connect(worker.deleteLater)
        worker.failed.connect(worker.deleteLater)
        thread.finished.connect(thread.deleteLater)
        thread.finished.connect(self._clear_execution_worker)

        self._execution_thread = thread
        self._execution_worker = worker
        self.inspector_tabs.setCurrentWidget(self.execution_inspector_tab)
        self.job_details_widget.start_run(prepared)
        thread.start()

    def _cancel_job(self) -> None:
        if self._execution_worker is None:
            return
        self.job_details_widget.progress_summary_label.setText(
            "Cancel requested. Waiting for ffmpeg to stop."
        )
        self._execution_worker.cancel()

    def _handle_job_progress(self, progress: object) -> None:
        if not isinstance(progress, JobProgress):
            return
        self.job_details_widget.update_progress(progress)

    def _handle_job_finished(self, result: object) -> None:
        if not isinstance(result, JobResult):
            return
        self.job_details_widget.show_result(result, can_start=self._can_start_current_job())

    def _handle_job_failure(self, message: str) -> None:
        self.job_details_widget.show_failure(
            message or "Job execution failed unexpectedly.",
            can_start=self._can_start_current_job(),
        )

    def _clear_execution_worker(self) -> None:
        self._execution_thread = None
        self._execution_worker = None

    def _can_start_current_job(self) -> bool:
        prepared, _ = self._prepare_execution()
        self._prepared_execution = prepared
        return prepared is not None


__all__ = [
    "DependencySetupWidget",
    "MainWindow",
    "MediaSummaryWidget",
    "OutputSettingsWidget",
    "PreparedExecution",
    "QueueDetailsWidget",
    "QueueWidget",
    "CommandPreviewWidget",
    "JobDetailsWidget",
    "_audio_codec_label",
    "_audio_mode_label",
    "_build_job_definition",
    "_build_media_summary_text",
    "_extract_path_from_mime_data",
    "_format_command_preview_text",
    "_format_dependency_status",
    "_format_output_overview",
    "_format_queue_summary",
    "_format_validation_result",
    "_job_state_label",
    "_resolution_preset_label",
    "_suggest_queue_output_path",
    "_video_codec_label",
]
