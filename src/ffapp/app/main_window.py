from __future__ import annotations

import logging
from collections.abc import Callable, Iterable
from dataclasses import dataclass
from enum import StrEnum
from pathlib import Path
from typing import TypeVar, cast
from urllib.parse import unquote, urlparse

from ffapp.app.execution import JobExecutionWorker
from ffapp.core.commands import CommandBuildError, DefaultCommandBuilder, suggest_output_path
from ffapp.core.models import (
    AppSettings,
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
    QualityOptions,
    ResolutionPreset,
    SourceMediaInfo,
    StreamHandlingOptions,
    TargetBitrateOptions,
    TargetFileSizeOptions,
    ValidationResult,
    VideoCodec,
    Vp8QualityOptions,
    Vp9QualityOptions,
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
from ffapp.infrastructure.probe import FfprobeMediaProbeService, MediaProbeError
from ffapp.infrastructure.settings import JsonSettingsService

LOGGER = logging.getLogger(__name__)
T = TypeVar("T")
_CUSTOM_AUDIO_BITRATE = "custom"
_COMMON_AUDIO_BITRATES_KBPS: tuple[int, ...] = (32, 64, 96, 128, 160, 192, 256, 320)

try:
    from PySide6.QtCore import QSignalBlocker, Qt, QThread, Signal
    from PySide6.QtGui import QAction, QDragEnterEvent, QDropEvent
    from PySide6.QtWidgets import (
        QApplication,
        QCheckBox,
        QComboBox,
        QFileDialog,
        QFormLayout,
        QGridLayout,
        QGroupBox,
        QHBoxLayout,
        QLabel,
        QLineEdit,
        QMainWindow,
        QMessageBox,
        QPlainTextEdit,
        QProgressBar,
        QPushButton,
        QScrollArea,
        QSizePolicy,
        QSpinBox,
        QStackedWidget,
        QToolButton,
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

        self.summary.setPlainText(_build_media_summary_text(media_info))


class OutputSettingsWidget(QWidget):
    settings_changed = Signal()

    def __init__(self) -> None:
        super().__init__()
        self._compatibility_service = DefaultCompatibilityService()
        self._validation_service = DefaultValidationService(self._compatibility_service)
        self._media_info: SourceMediaInfo | None = None
        self._updating_form = False

        layout = QVBoxLayout(self)
        layout.setSpacing(12)

        intro = QLabel(
            "Choose a container, codec set, resolution preset, and encoding strategy. "
            "The form only offers valid container/codec combinations and validates against "
            "the currently loaded source."
        )
        intro.setWordWrap(True)
        layout.addWidget(intro)

        self.container_combo = QComboBox()
        self.video_codec_combo = QComboBox()
        self.audio_mode_combo = QComboBox()
        self.audio_codec_combo = QComboBox()
        self.audio_bitrate_combo = QComboBox()
        self.audio_bitrate_custom_spin = QSpinBox()
        self.audio_bitrate_custom_spin.setRange(8, 1_536)
        self.audio_bitrate_custom_spin.setSingleStep(16)
        self.audio_bitrate_custom_spin.setValue(128)
        self.audio_bitrate_custom_spin.setSuffix(" kbps")
        self.audio_remix_checkbox = QCheckBox("Remix all audio streams to stereo")
        self.remove_subtitles_checkbox = QCheckBox("Remove subtitle streams")
        self.remove_data_checkbox = QCheckBox("Remove data streams")
        self.resolution_combo = QComboBox()
        self.strategy_combo = QComboBox()

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
        self.validation_summary.setMinimumHeight(220)
        self.validation_summary.setSizePolicy(
            QSizePolicy.Policy.Expanding,
            QSizePolicy.Policy.Expanding,
        )

        self._configure_field_widgets()

        content_layout = QHBoxLayout()
        content_layout.setSpacing(16)
        layout.addLayout(content_layout)

        left_column = QVBoxLayout()
        left_column.setSpacing(12)
        right_column = QVBoxLayout()
        right_column.setSpacing(12)
        content_layout.addLayout(left_column, 3)
        content_layout.addLayout(right_column, 2)

        left_column.addWidget(self._build_format_group())
        left_column.addWidget(self._build_audio_group())
        left_column.addWidget(self._build_encoding_group())
        left_column.addStretch(1)

        right_column.addWidget(self._build_status_group(), 1)

        layout.addStretch(1)

        self._populate_static_options()
        self._connect_signals()
        self._refresh_form_state()

    def set_media_info(self, media_info: SourceMediaInfo | None) -> None:
        self._media_info = media_info
        self._refresh_validation()

    def current_settings(self) -> OutputSettings:
        video_codec = self._selected_value(self.video_codec_combo, VideoCodec.H264)
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
                mode=self._selected_value(self.audio_mode_combo, AudioMode.COPY),
                codec=self._selected_audio_codec(),
                bitrate=self._selected_audio_bitrate() * 1_000,
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

    def _build_file_size_panel(self) -> QWidget:
        panel = QWidget()
        layout = QFormLayout(panel)
        self._configure_form_layout(layout)

        self.target_size_spin = QSpinBox()
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

        self.target_bitrate_spin = QSpinBox()
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

        self.h26x_crf_spin = QSpinBox()
        self.h26x_crf_spin.setRange(0, 51)
        self.h26x_crf_spin.setValue(23)
        self.h26x_preset_combo = QComboBox()
        self.h26x_profile_combo = QComboBox()
        layout.addRow("CRF", self.h26x_crf_spin)
        return panel

    def _build_h26x_encoder_group(self) -> QGroupBox:
        group = QGroupBox("H.264 / H.265 Encoder Tuning")
        layout = QFormLayout(group)
        self._configure_form_layout(layout)

        layout.addRow("Preset", self.h26x_preset_combo)
        layout.addRow("Profile", self.h26x_profile_combo)
        return group

    def _build_vpx_quality_panel(self) -> QWidget:
        panel = QWidget()
        layout = QFormLayout(panel)
        self._configure_form_layout(layout)

        self.vpx_quality_spin = QSpinBox()
        self.vpx_quality_spin.setRange(0, 63)
        self.vpx_quality_spin.setValue(32)
        self.vpx_speed_spin = QSpinBox()
        self.vpx_speed_spin.setRange(VPX_SPEED_RANGE.start, VPX_SPEED_RANGE.stop - 1)
        self.vpx_speed_spin.setValue(2)

        layout.addRow("Quality", self.vpx_quality_spin)
        layout.addRow("Speed", self.vpx_speed_spin)
        return panel

    def _build_format_group(self) -> QGroupBox:
        group = QGroupBox("Format and Video")
        layout = QFormLayout(group)
        self._configure_form_layout(layout)
        layout.addRow("Container", self.container_combo)
        layout.addRow("Video Codec", self.video_codec_combo)
        layout.addRow("Resolution", self.resolution_combo)
        return group

    def _build_audio_group(self) -> QGroupBox:
        group = QGroupBox("Audio and Streams")
        layout = QGridLayout(group)
        layout.setHorizontalSpacing(12)
        layout.setVerticalSpacing(10)
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
        layout.setSpacing(8)
        layout.addWidget(self.audio_bitrate_combo, 2)
        layout.addWidget(self.audio_bitrate_custom_spin, 1)
        return row

    def _build_encoding_group(self) -> QGroupBox:
        group = QGroupBox("Encoding")
        layout = QVBoxLayout(group)
        form_layout = QFormLayout()
        self._configure_form_layout(form_layout)
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
        layout.addStretch(1)
        return group

    def _build_status_group(self) -> QGroupBox:
        group = QGroupBox("Validation and Output")
        layout = QVBoxLayout(group)

        resolution_heading = QLabel("Resolved Output")
        resolution_heading.setStyleSheet("font-weight: 600;")
        layout.addWidget(resolution_heading)
        layout.addWidget(self.output_resolution_label)

        validation_heading = QLabel("Validation")
        validation_heading.setStyleSheet("font-weight: 600;")
        layout.addWidget(validation_heading)
        layout.addWidget(self.validation_summary, 1)
        return group

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
            self.vpx_speed_spin,
        )
        for widget in expanding_fields:
            widget.setMinimumWidth(220)
            widget.setSizePolicy(
                QSizePolicy.Policy.Expanding,
                QSizePolicy.Policy.Fixed,
            )

    def _configure_form_layout(self, layout: QFormLayout) -> None:
        layout.setHorizontalSpacing(12)
        layout.setVerticalSpacing(10)
        layout.setFieldGrowthPolicy(QFormLayout.FieldGrowthPolicy.ExpandingFieldsGrow)

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
            self.h26x_encoder_group.setVisible(True)
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
            self.h26x_encoder_group.setVisible(False)
            if self.vpx_speed_spin.value() not in VPX_SPEED_RANGE:
                self.vpx_speed_spin.setValue(2)

        self._sync_stack_height(self.quality_stack)

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
                speed=self.vpx_speed_spin.value(),
            )
        return Vp9QualityOptions(
            quality=self.vpx_quality_spin.value(),
            speed=self.vpx_speed_spin.value(),
        )

    def _current_video_encoder_options(
        self,
        video_codec: VideoCodec,
    ) -> H264EncoderOptions | H265EncoderOptions | None:
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
        return None

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
        intro = QLabel(
            "Run the current job here. Progress, pass state, and ffmpeg log output stay attached "
            "to the active execution."
        )
        intro.setWordWrap(True)
        layout.addWidget(intro)

        button_row = QHBoxLayout()
        self.start_button = QPushButton("Start Job")
        self.start_button.clicked.connect(self.start_requested.emit)
        self.cancel_button = QPushButton("Cancel Job")
        self.cancel_button.clicked.connect(self.cancel_requested.emit)
        button_row.addWidget(self.start_button)
        button_row.addWidget(self.cancel_button)
        button_row.addStretch(1)
        layout.addLayout(button_row)

        self.state_label = QLabel("State: Idle")
        self.state_label.setStyleSheet("font-weight: 600;")
        layout.addWidget(self.state_label)

        self.output_path_label = QLabel("Output: No job prepared.")
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
        self.command_output.setMinimumHeight(140)
        self.command_output.setPlaceholderText("Prepared commands will appear here.")
        layout.addWidget(self.command_output)
        self.command_output.setVisible(False)

        log_heading = QLabel("ffmpeg Log")
        log_heading.setStyleSheet("font-weight: 600;")
        layout.addWidget(log_heading)

        self.log_output = QPlainTextEdit()
        self.log_output.setReadOnly(True)
        self.log_output.setMinimumHeight(180)
        self.log_output.setPlaceholderText("ffmpeg stderr output will appear here during a job.")
        layout.addWidget(self.log_output)

        self.set_start_enabled(False)
        self.cancel_button.setEnabled(False)

    def _set_command_section_visible(self, visible: bool) -> None:
        self.command_toggle.setArrowType(
            Qt.ArrowType.DownArrow if visible else Qt.ArrowType.RightArrow
        )
        self.command_output.setVisible(visible)

    def set_idle_message(self, message: str) -> None:
        self._running = False
        self.state_label.setText("State: Idle")
        self.output_path_label.setText("Output: No job prepared.")
        self.progress_summary_label.setText(message)
        self.progress_bar.setRange(0, 1000)
        self.progress_bar.setValue(0)
        self.command_output.clear()
        self.command_output.setPlaceholderText("Prepared commands will appear here.")
        self.command_toggle.setChecked(False)
        self.log_output.clear()
        self.start_button.setEnabled(False)
        self.cancel_button.setEnabled(False)

    def set_prepared_job(self, prepared: PreparedExecution) -> None:
        if self._running:
            return

        warning_text = ""
        if prepared.validation.has_warnings:
            warning_count = len(prepared.validation.warnings)
            warning_text = f" {warning_count} warning(s) will not block execution."

        self.state_label.setText("State: Ready")
        self.output_path_label.setText(f"Output: {prepared.job.output_path}")
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
        self.cancel_button.setEnabled(False)

    def start_run(self, prepared: PreparedExecution) -> None:
        self._running = True
        self.state_label.setText("State: Running")
        self.output_path_label.setText(f"Output: {prepared.job.output_path}")
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
            self.state_label.setText(
                f"State: Running (Pass {progress.pass_index} of {progress.pass_count})"
            )
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
        self.state_label.setText(f"State: {_job_state_label(result.state)}")
        self.output_path_label.setText(f"Output: {result.job.output_path}")
        self.progress_summary_label.setText(_format_job_result_summary(result))
        self.progress_bar.setRange(0, 1000)
        self.progress_bar.setValue(1000 if result.state is JobState.COMPLETED else 0)
        self.command_output.setPlainText(
            _format_command_preview_text(result.commands, single_command=False)
        )
        self.log_output.setPlainText(
            result.log_output or "No ffmpeg stderr output was captured for this job."
        )
        self.start_button.setEnabled(can_start)
        self.cancel_button.setEnabled(False)

    def show_failure(self, message: str, *, can_start: bool) -> None:
        self._running = False
        self.state_label.setText("State: Failed")
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


