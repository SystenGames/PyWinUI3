"""最小示例：一个窗口、几行文字、一个按钮。

运行：
    python examples/01_hello.py
"""

import pywinui as ui

win = ui.Window(title="Hello PyWinUI", width=640, height=420)

col = ui.Column(win, padding=32, spacing=14)

ui.Label(col, "你好，WinUI 3", font_size=32, weight="semibold")
ui.Label(
    col,
    "界面由 Python 描述，渲染由独立的 C# 宿主完成。",
    font_size=14,
    foreground="#5A5A5A",
    wrap=True,
)

status = ui.Label(col, "还没有点过按钮", font_size=15)


def on_click() -> None:
    print("clicked!")
    status.text = "按钮被点了 ✓"


ui.Button(col, "点我", command=on_click, width=160, height=40)

win.mainloop()
