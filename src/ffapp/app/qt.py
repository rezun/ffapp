from __future__ import annotations

try:
    from PySide6.QtCore import QSignalBlocker, QRect, Qt, QThread, QTimer, Signal
    from PySide6.QtGui import (
        QAction,
        QCloseEvent,
        QColor,
        QDragEnterEvent,
        QDropEvent,
        QGuiApplication,
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

_UI_OUTER_MARGINS = (10, 10, 10, 10)
_UI_GROUP_MARGINS = (8, 6, 8, 8)
_UI_SECTION_SPACING = 8
_UI_GROUP_STACK_SPACING = 10
_UI_FORM_HORIZONTAL_SPACING = 10
_UI_FORM_VERTICAL_SPACING = 8
_UI_INLINE_SPACING = 6
_UI_SETTINGS_TWO_COLUMN_MIN_WIDTH = 600
_INFO_GLYPH = " ⓘ"


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
        show_recommendation: bool = True,
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
        self.range_bar.setVisible(show_recommendation)
        self.hint_label.setVisible(show_recommendation)

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


__all__ = [
    "QAction",
    "QApplication",
    "QCheckBox",
    "QCloseEvent",
    "QColor",
    "QComboBox",
    "QDragEnterEvent",
    "QDropEvent",
    "QFileDialog",
    "QFormLayout",
    "QFrame",
    "QGridLayout",
    "QGroupBox",
    "QGuiApplication",
    "QHBoxLayout",
    "QInputDialog",
    "QLabel",
    "QLineEdit",
    "QListWidget",
    "QListWidgetItem",
    "QMainWindow",
    "QMessageBox",
    "QPaintEvent",
    "QPainter",
    "QPlainTextEdit",
    "QProgressBar",
    "QPushButton",
    "QRect",
    "QResizeEvent",
    "QScrollArea",
    "QSignalBlocker",
    "QSizePolicy",
    "QSlider",
    "QSpinBox",
    "QSplitter",
    "QStackedWidget",
    "QTabWidget",
    "QThread",
    "QToolButton",
    "QVBoxLayout",
    "QWheelEvent",
    "QWidget",
    "Qt",
    "Signal",
    "_INFO_GLYPH",
    "_NoWheelComboBox",
    "_NoWheelSlider",
    "_NoWheelSpinBox",
    "_SliderField",
    "_UI_FORM_HORIZONTAL_SPACING",
    "_UI_FORM_VERTICAL_SPACING",
    "_UI_GROUP_MARGINS",
    "_UI_GROUP_STACK_SPACING",
    "_UI_INLINE_SPACING",
    "_UI_OUTER_MARGINS",
    "_UI_SECTION_SPACING",
    "_UI_SETTINGS_TWO_COLUMN_MIN_WIDTH",
]
