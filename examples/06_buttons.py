"""三种按钮：ToggleButton / SplitButton / RepeatButton。

- **ToggleButton**：按下去保持按下，再按弹回。工具栏式的模式开关。
- **SplitButton**：左半边执行默认动作，右半边箭头弹菜单。
- **RepeatButton**：按住不放会连续触发，常见于数值增减。

跑起来可以试：点「加粗」再点一次、点「保存」左半边、点右边的箭头选菜单、
按住 + / − 不放。

``python 06_buttons.py --auto`` 会自己跑完退出。
"""

from __future__ import annotations

import sys

import pywinui as ui


def main() -> None:
    win = ui.Window(title="三种按钮", width=560, height=470, x=240, y=140)

    col = ui.Column(win, padding=20, spacing=12)

    # ---- ToggleButton ----
    ui.Label(col, "ToggleButton（按下保持）", font_size=18, weight="semibold")

    toolbar = ui.Row(col, spacing=8)
    bold = ui.ToggleButton(toolbar, "加粗", icon="\uE8DD")
    italic = ui.ToggleButton(toolbar, "斜体", icon="\uE8DB")
    images_only = ui.ToggleButton(toolbar, "只看图", checked=True)

    state = ui.Label(col, "", opacity=0.7)

    def refresh(_event=None) -> None:
        state.set_text(
            f"加粗 {'开' if bold.value else '关'} · "
            f"斜体 {'开' if italic.value else '关'} · "
            f"只看图 {'开' if images_only.value else '关'}"
        )

    for button in (bold, italic, images_only):
        button.bind("change", refresh)
    refresh()

    ui.Separator(col)

    # ---- SplitButton ----
    ui.Label(col, "SplitButton（左执行 · 右弹菜单）", font_size=18, weight="semibold")

    save = ui.SplitButton(
        col,
        "保存",
        icon="\uE74E",
        items=[
            {"label": "另存为…", "glyph": "\uE792", "value": "save-as"},
            {"label": "导出为 PDF", "glyph": "\uE8A5", "value": "export-pdf"},
            "-",
            {"label": "导出为图片", "value": "export-image"},
        ],
        h_align="left",
    )
    save.bind("click", lambda e: print("[click] 默认动作：保存", flush=True))
    save.on_menu(lambda e: print(f"[menu] {e.data['value']}", flush=True))

    ui.Separator(col)

    # ---- RepeatButton ----
    ui.Label(col, "RepeatButton（按住连发）", font_size=18, weight="semibold")

    stepper = ui.Row(col, spacing=10)
    count = ui.Label(stepper, "0", font_size=26, weight="semibold", width=64, text_align="center")
    minus = ui.RepeatButton(stepper, "−", width=46, repeat_interval=80)
    plus = ui.RepeatButton(stepper, "+", width=46, repeat_interval=80)

    value = {"n": 0}

    def bump(delta: int) -> None:
        value["n"] += delta
        count.set_text(str(value["n"]))

    minus.bind("click", lambda e: bump(-1))
    plus.bind("click", lambda e: bump(1))

    ui.Label(col, "按住 + / − 不放，数字会连续变化。", opacity=0.6, wrap=True)

    if "--auto" in sys.argv:
        win.after(6000, win.quit)

    win.mainloop()


if __name__ == "__main__":
    main()
