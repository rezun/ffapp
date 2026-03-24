from __future__ import annotations

import logging
from collections.abc import Callable, Iterable
from dataclasses import dataclass, replace
from enum import StrEnum
from pathlib import Path
from typing import TypeVar, cast
from urllib.parse import unquote, urlparse

from ffapp.app.execution import JobExecutionWorker, QueueExecutionWorker
from ffapp.core.commands import CommandBuildError, DefaultCommandBuilder, suggest_output_path
from ffapp.core.models import (
    AudioCodec,
    AudioMode,
    AudioOptions,
    DependencyStatus,
    EncodingStrategy,
    ExecutablePaths,
    GeneratedCommand,
    H264EncoderOptions,
    H264QualityOptions,
    H265EncoderOptions,
    H265QualityOptions,
    JobDefinition,
    JobProgress,
    JobResult,
    JobState,
    OutputContainer,
    OutputSettings,
    PresetDefinition,
    QualityOptions,
    QueuedJob,
    QueueItem,
    ResolutionPreset,
    SourceMediaInfo,
    StreamHandlingOptions,
    TargetBitrateOptions,
    TargetFileSizeOptions,
    ValidationResult,
    VideoCodec,
    Vp8QualityOptions,
    Vp9QualityOptions,
    VpxEncoderOptions,
    default_vpx_cpu_used,
)
from ffapp.core.validation import (
    VPX_SPEED_RANGE,
    DefaultCompatibilityService,
    DefaultValidationService,
    quality_preset_options,
    quality_profile_options,
)
from ffapp.infrastructure.dependencies import DependencyManager
from ffapp.infrastructure.execution import DefaultJobExecutionService, SubprocessProcessRunner
from ffapp.infrastructure.presets import JsonPresetService, PresetStorageError
from ffapp.infrastructure.probe import FfprobeMediaProbeService, MediaProbeError
from ffapp.infrastructure.queue import InMemoryQueueService
from ffapp.infrastructure.settings import JsonSettingsService

LOGGER = logging.getLogger(__name__)
T = TypeVar("T")
_CUSTOM_AUDIO_BITRATE = "custom"
_CUSTOM_PRESET_VALUE = "__custom__"
_COMMON_AUDIO_BITRATES_KBPS: tuple[int, ...] = (32, 64, 96, 128, 160, 192, 256, 320)
_UI_OUTER_MARGINS = (10, 10, 10, 10)
_UI_GROUP_MARGINS = (8, 6, 8, 8)
_UI_SECTION_SPACING = 8
_UI_GROUP_STACK_SPACING = 10
_UI_FORM_HORIZONTAL_SPACING = 10
_UI_FORM_VERTICAL_SPACING = 8
_UI_INLINE_SPACING = 6
_UI_SETTINGS_TWO_COLUMN_MIN_WIDTH = 600
_INFO_GLYPH = " ⓘ"
_PRESETS_TOOLTIP = (
    "Save reusable output configurations and reload them without rebuilding the form."
)
_OUTPUT_SETTINGS_TOOLTIP = (
    "Choose a container, codec set, resolution preset, and encoding strategy. "
    "The form only offers valid container/codec combinations and validates against "
    "the currently loaded source."
)
_RECOMMENDED_H264_CRF_RANGE = (17, 23)
_RECOMMENDED_H265_CRF_RANGE = (20, 28)
_RECOMMENDED_VPX_QUALITY_RANGE = (28, 36)
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

try:
    from PySide6.QtCore import QSignalBlocker, Qt, QThread, Signal
    from PySide6.QtGui import (
        QAction,
        QColor,
        QDragEnterEvent,
        QDropEvent,
        QPainter,
        QPaintEvent,
        QResizeEvent,
        QWheelEvent,
    )
    from PySide6.QtWidgets import (
        QApplication,
        QCheckBox,
        QComboBox,
        QFileDialog,
        QFormLayout,
        QFrame,
        QGridLayout,
        QGroupBox,
        QHBoxLayout,
        QInputDialog,
        QLabel,
        QLineEdit,
        QListWidget,
        QListWidgetItem,
        QMainWindow,
        QMessageBox,
        QPlainTextEdit,
        QProgressBar,
        QPushButton,
        QScrollArea,
        QSizePolicy,
        QSlider,
        QSpinBox,
        QSplitter,
        QStackedWidget,
        QTabWidget,
        QToolButton,
        QVBoxLayout,
        QWidget,
    )
except ModuleNotFoundError as exc:  # pragma: no cover - imported only when PySide6 is missing
    raise RuntimeError("PySide6 must be installed to import the app layer.") from exc


class _NoWheelComboBox(QComboBox):
    def __init__(self) -> None:
        super().__init__()
        self.setFocusPolicy(Qt.FocusPolicy.StrongFocus)

    def wheelEvent(self, event: QWheelEvent) -> None:
        if not self.hasFocus():
            event.ignore()
            return
        super().wheelEvent(event)


class _NoWheelSpinBox(QSpinBox):
    def __init__(self) -> None:
        super().__init__()
        self.setFocusPolicy(Qt.FocusPolicy.StrongFocus)

    def wheelEvent(self, event: QWheelEvent) -> None:
        if not self.hasFocus():
            event.ignore()
            return
        super().wheelEvent(event)


class _NoWheelSlider(QSlider):
    def __init__(self, orientation: Qt.Orientation) -> None:
        super().__init__(orientation)
        self.setFocusPolicy(Qt.FocusPolicy.StrongFocus)

    def wheelEvent(self, event: QWheelEvent) -> None:
        event.ignore()


class _RecommendedRangeBar(QWidget):
    def __init__(self, minimum: int, maximum: int) -> None:
        super().__init__()
        self._minimum = minimum
        self._maximum = maximum
        self._recommended = (minimum, maximum)
        self.setMinimumHeight(8)
        self.setMaximumHeight(8)

    def set_bounds(self, minimum: int, maximum: int) -> None:
        self._minimum = minimum
        self._maximum = maximum
        self.update()

    def set_recommended_range(self, lower: int, upper: int) -> None:
        self._recommended = (lower, upper)
        self.update()

    def paintEvent(self, event: QPaintEvent) -> None:
        del event
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing, True)

        groove_color = self.palette().color(self.backgroundRole()).darker(108)
        band_color = QColor("#5E9B5B")
        endcap_color = self.palette().color(self.foregroundRole())

        rect = self.rect().adjusted(0, 1, 0, -1)
        painter.setPen(Qt.PenStyle.NoPen)
        painter.setBrush(groove_color)
        painter.drawRoundedRect(rect, 3, 3)

        if self._maximum <= self._minimum:
            return

        span = self._maximum - self._minimum
        lower = max(self._minimum, min(self._recommended[0], self._maximum))
        upper = max(lower, min(self._recommended[1], self._maximum))
        left = rect.left() + round(((lower - self._minimum) / span) * rect.width())
        right = rect.left() + round(((upper - self._minimum) / span) * rect.width())
        recommended_rect = rect.adjusted(left - rect.left(), 0, right - rect.right(), 0)

        painter.setBrush(band_color)
        painter.drawRoundedRect(recommended_rect, 3, 3)

        painter.setBrush(endcap_color)
        painter.drawEllipse(rect.left(), rect.center().y() - 1, 2, 2)
        painter.drawEllipse(rect.right() - 1, rect.center().y() - 1, 2, 2)


