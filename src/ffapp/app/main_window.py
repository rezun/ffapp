from __future__ import annotations

import logging
from collections.abc import Callable
from pathlib import Path

from ffapp.core.models import AppSettings, DependencyStatus, ExecutablePaths, SourceMediaInfo
from ffapp.infrastructure.dependencies import DependencyManager
from ffapp.infrastructure.probe import FfprobeMediaProbeService, MediaProbeError
from ffapp.infrastructure.settings import JsonSettingsService

LOGGER = logging.getLogger(__name__)

try:
    from PySide6.QtGui import QAction, QDragEnterEvent, QDropEvent
    from PySide6.QtWidgets import (
        QApplication,
        QFileDialog,
        QFormLayout,
        QGroupBox,
        QHBoxLayout,
        QLabel,
        QLineEdit,
        QMainWindow,
        QMessageBox,
        QPushButton,
        QPlainTextEdit,
        QScrollArea,
        QStackedWidget,
        QVBoxLayout,
        QWidget,
    )
except ModuleNotFoundError as exc:  # pragma: no cover - imported only when PySide6 is missing
    raise RuntimeError("PySide6 must be installed to import the app layer.") from exc


class DependencySetupWidget(QWidget):
    def __init__(self, save_callback: Callable[[ExecutablePaths], None]) -> None:
        super().__init__()
        self._save_callback = save_callback

        layout = QVBoxLayout(self)
        layout.setSpacing(12)

        intro = QLabel(
            "ffmpeg and ffprobe are required before the app can probe media files. "
            "Use the detected paths or browse to the executables manually."
        )
        intro.setWordWrap(True)
        layout.addWidget(intro)

        form_layout = QFormLayout()
        self.ffmpeg_edit = QLineEdit()
        self.ffprobe_edit = QLineEdit()
        form_layout.addRow("ffmpeg", self._row_with_browse(self.ffmpeg_edit))
        form_layout.addRow("ffprobe", self._row_with_browse(self.ffprobe_edit))
        layout.addLayout(form_layout)

        self.status_label = QLabel("")
        self.status_label.setWordWrap(True)
        layout.addWidget(self.status_label)

        button_row = QHBoxLayout()
        save_button = QPushButton("Validate and Save")
        save_button.clicked.connect(self._handle_save)
        button_row.addWidget(save_button)
        button_row.addStretch(1)
        layout.addLayout(button_row)

        layout.addStretch(1)

    def set_paths(self, executable_paths: ExecutablePaths) -> None:
        self.ffmpeg_edit.setText(str(executable_paths.ffmpeg or ""))
        self.ffprobe_edit.setText(str(executable_paths.ffprobe or ""))

    def set_status(self, status: str) -> None:
        self.status_label.setText(status)

    def _handle_save(self) -> None:
        executable_paths = ExecutablePaths(
            ffmpeg=_line_edit_path(self.ffmpeg_edit),
            ffprobe=_line_edit_path(self.ffprobe_edit),
        )
        self._save_callback(executable_paths)

    def _row_with_browse(self, line_edit: QLineEdit) -> QWidget:
        row = QWidget()
        layout = QHBoxLayout(row)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.addWidget(line_edit)

        browse_button = QPushButton("Browse")
        browse_button.clicked.connect(lambda: self._browse_for_executable(line_edit))
        layout.addWidget(browse_button)
        return row

    def _browse_for_executable(self, line_edit: QLineEdit) -> None:
        filename, _ = QFileDialog.getOpenFileName(self, "Select Executable")
        if filename:
            line_edit.setText(filename)


