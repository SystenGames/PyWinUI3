"""PyWinUI —— 用 tkinter 的写法，渲染真正的 WinUI 3 界面。

最小的例子::

    import pywinui as ui

    win = ui.Window(title="Hello", width=640, height=400)
    ui.Label(win, "你好，WinUI 3", font_size=28)
    ui.Button(win, "点我", command=lambda: print("clicked"))
    win.mainloop()

Python 进程不直接持有任何 XAML 对象：它把界面描述成一份「表单」，
通过管道发给独立的 C# 宿主进程渲染。UI 崩了不会带走 Python，
`mainloop()` 的语义也因此和 tkinter 完全一致。
"""

from __future__ import annotations

from .app import App
from .errors import (
    HostExited,
    HostStartError,
    HostTimeout,
    ProtocolError,
    ProtocolVersionError,
    PyWinUIError,
    UiError,
)
from .event import Event
from .layout import Column, Frame, Grid, Row, Scroll
from .protocol import PROTOCOL_VERSION
from .variables import BooleanVar, DoubleVar, IntVar, StringVar
from .widgets import (
    Button,
    Checkbox,
    ComboBox,
    Entry,
    HyperlinkButton,
    Image,
    Label,
    ListBox,
    Media,
    ProgressBar,
    ProgressRing,
    Radio,
    RepeatButton,
    Separator,
    Slider,
    SplitButton,
    Switch,
    ToggleButton,
    Widget,
)
from .window import Window

__version__ = "0.1.0"


def dialog(title: str, message: str, kind: str = "info") -> None:
    """在当前窗口弹出模态对话框。

    会阻塞窗口交互，适合演示和调试。生产界面请用界面内的状态文字。

    ::

        ui.dialog("控件事件", "Button 被点击了")
    """
    App.default().dialog(title, message, kind)

__all__ = [
    # 应用与窗口
    "App",
    "Window",
    # 布局
    "Column",
    "Row",
    "Grid",
    "Frame",
    "Scroll",
    # 控件
    "Widget",
    "Label",
    "Button",
    "HyperlinkButton",
    "ToggleButton",
    "SplitButton",
    "RepeatButton",
    "Entry",
    "Checkbox",
    "Radio",
    "Switch",
    "Slider",
    "ProgressBar",
    "ProgressRing",
    "ComboBox",
    "ListBox",
    "Image",
    "Media",
    "Separator",
    # 变量
    "StringVar",
    "IntVar",
    "DoubleVar",
    "BooleanVar",
    # 其他
    "Event",
    "dialog",
    "PROTOCOL_VERSION",
    "__version__",
    # 异常
    "PyWinUIError",
    "ProtocolError",
    "ProtocolVersionError",
    "HostStartError",
    "HostExited",
    "HostTimeout",
    "UiError",
]
