"""自动收缩侧栏。

鼠标移到左边窄条上 → 展开；点一个导航项后把鼠标移开 → 保持展开；
点到右边内容区 → 收起。展开/收起是 20 步指数缓出动画
（``w += (end_w - w) / 3``，最后一步 snap 到目标值）。
"""

from __future__ import annotations

import sys

import pywinui as ui


def main() -> None:
    win = ui.Window(
        title="自动收缩侧栏",
        width=900,
        height=560,
        layout="grid",
        scroll=False,
        rows=["*"],
        columns=["auto", "*"],
        x=160,
        y=120,
    )

    nav = ui.ListBox(
        win,
        [
            {"text": "概览", "glyph": "\uE80F"},
            {"text": "任务", "glyph": "\uE9D5"},
            {"text": "文件", "glyph": "\uE8B7"},
            {"text": "设置", "glyph": "\uE713"},
        ],
        width=200,
        auto_collapse=True,
        collapsed_width=56,
        background="#F3F3F3",
    )
    nav.grid(row=0, column=0)
    nav.selected_index = 0

    body = ui.Scroll(win, padding=24, spacing=10)
    body.grid(row=0, column=1)

    title = ui.Label(body, "概览", font_size=28, weight="semibold")
    ui.Label(
        body,
        "把鼠标移到左边窄条上，它会展开；点一个导航项后再把鼠标移开，"
        "它会保持展开；点到这块内容区，它才收起。",
        opacity=0.75,
        wrap=True,
    )
    for i in range(1, 31):
        ui.Label(body, f"内容第 {i} 行")

    nav.bind("change", lambda e: title.set_text(str(e.data.get("value") or "内容区")))
    body.bind("pointerPressed", lambda e: nav.release())

    if "--auto" in sys.argv:
        win.after(5000, win.quit)

    win.mainloop()


if __name__ == "__main__":
    main()