class MediaSummaryWidget(QWidget):
    def __init__(self) -> None:
        super().__init__()
        layout = QVBoxLayout(self)
        self.summary = QPlainTextEdit()
        self.summary.setReadOnly(True)
        self.summary.setPlaceholderText("No source file selected.")
        layout.addWidget(self.summary)

    def set_media_info(self, media_info: SourceMediaInfo | None) -> None:
        if media_info is None:
            self.summary.clear()
            self.summary.setPlaceholderText("No source file selected.")
            return

        primary_video = media_info.primary_video_stream
        lines = [
            f"Source: {media_info.source_path}",
            f"Format: {media_info.container_format or 'Unknown'}",
            f"Duration: {_format_duration(media_info.duration_seconds)}",
            f"Overall bitrate: {_format_bitrate(media_info.bit_rate)}",
            "",
            f"Video streams: {len(media_info.video_streams)}",
            f"Audio streams: {len(media_info.audio_streams)}",
            f"Subtitle streams: {len(media_info.subtitle_streams)}",
            f"Data streams: {len(media_info.data_streams)}",
        ]

        if primary_video is not None:
            lines.extend(
                [
                    "",
                    "Primary video stream",
                    f"Codec: {primary_video.codec_name or 'Unknown'}",
                    f"Resolution: {_format_dimensions(primary_video.width, primary_video.height)}",
                    f"Frame rate: {_format_frame_rate(primary_video.frame_rate)}",
                    f"Bitrate: {_format_bitrate(primary_video.bit_rate)}",
                ]
            )

        if media_info.video_streams and len(media_info.video_streams) > 1:
            lines.extend(["", "Warning: multiple video streams detected. MVP uses the first stream only."])

        self.summary.setPlainText("\n".join(lines))


class MainWindow(QMainWindow):
    def __init__(
        self,
        settings_service: JsonSettingsService,
        dependency_manager: DependencyManager,
    ) -> None:
        super().__init__()
        self._settings_service = settings_service
        self._dependency_manager = dependency_manager
        self._settings = self._settings_service.load()
        self._dependency_status = self._dependency_manager.discover(self._settings)
        self._media_info: SourceMediaInfo | None = None

        self.setWindowTitle("FFmpeg GUI")
        self.resize(1040, 780)
        self.setAcceptDrops(True)

        self._stack = QStackedWidget()
        self.setCentralWidget(self._stack)
        self._setup_widget = DependencySetupWidget(self._save_dependency_paths)
        self._main_widget = self._build_main_panel()
        self._stack.addWidget(self._setup_widget)
        self._stack.addWidget(self._main_widget)

        self._configure_actions()
        self._refresh_dependency_ui()

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
        dependencies_action.triggered.connect(lambda: self._stack.setCurrentWidget(self._setup_widget))
        self.addAction(dependencies_action)

    def _build_main_panel(self) -> QWidget:
        container = QWidget()
        outer_layout = QVBoxLayout(container)

        scroll_area = QScrollArea()
        scroll_area.setWidgetResizable(True)
        outer_layout.addWidget(scroll_area)

        content = QWidget()
        layout = QVBoxLayout(content)
        layout.setSpacing(16)

        dependency_box = QGroupBox("Dependency Status")
        dependency_layout = QVBoxLayout(dependency_box)
        self.dependency_status_label = QLabel("")
        self.dependency_status_label.setWordWrap(True)
        dependency_layout.addWidget(self.dependency_status_label)

        source_box = QGroupBox("Source Selection")
        source_layout = QVBoxLayout(source_box)
        button_row = QHBoxLayout()
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

        self.source_path_label = QLabel("Drop a file here, choose one, or paste a file path from the clipboard.")
        self.source_path_label.setWordWrap(True)
        source_layout.addWidget(self.source_path_label)

        media_box = QGroupBox("Media Info Summary")
        media_layout = QVBoxLayout(media_box)
        self.media_summary_widget = MediaSummaryWidget()
        media_layout.addWidget(self.media_summary_widget)

        output_box = QGroupBox("Output Settings")
        output_layout = QVBoxLayout(output_box)
        output_layout.addWidget(
            QLabel("Phase 2 will add container, codec, validation, and resolution controls here.")
        )

        command_box = QGroupBox("Command Preview")
        command_layout = QVBoxLayout(command_box)
        command_layout.addWidget(
            QLabel("Phase 3 will add a read-only ffmpeg command preview here.")
        )

        queue_box = QGroupBox("Queue")
        queue_layout = QVBoxLayout(queue_box)
        queue_layout.addWidget(QLabel("Phase 5 will add queueing and batch execution here."))

        for widget in (dependency_box, source_box, media_box, output_box, command_box, queue_box):
            layout.addWidget(widget)

        layout.addStretch(1)
        scroll_area.setWidget(content)
        return container

    def _refresh_dependency_ui(self) -> None:
        self._dependency_status = self._dependency_manager.discover(self._settings)
        configured_paths = ExecutablePaths(
            ffmpeg=self._dependency_status.ffmpeg.path or self._settings.executable_paths.ffmpeg,
            ffprobe=self._dependency_status.ffprobe.path or self._settings.executable_paths.ffprobe,
        )
        self._setup_widget.set_paths(configured_paths)
        self._setup_widget.set_status(_format_dependency_status(self._dependency_status))
        self.dependency_status_label.setText(_format_dependency_status(self._dependency_status))

        if self._dependency_status.is_ready:
            self._stack.setCurrentWidget(self._main_widget)
        else:
            self._stack.setCurrentWidget(self._setup_widget)

    def _save_dependency_paths(self, executable_paths: ExecutablePaths) -> None:
        status = self._dependency_manager.validate_paths(executable_paths)
        self._setup_widget.set_status(_format_dependency_status(status))
        if not status.is_ready:
            return

        self._settings = AppSettings(executable_paths=status.executable_paths)
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
        self._stack.setCurrentWidget(self._main_widget)


