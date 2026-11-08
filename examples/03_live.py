"""实时渲染演示。

目的是把「实时」这件事摆到眼前，而不是靠读日志相信它：

1. 时钟、进度条、色块每 50ms 变一次，持续不断
2. 点「动态添加一行」会在运行中往界面里插入新控件（结构变化也能实时）
3. 全程没有调用任何 update() / refresh()——赋值即渲染

运行：
    python examples/03_live.py
"""

import colorsys
import time

import pywinui as ui

win = ui.Window(title="PyWinUI 实时渲染", width=780, height=620)

root = ui.Column(win, padding=28, spacing=14)

ui.Label(root, "实时渲染", font_size=26, weight="semibold")
ui.Label(
    root,
    "下面所有内容都在持续变化，代码里没有任何手动刷新。",
    font_size=13,
    foreground="#5A5A5A",
    wrap=True,
)

clock = ui.Label(root, "--:--:--", font_size=38, weight="light", foreground="#0F6E56")

ui.Separator(root)

# 进度条 + 百分比
progress_row = ui.Row(root, spacing=12)
progress = ui.ProgressBar(progress_row, value=0, width=520, height=10)
percent = ui.Label(progress_row, "0%", font_size=13, foreground="#185FA5")

# 色块：用 Button 充当（Button 支持 background，且能完整覆盖视觉状态）
swatch = ui.Button(root, "颜色随时间变化", width=260, height=54)

ui.Separator(root)

# 运行中插入新控件
added_rows = ui.Column(root, spacing=6)
added_count = {"n": 0}


def add_row() -> None:
    added_count["n"] += 1
    ui.Label(
        added_rows,
        f"第 {added_count['n']} 行 —— 这是运行中插入的控件",
        font_size=13,
        foreground="#3C3489",
    )


def clear_rows() -> None:
    for child in added_rows.children:
        child.destroy()
    added_count["n"] = 0


controls = ui.Row(root, spacing=10)
ui.Button(controls, "动态添加一行", width=150, height=36, command=add_row)
ui.Button(controls, "清空", width=90, height=36, variant="accent", command=clear_rows)

start = time.monotonic()
frames = {"n": 0, "paused": False}


def tick() -> None:
    if frames["paused"]:
        win.after(50, tick)
        return

    frames["n"] += 1
    elapsed = time.monotonic() - start

    clock.text = time.strftime("%H:%M:%S")
    clock.foreground = "#0F6E56" if int(elapsed) % 2 == 0 else "#534AB7"

    value = (frames["n"] * 2) % 101
    progress.value = value
    percent.text = f"{value}%"

    hue = (frames["n"] * 5) % 360
    r, g, b = colorsys.hsv_to_rgb(hue / 360.0, 0.55, 0.86)
    swatch.background = "#{:02X}{:02X}{:02X}".format(int(r * 255), int(g * 255), int(b * 255))

    win.after(50, tick)


tick()
win.mainloop()
