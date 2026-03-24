from __future__ import annotations

import os
import sys
from pathlib import Path

APP_DIRECTORY_NAME = "ffapp"
SETTINGS_FILE_NAME = "settings.json"


def app_data_directory() -> Path:
    if sys.platform == "win32":
        base = Path(os.environ.get("APPDATA", Path.home() / "AppData" / "Roaming"))
    elif sys.platform == "darwin":
        base = Path.home() / "Library" / "Application Support"
    else:
        xdg_config_home = os.environ.get("XDG_CONFIG_HOME")
        base = Path(xdg_config_home) if xdg_config_home else Path.home() / ".config"
    return base / APP_DIRECTORY_NAME


def settings_file_path() -> Path:
    return app_data_directory() / SETTINGS_FILE_NAME