def _line_edit_path(line_edit: QLineEdit) -> Path | None:
    value = line_edit.text().strip()
    return Path(value).expanduser() if value else None


def _format_dependency_status(status: DependencyStatus) -> str:
    return "\n".join(
        [
            _format_single_dependency(status.ffmpeg),
            _format_single_dependency(status.ffprobe),
        ]
    )


def _format_single_dependency(validation: object) -> str:
    from ffapp.core.models import ExecutableValidation

    if not isinstance(validation, ExecutableValidation):
        return "Unknown dependency state"

    if validation.is_valid:
        version = validation.version_output.splitlines()[0] if validation.version_output else "Validated"
        return f"{validation.name}: ready at {validation.path}\n{version}"
    return f"{validation.name}: {validation.error or 'Not configured'}"


def _extract_path_from_mime_data(mime_data: object) -> Path | None:
    if mime_data is None:
        return None

    urls_getter = getattr(mime_data, "urls", None)
    if callable(urls_getter):
        urls = urls_getter()
        if urls:
            for url in urls:
                if getattr(url, "isLocalFile", lambda: False)():
                    local_file = getattr(url, "toLocalFile", lambda: "")()
                    if local_file:
                        return Path(local_file)

    text_getter = getattr(mime_data, "text", None)
    if callable(text_getter):
        raw_text = text_getter().strip()
        if not raw_text:
            return None
        if raw_text.startswith("file://"):
            from urllib.parse import urlparse, unquote

            parsed = urlparse(raw_text)
            return Path(unquote(parsed.path))
        return Path(raw_text)

    return None


def _format_duration(duration_seconds: float | None) -> str:
    if duration_seconds is None:
        return "Unknown"
    total_seconds = int(duration_seconds)
    hours, remainder = divmod(total_seconds, 3600)
    minutes, seconds = divmod(remainder, 60)
    return f"{hours:02d}:{minutes:02d}:{seconds:02d}"


def _format_bitrate(bit_rate: int | None) -> str:
    if bit_rate is None:
        return "Unknown"
    return f"{bit_rate / 1_000_000:.2f} Mbps"


def _format_dimensions(width: int | None, height: int | None) -> str:
    if width is None or height is None:
        return "Unknown"
    return f"{width}x{height}"


def _format_frame_rate(frame_rate: float | None) -> str:
    if frame_rate is None:
        return "Unknown"
    return f"{frame_rate:.2f} fps"
