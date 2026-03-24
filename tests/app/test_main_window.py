from __future__ import annotations

import os
import unittest
from pathlib import Path
from unittest.mock import patch

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

from PySide6.QtCore import QPoint, QPointF, Qt
from PySide6.QtGui import QWheelEvent
from PySide6.QtWidgets import QApplication, QMessageBox

from ffapp.app.main_window import (
    JobDetailsWidget,
    MainWindow,
    OutputSettingsWidget,
    PreparedExecution,
    QueueDetailsWidget,
    QueueWidget,
    _format_command_preview_text,
    _format_queue_summary,
)
from ffapp.core.models import (
    AppSettings,
    AudioCodec,
    AudioMode,
    AudioOptions,
    DependencyStatus,
    EncodingStrategy,
    ExecutableValidation,
    GeneratedCommand,
    H264QualityOptions,
    JobDefinition,
    JobResult,
    JobState,
    OutputContainer,
    OutputSettings,
    PresetDefinition,
    QueuedJob,
    QueueItem,
    ResolutionPreset,
    SourceMediaInfo,
    ValidationResult,
    VideoCodec,
    VideoStreamInfo,
    Vp9QualityOptions,
)


def _command(text: str) -> GeneratedCommand:
    return GeneratedCommand(
        executable_path=Path("/usr/local/bin/ffmpeg"),
        arguments=(),
        display_string=text,
    )


class _FakeSettingsService:
    def __init__(self, settings: AppSettings | None = None) -> None:
        self.value = settings or AppSettings()

    def load(self) -> AppSettings:
        return self.value

    def save(self, settings: AppSettings) -> None:
        self.value = settings


class _FakePresetService:
    def __init__(self, presets: tuple[PresetDefinition, ...] = ()) -> None:
        self.presets = list(presets)

    def load_all(self) -> tuple[PresetDefinition, ...]:
        return tuple(sorted(self.presets, key=lambda preset: preset.name.casefold()))

    def save(self, preset: PresetDefinition) -> None:
        for index, existing in enumerate(self.presets):
            if existing.name.casefold() == preset.name.casefold():
                self.presets[index] = preset
                return
        self.presets.append(preset)

    def rename(self, current_name: str, new_name: str) -> PresetDefinition:
        for index, preset in enumerate(self.presets):
            if preset.name == current_name:
                renamed = PresetDefinition(name=new_name, settings=preset.settings)
                self.presets[index] = renamed
                return renamed
        raise AssertionError("Preset not found")

    def delete(self, name: str) -> None:
        self.presets = [preset for preset in self.presets if preset.name != name]


class _FakeDependencyManager:
    def discover(self, settings: AppSettings) -> DependencyStatus:
        del settings
        return self._status()

    def validate_paths(self, executable_paths: object) -> DependencyStatus:
        del executable_paths
        return self._status()

    @staticmethod
    def _status() -> DependencyStatus:
        return DependencyStatus(
            ffmpeg=ExecutableValidation(
                name="ffmpeg",
                path=None,
                is_valid=False,
                error="ffmpeg not configured",
            ),
            ffprobe=ExecutableValidation(
                name="ffprobe",
                path=None,
                is_valid=False,
                error="ffprobe not configured",
            ),
        )


class CommandPreviewFormattingTests(unittest.TestCase):
    def test_two_pass_preview_can_render_as_separate_commands(self) -> None:
        text = _format_command_preview_text(
            (_command("ffmpeg pass1"), _command("ffmpeg pass2")),
            single_command=False,
        )

        self.assertEqual(text, "Pass 1\n\nffmpeg pass1\n\nPass 2\n\nffmpeg pass2")

    def test_two_pass_preview_can_render_as_single_command(self) -> None:
        text = _format_command_preview_text(
            (_command("ffmpeg pass1"), _command("ffmpeg pass2")),
            single_command=True,
        )

        self.assertEqual(text, "ffmpeg pass1 && ffmpeg pass2")


def _prepared_execution() -> PreparedExecution:
    return PreparedExecution(
        job=JobDefinition(
            source_path=Path("/tmp/input.mov"),
            output_path=Path("/tmp/output.mp4"),
            container=OutputContainer.MP4,
            video_codec=VideoCodec.H264,
            encoding_strategy=EncodingStrategy.TARGET_QUALITY,
            target_quality=H264QualityOptions(),
        ),
        commands=(_command("ffmpeg pass1"), _command("ffmpeg pass2")),
        validation=ValidationResult(),
    )


class JobDetailsWidgetTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.application = QApplication.instance() or QApplication([])

    def test_prepared_job_renders_command_and_ready_state(self) -> None:
        widget = JobDetailsWidget()
        prepared = _prepared_execution()

        widget.set_prepared_job(prepared)
        widget.set_start_enabled(True)

        self.assertEqual(widget.state_label.text(), "State: Ready")
        self.assertTrue(widget.start_button.isEnabled())
        self.assertFalse(widget.command_toggle.isChecked())
        self.assertFalse(widget.command_output.isVisible())
        self.assertIn("Pass 1", widget.command_output.toPlainText())
        self.assertIn(str(prepared.job.output_path), widget.output_path_label.text())

    def test_show_result_updates_state_progress_and_log_output(self) -> None:
        widget = JobDetailsWidget()
        prepared = _prepared_execution()
        widget.start_run(prepared)

        result = JobResult(
            job=prepared.job,
            state=JobState.COMPLETED,
            commands=prepared.commands,
            process_results=(),
            elapsed_seconds=12.4,
            log_output="encoded successfully\n",
        )

        widget.show_result(result, can_start=True)

        self.assertEqual(widget.state_label.text(), "State: Completed")
        self.assertTrue(widget.start_button.isEnabled())
        self.assertFalse(widget.cancel_button.isEnabled())
        self.assertEqual(widget.progress_bar.value(), 1000)
        self.assertIn("encoded successfully", widget.log_output.toPlainText())


def _queue_item(state: JobState = JobState.PENDING) -> QueueItem:
    queued_job = QueuedJob(
        job=JobDefinition(
            source_path=Path("/tmp/input.mov"),
            output_path=Path("/tmp/output.mp4"),
            container=OutputContainer.MP4,
            video_codec=VideoCodec.H264,
            encoding_strategy=EncodingStrategy.TARGET_QUALITY,
            target_quality=H264QualityOptions(),
        ),
        media_info=SourceMediaInfo(
            source_path=Path("/tmp/input.mov"),
            video_streams=(VideoStreamInfo(index=0, codec_name="h264", width=1920, height=1080),),
        ),
        commands=(_command("ffmpeg pass1"),),
        validation=ValidationResult(),
    )
    result = None
    if state is not JobState.PENDING and state is not JobState.RUNNING:
        result = JobResult(
            job=queued_job.job,
            state=state,
            commands=queued_job.commands,
            process_results=(),
            elapsed_seconds=3.0,
            log_output="queue log output\n",
        )
    return QueueItem(
        id="queue-1",
        queued_job=queued_job,
        state=state,
        result=result,
    )


class QueueWidgetTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.application = QApplication.instance() or QApplication([])

    def test_queue_widget_tracks_summary_and_selection(self) -> None:
        widget = QueueWidget()
        item = _queue_item(JobState.COMPLETED)

        widget.set_items((item,), selected_id=item.id)

        self.assertIn("Completed", widget.summary_label.text())
        self.assertEqual(widget.selected_item_id(), item.id)

    def test_format_queue_summary_counts_queue_states(self) -> None:
        summary = _format_queue_summary(
            (
                _queue_item(JobState.PENDING),
                _queue_item(JobState.RUNNING),
                _queue_item(JobState.CANCELED),
            )
        )

        self.assertIn("Pending: 1", summary)
        self.assertIn("Running: 1", summary)
        self.assertIn("Canceled: 1", summary)

    def test_remove_selected_button_can_be_toggled(self) -> None:
        widget = QueueWidget()

        widget.set_remove_enabled(True)
        self.assertTrue(widget.remove_selected_button.isEnabled())

        widget.set_remove_enabled(False)
        self.assertFalse(widget.remove_selected_button.isEnabled())


class QueueDetailsWidgetTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.application = QApplication.instance() or QApplication([])

    def test_queue_details_widget_renders_selected_item_details(self) -> None:
        widget = QueueDetailsWidget()
        item = _queue_item(JobState.COMPLETED)

        widget.show_item_details(item)

        self.assertIn(str(item.queued_job.job.source_path), widget.detail_summary_label.text())
        self.assertIn("ffmpeg pass1", widget.detail_command_output.toPlainText())
        self.assertIn("queue log output", widget.detail_log_output.toPlainText())


class MainWindowLayoutTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.application = QApplication.instance() or QApplication([])

    def test_main_window_builds_split_workflow_tabs_and_inspector_tabs(self) -> None:
        window = MainWindow(
            _FakeSettingsService(),
            _FakePresetService(),
            _FakeDependencyManager(),
        )

        self.assertEqual(window.workflow_tabs.tabText(0), "Source")
        self.assertEqual(window.workflow_tabs.tabText(1), "Queue")
        self.assertEqual(window.inspector_tabs.tabText(0), "Preview")
        self.assertEqual(window.inspector_tabs.tabText(1), "Execution")
        self.assertIn("Container:", window.output_summary_label.text())

    def test_main_window_restores_last_used_settings_and_lists_presets(self) -> None:
        saved_settings = OutputSettings(
            container=OutputContainer.MKV,
            video_codec=VideoCodec.VP9,
            encoding_strategy=EncodingStrategy.TARGET_QUALITY,
            resolution_preset=ResolutionPreset.P1080,
            audio_options=AudioOptions(
                mode=AudioMode.REENCODE,
                codec=AudioCodec.OPUS,
                bitrate=160_000,
            ),
            target_quality=Vp9QualityOptions(quality=29, speed=3),
        )
        settings_service = _FakeSettingsService(
            AppSettings(last_used_settings=saved_settings)
        )
        preset_service = _FakePresetService(
            (PresetDefinition(name="Archive", settings=saved_settings),)
        )

        window = MainWindow(
            settings_service,
            preset_service,
            _FakeDependencyManager(),
        )

        self.assertEqual(window.output_settings_widget.current_settings(), saved_settings)
        self.assertEqual(window.preset_combo.currentText(), "Archive")

    def test_selecting_a_preset_applies_it_immediately(self) -> None:
        current_settings = OutputSettings()
        preset_settings = OutputSettings(
            container=OutputContainer.MKV,
            video_codec=VideoCodec.VP9,
            encoding_strategy=EncodingStrategy.TARGET_QUALITY,
            resolution_preset=ResolutionPreset.P1080,
            audio_options=AudioOptions(
                mode=AudioMode.REENCODE,
                codec=AudioCodec.OPUS,
                bitrate=160_000,
            ),
            target_quality=Vp9QualityOptions(quality=29, speed=3),
        )
        window = MainWindow(
            _FakeSettingsService(AppSettings(last_used_settings=current_settings)),
            _FakePresetService((PresetDefinition(name="Archive", settings=preset_settings),)),
            _FakeDependencyManager(),
        )

        window.preset_combo.setCurrentText("Archive")

        self.assertEqual(window.output_settings_widget.current_settings(), preset_settings)

    def test_preset_management_actions_update_service_and_selection(self) -> None:
        settings = OutputSettings(
            container=OutputContainer.MKV,
            video_codec=VideoCodec.VP9,
            encoding_strategy=EncodingStrategy.TARGET_QUALITY,
            resolution_preset=ResolutionPreset.P720,
            audio_options=AudioOptions(
                mode=AudioMode.REENCODE,
                codec=AudioCodec.OPUS,
                bitrate=128_000,
            ),
            target_quality=Vp9QualityOptions(quality=31, speed=2),
        )
        window = MainWindow(
            _FakeSettingsService(AppSettings(last_used_settings=settings)),
            _FakePresetService(),
            _FakeDependencyManager(),
        )

        with patch(
            "ffapp.app.main_window.QInputDialog.getText",
            return_value=("Streaming", True),
        ):
            window._save_preset_as()

        self.assertEqual(window.preset_combo.currentText(), "Streaming")

        with patch(
            "ffapp.app.main_window.QInputDialog.getText",
            return_value=("Archive", True),
        ):
            window._rename_selected_preset()

        self.assertEqual(window.preset_combo.currentText(), "Archive")

        with patch(
            "ffapp.app.main_window.QMessageBox.question",
            return_value=QMessageBox.StandardButton.Yes,
        ):
            window._delete_selected_preset()

        self.assertEqual(window.preset_combo.currentText(), "Current Settings")
        self.assertEqual(window.preset_combo.count(), 1)


class WheelGuardTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.application = QApplication.instance() or QApplication([])

    def test_unfocused_spinbox_ignores_wheel(self) -> None:
        widget = OutputSettingsWidget()
        widget.show()
        widget.container_combo.setFocus()
        self.application.processEvents()
        original_value = widget.h26x_crf_spin.value()

        QApplication.sendEvent(widget.h26x_crf_spin, _wheel_event())

        self.assertEqual(widget.h26x_crf_spin.value(), original_value)

    def test_unfocused_combobox_ignores_wheel(self) -> None:
        widget = OutputSettingsWidget()
        widget.show()
        widget.video_codec_combo.setFocus()
        self.application.processEvents()
        original_index = widget.container_combo.currentIndex()

        QApplication.sendEvent(widget.container_combo, _wheel_event())

        self.assertEqual(widget.container_combo.currentIndex(), original_index)


def _wheel_event() -> QWheelEvent:
    return QWheelEvent(
        QPointF(10, 10),
        QPointF(10, 10),
        QPoint(0, 0),
        QPoint(0, -120),
        Qt.MouseButton.NoButton,
        Qt.KeyboardModifier.NoModifier,
        Qt.ScrollPhase.ScrollUpdate,
        False,
    )


if __name__ == "__main__":
    unittest.main()
