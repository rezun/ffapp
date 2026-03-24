from __future__ import annotations

import logging
import sys

from ffapp.infrastructure.dependencies import DependencyManager
from ffapp.infrastructure.presets import JsonPresetService
from ffapp.infrastructure.settings import JsonSettingsService


def configure_logging() -> None:
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
    )


def main() -> int:
    try:
        from PySide6.QtWidgets import QApplication
    except ModuleNotFoundError as exc:
        raise SystemExit(
            "PySide6 is not installed. Install project dependencies before launching the app."
        ) from exc

    from ffapp.app.main_window import MainWindow

    configure_logging()
    application = QApplication(sys.argv)
    window = MainWindow(
        settings_service=JsonSettingsService(),
        preset_service=JsonPresetService(),
        dependency_manager=DependencyManager(),
    )
    window.show()
    return application.exec()