class _SliderField(QWidget):
    valueChanged = Signal(int)

    def __init__(
        self,
        *,
        minimum: int,
        maximum: int,
        value: int,
        value_prefix: str = "",
    ) -> None:
        super().__init__()
        self._value_prefix = value_prefix
        layout = QGridLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setHorizontalSpacing(_UI_INLINE_SPACING)
        layout.setVerticalSpacing(4)
        layout.setColumnStretch(0, 1)

        self.slider = _NoWheelSlider(Qt.Orientation.Horizontal)
        self.slider.setRange(minimum, maximum)
        self.slider.setValue(value)
        self.slider.setSingleStep(1)
        self.slider.setPageStep(max(1, (maximum - minimum) // 10))
        self.slider.setTickInterval(max(1, (maximum - minimum) // 8))
        self.slider.setTickPosition(QSlider.TickPosition.TicksBelow)
        layout.addWidget(self.slider, 0, 0)

        self.value_label = QLabel()
        self.value_label.setMinimumWidth(48)
        self.value_label.setAlignment(
            Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter
        )
        layout.addWidget(self.value_label, 0, 1)

        self.range_bar = _RecommendedRangeBar(minimum, maximum)
        layout.addWidget(self.range_bar, 1, 0)

        self.hint_label = QLabel("")
        self.hint_label.setStyleSheet("font-size: 11px;")
        self.hint_label.setWordWrap(True)
        layout.addWidget(self.hint_label, 2, 0)

        self.slider.valueChanged.connect(self._handle_value_changed)
        self._handle_value_changed(value)

    def setRange(self, minimum: int, maximum: int) -> None:
        self.slider.setRange(minimum, maximum)
        self.range_bar.set_bounds(minimum, maximum)

    def setValue(self, value: int) -> None:
        self.slider.setValue(value)

    def value(self) -> int:
        return self.slider.value()

    def setSingleStep(self, step: int) -> None:
        self.slider.setSingleStep(step)

    def set_recommended_range(self, lower: int, upper: int, *, label: str) -> None:
        self.range_bar.set_recommended_range(lower, upper)
        self.hint_label.setText(label)

    def _handle_value_changed(self, value: int) -> None:
        prefix = f"{self._value_prefix} " if self._value_prefix else ""
        self.value_label.setText(f"{prefix}{value}")
        self.valueChanged.emit(value)


class DependencySetupWidget(QWidget):
    def __init__(self, save_callback: Callable[[ExecutablePaths], None]) -> None:
        super().__init__()
        self._save_callback = save_callback

        layout = QVBoxLayout(self)
        layout.setContentsMargins(*_UI_OUTER_MARGINS)
        layout.setSpacing(_UI_SECTION_SPACING)

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
        button_row.setSpacing(_UI_INLINE_SPACING)
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
        layout.setContentsMargins(0, 0, 0, 0)
        self.summary = QPlainTextEdit()
        self.summary.setReadOnly(True)
        self.summary.setPlaceholderText("No source file selected.")
        layout.addWidget(self.summary)

    def set_media_info(self, media_info: SourceMediaInfo | None) -> None:
        if media_info is None:
            self.summary.clear()
            self.summary.setPlaceholderText("No source file selected.")
            return

        self.summary.setPlainText(_build_media_summary_text(media_info))


class OutputSettingsWidget(QWidget):
    settings_changed = Signal()

    def __init__(self) -> None:
        super().__init__()
        self._compatibility_service = DefaultCompatibilityService()
        self._validation_service = DefaultValidationService(self._compatibility_service)
        self._media_info: SourceMediaInfo | None = None
        self._updating_form = False
        self.setSizePolicy(
            QSizePolicy.Policy.Expanding,
            QSizePolicy.Policy.Preferred,
        )

        layout = QVBoxLayout(self)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(0)

        self.container_combo = _NoWheelComboBox()
        self.video_codec_combo = _NoWheelComboBox()
        self.audio_mode_combo = _NoWheelComboBox()
        self.audio_codec_combo = _NoWheelComboBox()
        self.audio_bitrate_combo = _NoWheelComboBox()
        self.audio_bitrate_custom_spin = _NoWheelSpinBox()
        self.audio_bitrate_custom_spin.setRange(8, 1_536)
        self.audio_bitrate_custom_spin.setSingleStep(16)
        self.audio_bitrate_custom_spin.setValue(128)
        self.audio_bitrate_custom_spin.setSuffix(" kbps")
        self.audio_remix_checkbox = QCheckBox("Remix all audio streams to stereo")
        self.remove_subtitles_checkbox = QCheckBox("Remove subtitle streams")
        self.remove_data_checkbox = QCheckBox("Remove data streams")
        self.resolution_combo = _NoWheelComboBox()
        self.strategy_combo = _NoWheelComboBox()

        self.strategy_stack = QStackedWidget()
        self.strategy_stack.addWidget(self._build_file_size_panel())
        self.strategy_stack.addWidget(self._build_bitrate_panel())
        self.strategy_stack.addWidget(self._build_quality_panel())

        self.output_resolution_label = QLabel(
            "Output resolution will appear here after probing media."
        )
        self.output_resolution_label.setWordWrap(True)

        self.validation_summary = QPlainTextEdit()
        self.validation_summary.setReadOnly(True)
        self.validation_summary.setPlaceholderText(
            "Load a source file to validate output settings."
        )
        self.validation_summary.setMinimumHeight(120)
        self.validation_summary.setSizePolicy(
            QSizePolicy.Policy.Expanding,
            QSizePolicy.Policy.Expanding,
        )

        self._configure_field_widgets()

        self.format_group = self._build_format_group()
        self.audio_group = self._build_audio_group()
        self.encoding_group = self._build_encoding_group()
        self.status_group = self._build_status_group()
        self._using_two_column_layout = False

        self._group_layout = QGridLayout()
        self._group_layout.setContentsMargins(0, 0, 0, 0)
        self._group_layout.setHorizontalSpacing(_UI_GROUP_STACK_SPACING)
        self._group_layout.setVerticalSpacing(_UI_GROUP_STACK_SPACING)

        layout.addLayout(self._group_layout)
        layout.addStretch(1)

        self._populate_static_options()
        self._connect_signals()
        self._refresh_form_state()
        self._refresh_group_layout()

    def resizeEvent(self, event: QResizeEvent) -> None:
        super().resizeEvent(event)
        self._refresh_group_layout()

    def set_media_info(self, media_info: SourceMediaInfo | None) -> None:
        self._media_info = media_info
        self._refresh_validation()

    def apply_settings(self, settings: OutputSettings) -> None:
        self._updating_form = True
        try:
            self._set_combo_value(self.container_combo, settings.container)
            self._refresh_video_codec_options()
            self._refresh_audio_codec_options()

            self._set_combo_value(self.video_codec_combo, settings.video_codec)
            self._set_combo_value(self.resolution_combo, settings.resolution_preset)
            self._set_combo_value(self.strategy_combo, settings.encoding_strategy)

            self._set_combo_value(self.audio_mode_combo, settings.audio_options.mode)
            self._refresh_audio_codec_options()
            if settings.audio_options.codec is not None:
                self._set_combo_value(self.audio_codec_combo, settings.audio_options.codec)
            self._apply_audio_bitrate(settings.audio_options.bitrate)
            self.audio_remix_checkbox.setChecked(settings.audio_options.remix_to_stereo)

            self.remove_subtitles_checkbox.setChecked(
                settings.stream_handling.remove_subtitles
            )
            self.remove_data_checkbox.setChecked(settings.stream_handling.remove_data)

            self.target_size_spin.setValue(settings.target_file_size.target_size_mb)
            self.file_size_two_pass_checkbox.setChecked(settings.target_file_size.two_pass)
            self.target_bitrate_spin.setValue(settings.target_bitrate.video_bitrate // 1_000)
            self.bitrate_two_pass_checkbox.setChecked(settings.target_bitrate.two_pass)

            self._refresh_quality_controls()
            if settings.video_codec in {VideoCodec.H264, VideoCodec.H265}:
                self._apply_h26x_settings(settings)
            else:
                self._apply_vpx_settings(settings)

            self._refresh_audio_controls()
            self._refresh_strategy_panel()
        finally:
            self._updating_form = False

        self._refresh_validation()

    def current_settings(self) -> OutputSettings:
        video_codec = self._selected_value(self.video_codec_combo, VideoCodec.H264)
        audio_mode = self._selected_value(self.audio_mode_combo, AudioMode.COPY)
        audio_codec = self._selected_audio_codec() if audio_mode is AudioMode.REENCODE else None
        audio_bitrate = (
            self._selected_audio_bitrate() * 1_000 if audio_mode is AudioMode.REENCODE else None
        )
        return OutputSettings(
            container=self._selected_value(self.container_combo, OutputContainer.MP4),
            video_codec=video_codec,
            encoding_strategy=self._selected_value(
                self.strategy_combo,
                EncodingStrategy.TARGET_QUALITY,
            ),
            resolution_preset=self._selected_value(
                self.resolution_combo,
                ResolutionPreset.ORIGINAL,
            ),
            video_encoder_options=self._current_video_encoder_options(video_codec),
            audio_options=AudioOptions(
                mode=audio_mode,
                codec=audio_codec,
                bitrate=audio_bitrate,
                remix_to_stereo=self.audio_remix_checkbox.isChecked(),
            ),
            stream_handling=StreamHandlingOptions(
                remove_subtitles=self.remove_subtitles_checkbox.isChecked(),
                remove_data=self.remove_data_checkbox.isChecked(),
            ),
            target_file_size=TargetFileSizeOptions(
                target_size_mb=self.target_size_spin.value(),
                two_pass=self.file_size_two_pass_checkbox.isChecked(),
            ),
            target_bitrate=TargetBitrateOptions(
                video_bitrate=self.target_bitrate_spin.value() * 1_000,
                two_pass=self.bitrate_two_pass_checkbox.isChecked(),
            ),
            target_quality=self._current_quality_options(video_codec),
        )

    def _apply_h26x_settings(self, settings: OutputSettings) -> None:
        default_profile = "high" if settings.video_codec is VideoCodec.H264 else "main"
        preset = "medium"
        profile = default_profile
        if isinstance(
            settings.video_encoder_options,
            H264EncoderOptions | H265EncoderOptions,
        ):
            preset = settings.video_encoder_options.preset
            profile = settings.video_encoder_options.profile
        if isinstance(settings.target_quality, H264QualityOptions | H265QualityOptions):
            preset = settings.target_quality.preset
            profile = settings.target_quality.profile
            self.h26x_crf_spin.setValue(settings.target_quality.crf)
        else:
            self.h26x_crf_spin.setValue(23 if settings.video_codec is VideoCodec.H264 else 28)

        self._set_combo_value(self.h26x_preset_combo, preset)
        self._set_combo_value(self.h26x_profile_combo, profile)

    def _apply_vpx_settings(self, settings: OutputSettings) -> None:
        if isinstance(settings.target_quality, Vp8QualityOptions | Vp9QualityOptions):
            self.vpx_quality_spin.setValue(settings.target_quality.quality)
            self.vpx_quality_two_pass_checkbox.setChecked(settings.target_quality.two_pass)
        else:
            self.vpx_quality_spin.setValue(31 if settings.video_codec is VideoCodec.VP8 else 32)
            self.vpx_quality_two_pass_checkbox.setChecked(False)

        cpu_used = default_vpx_cpu_used(settings.video_codec)
        if (
            isinstance(settings.video_encoder_options, VpxEncoderOptions)
            and settings.video_encoder_options.codec is settings.video_codec
        ):
            cpu_used = settings.video_encoder_options.cpu_used
        self.vpx_speed_spin.setValue(cpu_used)

    def _apply_audio_bitrate(self, bitrate: int | None) -> None:
        if bitrate is None:
            default_index = self.audio_bitrate_combo.findData(128)
            self.audio_bitrate_combo.setCurrentIndex(default_index if default_index >= 0 else 0)
            self.audio_bitrate_custom_spin.setValue(128)
            return

        bitrate_kbps = max(8, bitrate // 1_000)
        index = self.audio_bitrate_combo.findData(bitrate_kbps)
        if index >= 0:
            self.audio_bitrate_combo.setCurrentIndex(index)
            self.audio_bitrate_custom_spin.setValue(bitrate_kbps)
            return

        custom_index = self.audio_bitrate_combo.findData(_CUSTOM_AUDIO_BITRATE)
        self.audio_bitrate_combo.setCurrentIndex(custom_index if custom_index >= 0 else 0)
        self.audio_bitrate_custom_spin.setValue(bitrate_kbps)

    def _build_file_size_panel(self) -> QWidget:
        panel = QWidget()
        layout = QFormLayout(panel)
        self._configure_form_layout(layout)

        self.target_size_spin = _NoWheelSpinBox()
        self.target_size_spin.setRange(1, 100_000)
        self.target_size_spin.setValue(50)
        self.target_size_spin.setSuffix(" MB")
        self.file_size_two_pass_checkbox = QCheckBox("Use 2-pass encode")

        layout.addRow("Target Size", self.target_size_spin)
        layout.addRow("", self.file_size_two_pass_checkbox)
        return panel

    def _build_bitrate_panel(self) -> QWidget:
        panel = QWidget()
        layout = QFormLayout(panel)
        self._configure_form_layout(layout)

        self.target_bitrate_spin = _NoWheelSpinBox()
        self.target_bitrate_spin.setRange(100, 100_000)
        self.target_bitrate_spin.setValue(3_000)
        self.target_bitrate_spin.setSingleStep(100)
        self.target_bitrate_spin.setSuffix(" kbps")
        self.bitrate_two_pass_checkbox = QCheckBox("Use 2-pass encode")

        layout.addRow("Video Bitrate", self.target_bitrate_spin)
        layout.addRow("", self.bitrate_two_pass_checkbox)
        return panel

    def _build_quality_panel(self) -> QWidget:
        panel = QWidget()
        panel.setSizePolicy(
            QSizePolicy.Policy.Expanding,
            QSizePolicy.Policy.Fixed,
        )
        outer_layout = QVBoxLayout(panel)
        outer_layout.setContentsMargins(0, 0, 0, 0)

        self.quality_stack = QStackedWidget()
        self.quality_stack.setSizePolicy(
            QSizePolicy.Policy.Expanding,
            QSizePolicy.Policy.Fixed,
        )
        self.quality_stack.addWidget(self._build_h26x_quality_panel())
        self.quality_stack.addWidget(self._build_vpx_quality_panel())
        outer_layout.addWidget(self.quality_stack)

        return panel

    def _build_h26x_quality_panel(self) -> QWidget:
        panel = QWidget()
        layout = QFormLayout(panel)
        self._configure_form_layout(layout)

        self.h26x_crf_spin = _SliderField(minimum=0, maximum=51, value=23, value_prefix="CRF")
        self.h26x_preset_combo = _NoWheelComboBox()
        self.h26x_profile_combo = _NoWheelComboBox()
        layout.addRow("CRF", self.h26x_crf_spin)
        return panel

    def _build_h26x_encoder_group(self) -> QGroupBox:
        group = QGroupBox("H.264 / H.265 Encoder Tuning")
        self._configure_group_box(group)
        layout = QFormLayout(group)
        self._configure_form_layout(layout)

        layout.addRow("Preset", self.h26x_preset_combo)
        layout.addRow("Profile", self.h26x_profile_combo)
        return group

    def _build_vpx_encoder_group(self) -> QGroupBox:
        group = QGroupBox("VP8 / VP9 Encoder Tuning")
        self._configure_group_box(group)
        layout = QFormLayout(group)
        self._configure_form_layout(layout)
        layout.addRow("CPU Used", self.vpx_speed_spin)
        return group

    def _build_vpx_quality_panel(self) -> QWidget:
        panel = QWidget()
        layout = QFormLayout(panel)
        self._configure_form_layout(layout)

        self.vpx_quality_spin = _SliderField(
            minimum=0,
            maximum=63,
            value=32,
            value_prefix="CQ",
        )
        self.vpx_quality_two_pass_checkbox = QCheckBox("Use 2-pass encode")
        self.vpx_speed_spin = _NoWheelSpinBox()
        self.vpx_speed_spin.setRange(VPX_SPEED_RANGE.start, VPX_SPEED_RANGE.stop - 1)
        self.vpx_speed_spin.setValue(2)

        layout.addRow("Quality", self.vpx_quality_spin)
        layout.addRow("", self.vpx_quality_two_pass_checkbox)
        return panel

    def _build_format_group(self) -> QGroupBox:
        group = QGroupBox("Format and Video")
        self._configure_group_box(group)
        layout = QFormLayout(group)
        self._configure_form_layout(layout)
        layout.addRow("Container", self.container_combo)
        layout.addRow("Video Codec", self.video_codec_combo)
        layout.addRow("Resolution", self.resolution_combo)
        return group

    def _build_audio_group(self) -> QGroupBox:
        group = QGroupBox("Audio and Streams")
        self._configure_group_box(group)
        layout = QGridLayout(group)
        layout.setContentsMargins(*_UI_GROUP_MARGINS)
        layout.setHorizontalSpacing(_UI_FORM_HORIZONTAL_SPACING)
        layout.setVerticalSpacing(_UI_FORM_VERTICAL_SPACING)
        layout.setColumnStretch(1, 1)

        layout.addWidget(QLabel("Audio Mode"), 0, 0)
        layout.addWidget(self.audio_mode_combo, 0, 1)
        layout.addWidget(QLabel("Audio Codec"), 1, 0)
        layout.addWidget(self.audio_codec_combo, 1, 1)
        layout.addWidget(QLabel("Audio Bitrate"), 2, 0)
        layout.addWidget(self._build_audio_bitrate_row(), 2, 1)
        layout.addWidget(self.audio_remix_checkbox, 3, 1)
        layout.addWidget(self.remove_subtitles_checkbox, 4, 1)
        layout.addWidget(self.remove_data_checkbox, 5, 1)
        return group

    def _build_audio_bitrate_row(self) -> QWidget:
        row = QWidget()
        layout = QHBoxLayout(row)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(_UI_INLINE_SPACING)
        layout.addWidget(self.audio_bitrate_combo, 2)
        layout.addWidget(self.audio_bitrate_custom_spin, 1)
        return row

    def _build_encoding_group(self) -> QGroupBox:
        group = QGroupBox("Encoding")
        self._configure_group_box(group)
        layout = QVBoxLayout(group)
        layout.setContentsMargins(*_UI_GROUP_MARGINS)
        layout.setSpacing(_UI_SECTION_SPACING)
        form_layout = QFormLayout()
        self._configure_form_layout(form_layout)
        form_layout.setContentsMargins(0, 0, 0, 0)
        form_layout.addRow("Strategy", self.strategy_combo)
        layout.addLayout(form_layout)
        self.strategy_stack.setSizePolicy(
            QSizePolicy.Policy.Expanding,
            QSizePolicy.Policy.Fixed,
        )
        layout.addWidget(self.strategy_stack)
        self.h26x_encoder_group = self._build_h26x_encoder_group()
        self.h26x_encoder_group.setSizePolicy(
            QSizePolicy.Policy.Preferred,
            QSizePolicy.Policy.Fixed,
        )
        layout.addWidget(self.h26x_encoder_group)
        self.vpx_encoder_group = self._build_vpx_encoder_group()
        self.vpx_encoder_group.setSizePolicy(
            QSizePolicy.Policy.Preferred,
            QSizePolicy.Policy.Fixed,
        )
        layout.addWidget(self.vpx_encoder_group)
        layout.addStretch(1)
        return group

    def _build_status_group(self) -> QGroupBox:
        group = QGroupBox("Validation and Output")
        self._configure_group_box(group)
        layout = QVBoxLayout(group)
        layout.setContentsMargins(*_UI_GROUP_MARGINS)
        layout.setSpacing(_UI_SECTION_SPACING)

        resolution_heading = QLabel("Resolved Output")
        resolution_heading.setStyleSheet("font-weight: 600;")
        layout.addWidget(resolution_heading)
        layout.addWidget(self.output_resolution_label)

        validation_heading = QLabel("Validation")
        validation_heading.setStyleSheet("font-weight: 600;")
        layout.addWidget(validation_heading)
        layout.addWidget(self.validation_summary, 1)
        return group

    def _refresh_group_layout(self) -> None:
        use_two_columns = self.width() >= _UI_SETTINGS_TWO_COLUMN_MIN_WIDTH
        if use_two_columns == self._using_two_column_layout and self._group_layout.count() > 0:
            return

        self._using_two_column_layout = use_two_columns
        for group in (
            self.format_group,
            self.audio_group,
            self.encoding_group,
            self.status_group,
        ):
            self._group_layout.removeWidget(group)

        if use_two_columns:
            self._group_layout.addWidget(self.format_group, 0, 0)
            self._group_layout.addWidget(self.audio_group, 1, 0)
            self._group_layout.addWidget(self.encoding_group, 2, 0)
            self._group_layout.addWidget(self.status_group, 0, 1, 3, 1)
            self._group_layout.setColumnStretch(0, 3)
            self._group_layout.setColumnStretch(1, 2)
            self._group_layout.setRowStretch(0, 0)
            self._group_layout.setRowStretch(1, 0)
            self._group_layout.setRowStretch(2, 1)
            self._group_layout.setRowStretch(3, 0)
            return

        self._group_layout.addWidget(self.format_group, 0, 0)
        self._group_layout.addWidget(self.audio_group, 1, 0)
        self._group_layout.addWidget(self.encoding_group, 2, 0)
        self._group_layout.addWidget(self.status_group, 3, 0)
        self._group_layout.setColumnStretch(0, 1)
        self._group_layout.setColumnStretch(1, 0)
        self._group_layout.setRowStretch(0, 0)
        self._group_layout.setRowStretch(1, 0)
        self._group_layout.setRowStretch(2, 0)
        self._group_layout.setRowStretch(3, 1)

    def _configure_field_widgets(self) -> None:
        expanding_fields: tuple[QWidget, ...] = (
            self.container_combo,
            self.video_codec_combo,
            self.audio_mode_combo,
            self.audio_codec_combo,
            self.audio_bitrate_combo,
            self.audio_bitrate_custom_spin,
            self.resolution_combo,
            self.strategy_combo,
            self.target_size_spin,
            self.target_bitrate_spin,
            self.h26x_crf_spin,
            self.h26x_preset_combo,
            self.h26x_profile_combo,
            self.vpx_quality_spin,
            self.vpx_quality_two_pass_checkbox,
            self.vpx_speed_spin,
        )
        for widget in expanding_fields:
            widget.setMinimumWidth(140)
            widget.setSizePolicy(
                QSizePolicy.Policy.Expanding,
                QSizePolicy.Policy.Fixed,
            )

    def _configure_form_layout(self, layout: QFormLayout) -> None:
        layout.setContentsMargins(*_UI_GROUP_MARGINS)
        layout.setHorizontalSpacing(_UI_FORM_HORIZONTAL_SPACING)
        layout.setVerticalSpacing(_UI_FORM_VERTICAL_SPACING)
        layout.setFieldGrowthPolicy(QFormLayout.FieldGrowthPolicy.ExpandingFieldsGrow)

    def _configure_group_box(self, group: QGroupBox) -> None:
        del group

    def _populate_static_options(self) -> None:
        for container in OutputContainer:
            self.container_combo.addItem(container.value.upper(), container)

        for mode in AudioMode:
            self.audio_mode_combo.addItem(_audio_mode_label(mode), mode)

        for bitrate in _COMMON_AUDIO_BITRATES_KBPS:
            self.audio_bitrate_combo.addItem(f"{bitrate} kbps", bitrate)
        self.audio_bitrate_combo.addItem("Custom", _CUSTOM_AUDIO_BITRATE)
        default_audio_bitrate_index = self.audio_bitrate_combo.findData(128)
        if default_audio_bitrate_index >= 0:
            self.audio_bitrate_combo.setCurrentIndex(default_audio_bitrate_index)

        for preset in ResolutionPreset:
            self.resolution_combo.addItem(_resolution_preset_label(preset), preset)

        self.strategy_combo.addItem("Target File Size", EncodingStrategy.TARGET_FILE_SIZE)
        self.strategy_combo.addItem("Target Bitrate", EncodingStrategy.TARGET_BITRATE)
        self.strategy_combo.addItem("Target Quality", EncodingStrategy.TARGET_QUALITY)
        self.strategy_combo.setCurrentIndex(2)

    def _connect_signals(self) -> None:
        self.container_combo.currentIndexChanged.connect(self._handle_container_changed)
        self.video_codec_combo.currentIndexChanged.connect(self._refresh_form_state)
        self.audio_mode_combo.currentIndexChanged.connect(self._handle_audio_mode_changed)
        self.audio_codec_combo.currentIndexChanged.connect(self._refresh_validation)
        self.audio_bitrate_combo.currentIndexChanged.connect(self._handle_audio_bitrate_changed)
        self.audio_bitrate_custom_spin.valueChanged.connect(self._refresh_validation)
        self.audio_remix_checkbox.toggled.connect(self._handle_audio_remix_toggled)
        self.remove_subtitles_checkbox.toggled.connect(self._refresh_validation)
        self.remove_data_checkbox.toggled.connect(self._refresh_validation)
        self.resolution_combo.currentIndexChanged.connect(self._refresh_validation)
        self.strategy_combo.currentIndexChanged.connect(self._refresh_form_state)
        self.target_size_spin.valueChanged.connect(self._refresh_validation)
        self.file_size_two_pass_checkbox.toggled.connect(self._refresh_validation)
        self.target_bitrate_spin.valueChanged.connect(self._refresh_validation)
        self.bitrate_two_pass_checkbox.toggled.connect(self._refresh_validation)
        self.h26x_crf_spin.valueChanged.connect(self._refresh_validation)
        self.h26x_preset_combo.currentIndexChanged.connect(self._refresh_validation)
        self.h26x_profile_combo.currentIndexChanged.connect(self._refresh_validation)
        self.vpx_quality_spin.valueChanged.connect(self._refresh_validation)
        self.vpx_quality_two_pass_checkbox.toggled.connect(self._refresh_validation)
        self.vpx_speed_spin.valueChanged.connect(self._refresh_validation)

    def _handle_container_changed(self) -> None:
        if self._updating_form:
            return
        self._refresh_form_state()

    def _handle_audio_mode_changed(self) -> None:
        if self._updating_form:
            return
        self._refresh_audio_controls()
        self._refresh_validation()

    def _handle_audio_bitrate_changed(self) -> None:
        if self._updating_form:
            return
        self._refresh_audio_controls()
        self._refresh_validation()

    def _handle_audio_remix_toggled(self, checked: bool) -> None:
        if self._updating_form:
            return
        if (
            checked
            and self._selected_value(self.audio_mode_combo, AudioMode.COPY)
            is not AudioMode.REENCODE
        ):
            self._set_combo_value(self.audio_mode_combo, AudioMode.REENCODE)
            self._refresh_form_state()
            return
        self._refresh_audio_controls()
        self._refresh_validation()

    def _refresh_form_state(self) -> None:
        if self._updating_form:
            return

        self._updating_form = True
        try:
            self._refresh_video_codec_options()
            self._refresh_audio_codec_options()
            self._refresh_audio_controls()
            self._refresh_quality_controls()
            self._refresh_strategy_panel()
        finally:
            self._updating_form = False

        self._refresh_validation()

    def _refresh_video_codec_options(self) -> None:
        allowed_codecs = self._compatibility_service.allowed_video_codecs(
            self._selected_value(self.container_combo, OutputContainer.MP4)
        )
        current_value = self._selected_value(self.video_codec_combo, allowed_codecs[0])
        self._replace_combo_items(
            self.video_codec_combo,
            allowed_codecs,
            labeler=_video_codec_label,
            selected=current_value,
        )

    def _refresh_audio_codec_options(self) -> None:
        allowed_codecs = self._compatibility_service.allowed_audio_codecs(
            self._selected_value(self.container_combo, OutputContainer.MP4)
        )
        current_value = self._selected_audio_codec() or allowed_codecs[0]
        self._replace_combo_items(
            self.audio_codec_combo,
            allowed_codecs,
            labeler=_audio_codec_label,
            selected=current_value,
        )

    def _refresh_audio_controls(self) -> None:
        audio_mode = self._selected_value(self.audio_mode_combo, AudioMode.COPY)
        is_reencode = audio_mode is AudioMode.REENCODE
        self.audio_codec_combo.setEnabled(is_reencode)
        self.audio_bitrate_combo.setEnabled(is_reencode)
        self.audio_bitrate_custom_spin.setEnabled(
            is_reencode and self.audio_bitrate_combo.currentData() == _CUSTOM_AUDIO_BITRATE
        )
        self.audio_remix_checkbox.setEnabled(audio_mode is not AudioMode.REMOVE)

    def _refresh_quality_controls(self) -> None:
        video_codec = self._selected_value(self.video_codec_combo, VideoCodec.H264)
        is_h26x = video_codec in {VideoCodec.H264, VideoCodec.H265}
        self.quality_stack.setCurrentIndex(0 if is_h26x else 1)

        if is_h26x:
            self._apply_h26x_quality_recommendation(video_codec)
            self.h26x_encoder_group.setVisible(True)
            self.vpx_encoder_group.setVisible(False)
            preset_options = quality_preset_options(video_codec)
            profile_options = quality_profile_options(video_codec)
            default_profile = "high" if video_codec is VideoCodec.H264 else "main"
            current_preset = self._selected_value(self.h26x_preset_combo, "medium")
            current_profile = self._selected_value(self.h26x_profile_combo, default_profile)
            self._replace_combo_items(
                self.h26x_preset_combo,
                preset_options,
                labeler=str,
                selected=current_preset,
            )
            self._replace_combo_items(
                self.h26x_profile_combo,
                profile_options,
                labeler=str,
                selected=current_profile,
            )
            if self.h26x_profile_combo.currentText() not in profile_options:
                self._set_combo_value(self.h26x_profile_combo, default_profile)

            if video_codec is VideoCodec.H264 and self.h26x_crf_spin.value() == 28:
                self.h26x_crf_spin.setValue(23)
            elif video_codec is VideoCodec.H265 and self.h26x_crf_spin.value() == 23:
                self.h26x_crf_spin.setValue(28)
        else:
            self._apply_vpx_quality_recommendation()
            self.h26x_encoder_group.setVisible(False)
            self.vpx_encoder_group.setVisible(True)
            if self.vpx_speed_spin.value() not in VPX_SPEED_RANGE:
                self.vpx_speed_spin.setValue(default_vpx_cpu_used(video_codec))

        self._sync_stack_height(self.quality_stack)

    def _apply_h26x_quality_recommendation(self, video_codec: VideoCodec) -> None:
        lower, upper = (
            _RECOMMENDED_H264_CRF_RANGE
            if video_codec is VideoCodec.H264
            else _RECOMMENDED_H265_CRF_RANGE
        )
        self.h26x_crf_spin.set_recommended_range(
            lower,
            upper,
            label=f"Recommended quality range: {lower}-{upper}",
        )

    def _apply_vpx_quality_recommendation(self) -> None:
        lower, upper = _RECOMMENDED_VPX_QUALITY_RANGE
        self.vpx_quality_spin.set_recommended_range(
            lower,
            upper,
            label=f"Recommended quality range: {lower}-{upper}",
        )

    def _refresh_strategy_panel(self) -> None:
        strategy = self._selected_value(self.strategy_combo, EncodingStrategy.TARGET_QUALITY)
        panel_index = {
            EncodingStrategy.TARGET_FILE_SIZE: 0,
            EncodingStrategy.TARGET_BITRATE: 1,
            EncodingStrategy.TARGET_QUALITY: 2,
        }[strategy]
        self.strategy_stack.setCurrentIndex(panel_index)
        self._sync_stack_height(self.strategy_stack)

    def _refresh_validation(self) -> None:
        settings = self.current_settings()
        self._update_output_resolution_label(settings)

        if self._media_info is None:
            self.validation_summary.clear()
            self.validation_summary.setPlaceholderText(
                "Load a source file to validate output settings."
            )
            self.settings_changed.emit()
            return

        validation = self._validation_service.validate(settings, self._media_info)
        self.validation_summary.setPlainText(_format_validation_result(validation))
        self.settings_changed.emit()

    def _update_output_resolution_label(self, settings: OutputSettings) -> None:
        if self._media_info is None:
            self.output_resolution_label.setText(
                "Output resolution will be derived from the loaded source."
            )
            return

        resolution = self._validation_service.resolve_output_resolution(settings, self._media_info)
        if resolution is None:
            self.output_resolution_label.setText(
                "Output resolution is unavailable until ffprobe returns source dimensions."
            )
            return

        self.output_resolution_label.setText(
            f"Resolved output resolution: {resolution.width}x{resolution.height}"
        )

    def _current_quality_options(self, video_codec: VideoCodec) -> QualityOptions:
        if video_codec is VideoCodec.H264:
            return H264QualityOptions(
                crf=self.h26x_crf_spin.value(),
                preset=self.h26x_preset_combo.currentText(),
                profile=self.h26x_profile_combo.currentText(),
            )
        if video_codec is VideoCodec.H265:
            return H265QualityOptions(
                crf=self.h26x_crf_spin.value(),
                preset=self.h26x_preset_combo.currentText(),
                profile=self.h26x_profile_combo.currentText(),
            )
        if video_codec is VideoCodec.VP8:
            return Vp8QualityOptions(
                quality=self.vpx_quality_spin.value(),
                two_pass=self.vpx_quality_two_pass_checkbox.isChecked(),
            )
        return Vp9QualityOptions(
            quality=self.vpx_quality_spin.value(),
            two_pass=self.vpx_quality_two_pass_checkbox.isChecked(),
        )

    def _current_video_encoder_options(
        self,
        video_codec: VideoCodec,
    ) -> H264EncoderOptions | H265EncoderOptions | VpxEncoderOptions | None:
        if video_codec is VideoCodec.H264:
            return H264EncoderOptions(
                preset=self.h26x_preset_combo.currentText(),
                profile=self.h26x_profile_combo.currentText(),
            )
        if video_codec is VideoCodec.H265:
            return H265EncoderOptions(
                preset=self.h26x_preset_combo.currentText(),
                profile=self.h26x_profile_combo.currentText(),
            )
        return VpxEncoderOptions(
            codec=video_codec,
            cpu_used=self.vpx_speed_spin.value(),
        )

    def _selected_audio_codec(self) -> AudioCodec | None:
        current_data = self.audio_codec_combo.currentData()
        if current_data is None:
            return None
        try:
            return AudioCodec(current_data)
        except ValueError:
            return None

    def _selected_audio_bitrate(self) -> int:
        current_data = self.audio_bitrate_combo.currentData()
        if current_data == _CUSTOM_AUDIO_BITRATE:
            return self.audio_bitrate_custom_spin.value()
        if isinstance(current_data, int):
            return current_data
        return 128

    def _selected_value(self, combo_box: QComboBox, fallback: T) -> T:
        current_data = combo_box.currentData()
        if current_data is None:
            return fallback
        if isinstance(fallback, StrEnum):
            try:
                return type(fallback)(current_data)
            except ValueError:
                return fallback
        return cast(T, current_data)

    def _sync_stack_height(self, stack: QStackedWidget) -> None:
        current_widget = stack.currentWidget()
        layout = current_widget.layout()
        if layout is not None:
            layout.activate()
        current_widget.adjustSize()
        hint = current_widget.sizeHint()
        target_height = hint.height() + (stack.frameWidth() * 2)
        stack.setFixedHeight(target_height)

    def _replace_combo_items(
        self,
        combo_box: QComboBox,
        values: Iterable[object],
        *,
        labeler: Callable[[object], str],
        selected: object,
    ) -> None:
        with QSignalBlocker(combo_box):
            combo_box.clear()
            values_tuple = tuple(values)
            for value in values_tuple:
                combo_box.addItem(labeler(value), value)
            if not values_tuple:
                return
            target_value = selected if selected in values_tuple else values_tuple[0]
            index = combo_box.findData(target_value)
            combo_box.setCurrentIndex(index if index >= 0 else 0)

    def _set_combo_value(self, combo_box: QComboBox, value: object) -> None:
        index = combo_box.findData(value)
        if index >= 0:
            with QSignalBlocker(combo_box):
                combo_box.setCurrentIndex(index)


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

        self.setWindowTitle("FFmpeg GUI")
        self.resize(1180, 760)
        self.setAcceptDrops(True)

        self._stack = QStackedWidget()
        self.setCentralWidget(self._stack)
        self._setup_widget = DependencySetupWidget(self._save_dependency_paths)
        self._main_widget = self._build_main_panel()
        self._stack.addWidget(self._setup_widget)
        self._stack.addWidget(self._main_widget)

        self._configure_actions()
        self.output_settings_widget.apply_settings(self._settings.last_used_settings)
        self._reload_presets()
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

    def _set_combo_value(self, combo_box: QComboBox, value: object) -> None:
        index = combo_box.findData(value)
        if index >= 0:
            with QSignalBlocker(combo_box):
                combo_box.setCurrentIndex(index)

    def _build_main_panel(self) -> QWidget:
        container = QWidget()
        outer_layout = QVBoxLayout(container)
        outer_layout.setContentsMargins(*_UI_OUTER_MARGINS)

        splitter = QSplitter(Qt.Orientation.Horizontal)
        splitter.setChildrenCollapsible(False)
        splitter.setHandleWidth(10)
        outer_layout.addWidget(splitter)

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
        right_splitter = QSplitter(Qt.Orientation.Vertical)
        right_splitter.setChildrenCollapsible(False)
        right_splitter.setHandleWidth(10)
        right_layout.addWidget(right_splitter)

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

        right_splitter.addWidget(top_scroll)
        right_splitter.addWidget(self.inspector_tabs)
        right_splitter.setStretchFactor(0, 3)
        right_splitter.setStretchFactor(1, 2)
        right_splitter.setSizes([560, 280])

        splitter.addWidget(left_panel)
        splitter.addWidget(right_panel)
        splitter.setStretchFactor(0, 2)
        splitter.setStretchFactor(1, 5)
        splitter.setSizes([300, 860])

        self.output_settings_widget.settings_changed.connect(self._handle_output_settings_changed)
        self._refresh_preset_controls()
        self._refresh_queue_ui()
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

        splitter = QSplitter(Qt.Orientation.Vertical)
        splitter.setChildrenCollapsible(False)
        splitter.addWidget(self.queue_widget)
        splitter.addWidget(queue_details_box)
        splitter.setStretchFactor(0, 3)
        splitter.setStretchFactor(1, 2)
        splitter.setSizes([360, 280])

        layout.addWidget(splitter)
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


def _line_edit_path(line_edit: QLineEdit) -> Path | None:
    value = line_edit.text().strip()
    return Path(value).expanduser() if value else None


def _build_media_summary_text(media_info: SourceMediaInfo) -> str:
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

    if media_info.audio_streams:
        lines.extend(["", "Audio streams detail"])
        for stream in media_info.audio_streams:
            lines.extend(
                [
                    f"Track {stream.index}",
                    f"Codec: {stream.codec_name or 'Unknown'}",
                    f"Language: {_format_language(stream.language)}",
                    f"Channels: {_format_channels(stream.channels)}",
                    f"Bitrate: {_format_audio_bitrate(stream.bit_rate)}",
                    "",
                ]
            )
        lines.pop()

    if media_info.video_streams and len(media_info.video_streams) > 1:
        lines.extend(
            [
                "",
                "Warning: multiple video streams detected. "
                "MVP uses the first stream only.",
            ]
        )

    return "\n".join(lines)


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
        version = (
            validation.version_output.splitlines()[0]
            if validation.version_output
            else "Validated"
        )
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


def _format_audio_bitrate(bit_rate: int | None) -> str:
    if bit_rate is None:
        return "Unknown"
    return f"{bit_rate / 1_000:.0f} kbps"


def _format_dimensions(width: int | None, height: int | None) -> str:
    if width is None or height is None:
        return "Unknown"
    return f"{width}x{height}"


def _format_frame_rate(frame_rate: float | None) -> str:
    if frame_rate is None:
        return "Unknown"
    return f"{frame_rate:.2f} fps"


def _format_channels(channels: int | None) -> str:
    if channels is None:
        return "Unknown"
    if channels == 1:
        return "1 channel"
    return f"{channels} channels"


def _format_language(language: str | None) -> str:
    if language is None:
        return "Unknown"
    return language


def _video_codec_label(codec: object) -> str:
    labels = {
        VideoCodec.H264: "H.264",
        VideoCodec.H265: "H.265",
        VideoCodec.VP8: "VP8",
        VideoCodec.VP9: "VP9",
    }
    if isinstance(codec, VideoCodec):
        return labels[codec]
    return str(codec)


def _audio_codec_label(codec: object) -> str:
    labels = {
        AudioCodec.AAC: "AAC",
        AudioCodec.MP3: "MP3",
        AudioCodec.VORBIS: "Vorbis",
        AudioCodec.OPUS: "Opus",
    }
    if isinstance(codec, AudioCodec):
        return labels[codec]
    return str(codec)


def _audio_mode_label(mode: AudioMode) -> str:
    labels = {
        AudioMode.COPY: "Copy All Audio Streams",
        AudioMode.REENCODE: "Re-encode All Audio Streams",
        AudioMode.REMOVE: "Remove All Audio Streams",
    }
    return labels[mode]


def _resolution_preset_label(preset: ResolutionPreset) -> str:
    labels = {
        ResolutionPreset.ORIGINAL: "Original Source Resolution",
        ResolutionPreset.P720: "720p",
        ResolutionPreset.P1080: "1080p",
        ResolutionPreset.P1440: "1440p",
        ResolutionPreset.P4K: "4K",
    }
    return labels[preset]


def _format_output_overview(
    settings: OutputSettings,
    media_info: SourceMediaInfo | None,
    validation_service: DefaultValidationService,
) -> str:
    resolution_text = _resolution_preset_label(settings.resolution_preset)
    if media_info is not None:
        resolved = validation_service.resolve_output_resolution(settings, media_info)
        if resolved is not None:
            resolution_text = f"{resolved.width}x{resolved.height}"

    audio_text = "Copy all audio streams"
    if settings.audio_options.mode is AudioMode.REMOVE:
        audio_text = "Remove all audio streams"
    elif settings.audio_options.mode is AudioMode.REENCODE:
        bitrate_kbps = (
            f"{settings.audio_options.bitrate // 1_000} kbps"
            if settings.audio_options.bitrate is not None
            else "custom bitrate"
        )
        remix_text = " stereo remix" if settings.audio_options.remix_to_stereo else ""
        audio_text = (
            f"{_audio_codec_label(settings.audio_options.codec or AudioCodec.AAC)} "
            f"{bitrate_kbps}{remix_text}"
        )

    strategy_text = {
        EncodingStrategy.TARGET_FILE_SIZE: (
            f"Target size {settings.target_file_size.target_size_mb} MB"
            f"{' • 2-pass' if settings.target_file_size.two_pass else ''}"
        ),
        EncodingStrategy.TARGET_BITRATE: (
            f"Target bitrate {settings.target_bitrate.video_bitrate // 1_000} kbps"
            f"{' • 2-pass' if settings.target_bitrate.two_pass else ''}"
        ),
        EncodingStrategy.TARGET_QUALITY: _format_quality_summary(
            settings.target_quality,
            settings.video_encoder_options,
        ),
    }[settings.encoding_strategy]

    return " | ".join(
        (
            f"Container: {settings.container.upper()}",
            f"Video: {_video_codec_label(settings.video_codec)}",
            f"Resolution: {resolution_text}",
            f"Audio: {audio_text}",
            strategy_text,
        )
    )


def _format_quality_summary(
    options: QualityOptions,
    video_encoder_options: object,
) -> str:
    if isinstance(options, H264QualityOptions | H265QualityOptions):
        return (
            f"Quality: CRF {options.crf} • Preset {options.preset} • Profile {options.profile}"
        )
    cpu_used = "default"
    if isinstance(video_encoder_options, VpxEncoderOptions):
        cpu_used = str(video_encoder_options.cpu_used)
    suffix = " • 2-pass" if options.two_pass else ""
    return f"Quality: {options.quality} • CPU Used {cpu_used}{suffix}"


def _format_validation_result(result: ValidationResult) -> str:
    if not result.issues:
        return "Current settings are valid for the loaded source."

    lines: list[str] = []
    if result.errors:
        lines.append("Errors")
        lines.extend(f"- {issue.message}" for issue in result.errors)
    if result.warnings:
        if lines:
            lines.append("")
        lines.append("Warnings")
        lines.extend(f"- {issue.message}" for issue in result.warnings)
    return "\n".join(lines)


def _format_command_preview_text(
    commands: Iterable[GeneratedCommand],
    *,
    single_command: bool,
) -> str:
    command_list = tuple(commands)
    if not command_list:
        return ""

    if len(command_list) == 1:
        return command_list[0].display_string

    if single_command:
        return " && ".join(command.display_string for command in command_list)

    sections: list[str] = []
    for index, command in enumerate(command_list, start=1):
        sections.append(f"Pass {index}")
        sections.append(command.display_string)
    return "\n\n".join(sections)


def _job_state_label(state: JobState) -> str:
    labels = {
        JobState.PENDING: "Pending",
        JobState.RUNNING: "Running",
        JobState.COMPLETED: "Completed",
        JobState.FAILED: "Failed",
        JobState.CANCELED: "Canceled",
    }
    return labels[state]


def _format_job_progress(progress: JobProgress) -> str:
    details = [
        f"State: {_job_state_label(progress.state)}",
        f"Pass: {progress.pass_index} of {progress.pass_count}",
        f"Elapsed: {_format_precise_duration(progress.elapsed_seconds)}",
        f"Processed: {_format_precise_duration(progress.processed_seconds)}",
        f"Progress: {_format_percentage(progress.percentage)}",
        f"Speed: {_format_speed(progress.speed)}",
    ]
    return " | ".join(details)


def _format_job_result_summary(result: JobResult) -> str:
    return (
        f"State: {_job_state_label(result.state)} | "
        f"Elapsed: {_format_precise_duration(result.elapsed_seconds)} | "
        f"Commands Run: {len(result.process_results)} of {len(result.commands)}"
    )


def _format_precise_duration(seconds: float | None) -> str:
    if seconds is None:
        return "Unknown"
    total_seconds = int(seconds)
    milliseconds = int((seconds - total_seconds) * 10)
    hours, remainder = divmod(total_seconds, 3600)
    minutes, remaining_seconds = divmod(remainder, 60)
    return f"{hours:02d}:{minutes:02d}:{remaining_seconds:02d}.{milliseconds}"


def _format_percentage(value: float | None) -> str:
    if value is None:
        return "Unknown"
    return f"{value:.1f}%"


def _format_speed(value: float | None) -> str:
    if value is None:
        return "Unknown"
    return f"{value:.2f}x"


def _format_queue_summary(items: Iterable[QueueItem]) -> str:
    item_list = tuple(items)
    if not item_list:
        return "Queue is empty."

    counts = {
        state: sum(1 for item in item_list if item.state is state)
        for state in JobState
    }
    return (
        f"Items: {len(item_list)} | "
        f"Pending: {counts[JobState.PENDING]} | "
        f"Running: {counts[JobState.RUNNING]} | "
        f"Completed: {counts[JobState.COMPLETED]} | "
        f"Failed: {counts[JobState.FAILED]} | "
        f"Canceled: {counts[JobState.CANCELED]}"
    )


def _format_queue_item_label(item: QueueItem) -> str:
    progress = ""
    if item.progress is not None and item.progress.percentage is not None:
        progress = f" | {_format_percentage(item.progress.percentage)}"
    return (
        f"{item.queued_job.job.source_path.name} -> "
        f"{item.queued_job.job.output_path.name} | "
        f"{_job_state_label(item.state)}{progress}"
    )


def _format_queue_item_details(item: QueueItem) -> str:
    lines = [
        f"Source: {item.queued_job.job.source_path}",
        f"Output: {item.queued_job.job.output_path}",
        f"State: {_job_state_label(item.state)}",
        f"Passes: {len(item.queued_job.commands)}",
    ]
    if item.progress is not None:
        lines.append(_format_job_progress(item.progress))
    elif item.result is not None:
        lines.append(_format_job_result_summary(item.result))

    if item.queued_job.validation.has_warnings:
        lines.append(f"Warnings: {len(item.queued_job.validation.warnings)}")
    if item.failure_message:
        lines.append(f"Failure: {item.failure_message}")
    return "\n".join(lines)


def _suggest_queue_output_path(
    source_path: Path,
    container: OutputContainer,
    *,
    reserved_outputs: set[Path],
) -> Path:
    candidate = source_path.with_suffix(f".{container.value}")
    if not candidate.exists() and candidate.resolve() not in reserved_outputs:
        return candidate

    counter = 1
    while True:
        numbered_candidate = candidate.with_name(
            f"{candidate.stem}-{counter}{candidate.suffix}"
        )
        if not numbered_candidate.exists() and numbered_candidate.resolve() not in reserved_outputs:
            return numbered_candidate
        counter += 1


def _build_job_definition(
    *,
    source_path: Path,
    output_path: Path,
    settings: OutputSettings,
) -> JobDefinition:
    return JobDefinition(
        source_path=source_path,
        output_path=output_path,
        container=settings.container,
        video_codec=settings.video_codec,
        encoding_strategy=settings.encoding_strategy,
        video_encoder_options=settings.video_encoder_options,
        audio_options=settings.audio_options,
        stream_handling=settings.stream_handling,
        resolution_preset=settings.resolution_preset,
        target_file_size=settings.target_file_size,
        target_bitrate=settings.target_bitrate,
        target_quality=settings.target_quality,
    )