class MainWindow(QMainWindow):
    def __init__(
        self,
        settings_service: JsonSettingsService,
        dependency_manager: DependencyManager,
    ) -> None:
        super().__init__()
        self._settings_service = settings_service
        self._dependency_manager = dependency_manager
        self._validation_service = DefaultValidationService(DefaultCompatibilityService())
        self._settings = self._settings_service.load()
        self._dependency_status = self._dependency_manager.discover(self._settings)
        self._media_info: SourceMediaInfo | None = None
        self._prepared_execution: PreparedExecution | None = None
        self._execution_thread: QThread | None = None
        self._execution_worker: JobExecutionWorker | None = None

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
        dependencies_action.triggered.connect(
            lambda: self._stack.setCurrentWidget(self._setup_widget)
        )
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

        self.source_path_label = QLabel(
            "Drop a file here, choose one, or paste a file path from the clipboard."
        )
        self.source_path_label.setWordWrap(True)
        source_layout.addWidget(self.source_path_label)

        media_box = QGroupBox("Media Info Summary")
        media_layout = QVBoxLayout(media_box)
        self.media_summary_widget = MediaSummaryWidget()
        media_layout.addWidget(self.media_summary_widget)

        output_box = QGroupBox("Output Settings")
        output_layout = QVBoxLayout(output_box)
        self.output_settings_widget = OutputSettingsWidget()
        output_layout.addWidget(self.output_settings_widget)

        command_box = QGroupBox("Command Preview")
        command_layout = QVBoxLayout(command_box)
        self.command_preview_widget = CommandPreviewWidget()
        command_layout.addWidget(self.command_preview_widget)

        job_box = QGroupBox("Job Details")
        job_layout = QVBoxLayout(job_box)
        self.job_details_widget = JobDetailsWidget()
        self.job_details_widget.start_requested.connect(self._start_job)
        self.job_details_widget.cancel_requested.connect(self._cancel_job)
        job_layout.addWidget(self.job_details_widget)

        queue_box = QGroupBox("Queue")
        queue_layout = QVBoxLayout(queue_box)
        queue_layout.addWidget(QLabel("Phase 5 will add queueing and batch execution here."))

        for widget in (source_box, media_box, output_box, command_box, job_box, queue_box):
            layout.addWidget(widget)

        layout.addStretch(1)
        scroll_area.setWidget(content)
        self.output_settings_widget.settings_changed.connect(self._refresh_command_preview)
        return container

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
        self.output_settings_widget.set_media_info(media_info)
        self._stack.setCurrentWidget(self._main_widget)
        self._refresh_command_preview()

    def _refresh_command_preview(self) -> None:
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
        intro = QLabel(
            "The preview below reflects the effective ffmpeg command for the current source "
            "and settings. It stays read-only, but you can copy or export it."
        )
        intro.setWordWrap(True)
        layout.addWidget(intro)

        button_row = QHBoxLayout()
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
        self.preview.setMinimumHeight(180)
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
