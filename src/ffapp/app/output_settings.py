from __future__ import annotations

from collections.abc import Callable, Iterable
from enum import StrEnum
from pathlib import Path
from typing import TypeVar, cast

from ffapp.app.formatting import (
    _audio_codec_label,
    _audio_mode_label,
    _build_media_summary_text,
    _format_validation_result,
    _resolution_preset_label,
    _video_codec_label,
)
from ffapp.app.qt import (
    QCheckBox,
    QComboBox,
    QFileDialog,
    QFormLayout,
    QGridLayout,
    QGroupBox,
    QHBoxLayout,
    QLabel,
    QLineEdit,
    QPlainTextEdit,
    QPushButton,
    QResizeEvent,
    QSignalBlocker,
    QSizePolicy,
    QSlider,
    QStackedWidget,
    QVBoxLayout,
    QWidget,
    Qt,
    Signal,
    _NoWheelComboBox,
    _NoWheelSpinBox,
    _SliderField,
    _UI_FORM_HORIZONTAL_SPACING,
    _UI_FORM_VERTICAL_SPACING,
    _UI_GROUP_MARGINS,
    _UI_GROUP_STACK_SPACING,
    _UI_INLINE_SPACING,
    _UI_OUTER_MARGINS,
    _UI_SECTION_SPACING,
    _UI_SETTINGS_TWO_COLUMN_MIN_WIDTH,
)
from ffapp.core.models import (
    AudioCodec,
    AudioMode,
    AudioOptions,
    EncodingStrategy,
    ExecutablePaths,
    H264EncoderOptions,
    H264QualityOptions,
    H265EncoderOptions,
    H265QualityOptions,
    OutputContainer,
    OutputSettings,
    QualityOptions,
    ResolutionPreset,
    SourceMediaInfo,
    StreamHandlingOptions,
    TargetBitrateOptions,
    TargetFileSizeOptions,
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

T = TypeVar("T")
_CUSTOM_AUDIO_BITRATE = "custom"
_COMMON_AUDIO_BITRATES_KBPS: tuple[int, ...] = (32, 64, 96, 128, 160, 192, 256, 320)
_RECOMMENDED_H264_CRF_RANGE = (17, 23)
_RECOMMENDED_H265_CRF_RANGE = (20, 28)
_RECOMMENDED_VPX_QUALITY_RANGE = (28, 36)


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
        self.vpx_speed_slider.setValue(cpu_used)

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
        layout.addRow("CPU Used", self.vpx_speed_slider)
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
        self.vpx_speed_slider = _SliderField(
            minimum=VPX_SPEED_RANGE.start,
            maximum=VPX_SPEED_RANGE.stop - 1,
            value=2,
            value_prefix="CPU",
            show_recommendation=False,
        )

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
            self.vpx_speed_slider,
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
        self.vpx_speed_slider.valueChanged.connect(self._refresh_validation)

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
            if self.vpx_speed_slider.value() not in VPX_SPEED_RANGE:
                self.vpx_speed_slider.setValue(default_vpx_cpu_used(video_codec))

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
            cpu_used=self.vpx_speed_slider.value(),
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


def _line_edit_path(line_edit: QLineEdit) -> Path | None:
    value = line_edit.text().strip()
    return Path(value).expanduser() if value else None


__all__ = [
    "DependencySetupWidget",
    "MediaSummaryWidget",
    "OutputSettingsWidget",
]
