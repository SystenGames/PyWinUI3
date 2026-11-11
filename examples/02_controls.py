"""控件总览：把当前支持的所有控件摆在一个窗口里。

操作任何一个控件，都会弹出对话框告诉你：
  - 哪个控件被操作了
  - 发生了什么事件
  - 修改后的值是什么

用途是「肉眼验收」——不用读日志就能确认每种控件和每种事件都通了。

运行：
    python examples/02_controls.py
"""

from pathlib import Path

import pywinui3 as ui

ASSETS = Path(__file__).parent / "assets"


def report(name: str, action: str, value: object = None) -> None:
    """统一的操作回报。所有控件都走这里，格式一致。"""
    body = f"控件：{name}\n事件：{action}"
    if value is not None:
        body += f"\n当前值：{value}"
    ui.dialog("控件事件", body)


# ----------------------------------------------------------------------
# 窗口
# ----------------------------------------------------------------------

win = ui.Window(title="PyWinUI3 控件总览", width=980, height=900)

root = ui.Column(win, padding=24, spacing=10)

ui.Label(root, "PyWinUI3 控件总览", font_size=26, weight="semibold")
ui.Label(
    root,
    "操作下面任意控件，都会弹窗显示「哪个控件、什么事件、当前值」。"
    "对话框会挡住操作，点「知道了」继续。",
    font_size=13,
    foreground="#5A5A5A",
    wrap=True,
)
ui.Separator(root)

# 左右两列并排
body = ui.Row(root, spacing=28)

left = ui.Column(body, spacing=8, width=420)
right = ui.Column(body, spacing=8, width=420)


def section(parent: ui.Column, title: str) -> None:
    ui.Label(parent, title, font_size=13, weight="semibold", foreground="#0F6E56")


# ----------------------------------------------------------------------
# 左列：文本与输入
# ----------------------------------------------------------------------

section(left, "文本与输入")

ui.Label(left, "Button —— 三种外观", font_size=14)

button_row = ui.Row(left, spacing=8)
ui.Button(
    button_row,
    "普通",
    width=96,
    height=38,
    command=lambda: report("Button（普通）", "click"),
)
ui.Button(
    button_row,
    "强调色",
    width=100,
    height=38,
    variant="accent",
    command=lambda: report("Button（accent）", "click"),
)
ui.Button(
    button_row,
    "自定义色",
    width=110,
    height=38,
    background="#0F6E56",
    command=lambda: report("Button（自定义背景）", "click"),
)

ui.HyperlinkButton(
    left,
    "HyperlinkButton · 点我",
    command=lambda: report("HyperlinkButton", "click"),
)

entry = ui.Entry(left, placeholder="输入文字后按回车", width=380)
entry.bind("submit", lambda e: report("Entry", "submit（回车）", e.text))

ui.Checkbox(
    left,
    "Checkbox · 勾选我",
    checked=False,
).bind("change", lambda e: report("Checkbox", "change", f"checked={e.checked}"))

ui.Label(left, "Radio —— 同组自动互斥", font_size=14)
radio_row = ui.Row(left, spacing=20)
for _index, _label in enumerate(["甲", "乙", "丙"]):
    ui.Radio(
        radio_row,
        f"选项{_label}",
        group="demo",
        checked=(_index == 0),
    ).bind(
        "change",
        lambda e, name=_label: report(f"Radio 选项{name}", "change（被选中）"),
    )

ui.Switch(left, "Switch · 拨动我").bind(
    "change", lambda e: report("Switch", "change", f"checked={e.checked}")
)

# ----------------------------------------------------------------------
# 右列：选择与数值
# ----------------------------------------------------------------------

section(right, "选择与数值")

ui.Label(right, "Slider —— 拖动后稍等片刻会弹窗", font_size=14)

slider_value = ui.Label(right, "当前值：30", font_size=14, foreground="#185FA5")

# WinUI 的 ProgressBar 默认只有 4px 高，不显式给高度几乎看不见
progress = ui.ProgressBar(right, value=30, width=380, height=10)

# 不确定态：光条来回跑，不表示具体进度
ui.Label(right, "上面是确定态，下面是 indeterminate（不确定态）", font_size=12, foreground="#888888")
ui.ProgressBar(right, indeterminate=True, width=380, height=6)

_slider = {"armed": False, "value": 30.0}


def flush_slider() -> None:
    _slider["armed"] = False
    report("Slider", "值变化", f"{_slider['value']:.0f}")


def on_slider(event: ui.Event) -> None:
    value = float(event.value)
    _slider["value"] = value
    # 顺手联动进度条，顺便演示「事件里改界面」也是即时生效的
    progress.value = value
    slider_value.text = f"当前值：{value:.0f}"
    if not _slider["armed"]:
        _slider["armed"] = True
        win.after(600, flush_slider)


ui.Slider(right, 0, 100, 30, width=380).bind("change", on_slider)

ui.ComboBox(
    right,
    ["选项 A", "选项 B", "选项 C"],
    selected_index=0,
    width=380,
).bind("change", lambda e: report("ComboBox", "change", f"{e.index} → {e.data.get('value')}"))

ui.Label(right, "ListBox —— 列表项可带图标", font_size=14)

ui.ListBox(
    right,
    [
        {"text": "文档", "glyph": "\uE8A5"},
        {"text": "图片", "glyph": "\uEB9F"},
        {"text": "文件夹", "glyph": "\uE8B7"},
        {"text": "设置", "glyph": "\uE713"},
    ],
    height=140,
    width=380,
).bind(
    "selectionChanged",
    lambda e: report("ListBox", "selectionChanged", f"{e.index} → {e.data.get('value')}"),
)

ui.Image(right, src=str(ASSETS / "checker.png"), width=72, height=72)

ring_row = ui.Row(right, spacing=12, height=32)
ui.ProgressRing(ring_row, size=28)
ui.Label(ring_row, "ProgressRing —— 不确定态加载圈", font_size=13)

# ----------------------------------------------------------------------
# 媒体播放器
# ----------------------------------------------------------------------

ui.Separator(root)
ui.Label(root, "Media —— 视频播放器", font_size=13, weight="semibold", foreground="#0F6E56")

media = ui.Media(
    root,
    src=r"C:\Users\Administrator\Videos\202402092217.mp4",
    width=880,
    height=200,
    show_controls=True,
)
media.bind(
    "mediaOpened",
    lambda e: report("Media", "mediaOpened", f"时长 {(e.data.get('duration') or 0):.1f} 秒"),
)
media.bind("mediaEnded", lambda e: report("Media", "mediaEnded"))
media.bind(
    "mediaFailed",
    lambda e: report("Media", "mediaFailed", e.data.get("error")),
)

# ----------------------------------------------------------------------
# 底部：说明只读控件
# ----------------------------------------------------------------------

ui.Label(
    root,
    "ProgressBar / ProgressRing / Image / Separator 是只读展示控件，本身不产生交互事件。",
    font_size=12,
    foreground="#888888",
)

win.mainloop()
