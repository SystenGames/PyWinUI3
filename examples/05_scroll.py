"""滚动：整窗滚动 + 局部滚动区。

两种滚动各管一段：

- **整窗滚动**默认开启（``Window(scroll=True)``）。内容比窗口高时自动出滚动条，
  不用额外包一层。
- **局部滚动区** ``ui.Scroll`` 只在自己那块区域里滚。它必须有确定高度
  （``height=...`` 或放进 Grid 的 ``"*"`` 行），否则父容器把它测量成无限高，
  结果是内容全展开、永远不滚。

``python 05_scroll.py --auto`` 会自己跑完退出。
"""

from __future__ import annotations

import sys

import pywinui3 as ui


def main() -> None:
    win = ui.Window(
        title="滚动演示",
        width=520,
        height=430,
        x=200,
        y=120,
        min_width=360,
        min_height=280,
    )

    col = ui.Column(win, padding=20, spacing=10)

    # ---- 局部滚动区：先放在上面，方便一眼看到 ----
    ui.Label(col, "局部滚动区（height=150）", font_size=18, weight="semibold")

    box = ui.Scroll(col, height=150, spacing=6)
    for i in range(1, 16):
        ui.Label(box, f"滚动区里的第 {i} 行", h_align="stretch")

    ui.Label(col, "这块只在它自己的框里滚，外层的按钮不会动。", opacity=0.7, wrap=True)

    ui.Separator(col)

    # ---- 整窗滚动：一直排到窗口外面 ----
    ui.Label(col, "整窗滚动", font_size=18, weight="semibold")
    ui.Label(col, "下面这些按钮排到了窗口外面，滚轮或拖动滚动条就能看全。", opacity=0.7, wrap=True)

    for i in range(1, 26):
        ui.Button(col, f"第 {i} 行按钮", h_align="stretch")

    ui.Label(col, "到底了。", opacity=0.7)

    if "--auto" in sys.argv:
        win.after(6000, win.quit)

    win.mainloop()


if __name__ == "__main__":
    main()
