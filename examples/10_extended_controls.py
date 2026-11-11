"""扩展控件总览 + query 读回 + 新事件面。

跑法::

    python examples/10_extended_controls.py

自动化冒烟/截图时可以设环境变量让窗口自己关::

    set PYWINUI3_DEMO_AUTOQUIT=6000 && python examples/10_extended_controls.py
"""

from __future__ import annotations

import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))

import pywinui3 as ui


def main() -> None:
    win = ui.Window(title="扩展控件总览", width=1000, height=760)

    info = ui.InfoBar(
        win,
        title="扩展控件",
        message="这一屏是 WinUI 3 内置控件 + query/result 读回 + 键盘/焦点/窗口状态事件",
        severity="info",
    )
    info.bind("closeButtonClick", lambda e: None)

    status = ui.Label(win, "操作任意控件，或点「读一次真实状态」", wrap=True, opacity=0.8)
    ui.Separator(win)

    def note(text: str) -> None:
        status.text = text

    columns = ui.Row(win, spacing=16)
    left = ui.Column(columns, spacing=8, width=470)
    right = ui.Column(columns, spacing=8, width=470)

    # ---------------- 左列：表单输入 ----------------
    ui.Label(left, "表单输入", font_size=18, weight="semibold")

    number = ui.NumberBox(left, value=42, min=0, max=100, step_frequency=2, header="数量")
    number.bind("change", lambda e: note(f"NumberBox → {e.data.get('value')}"))

    ui.DatePicker(left, date="2026-11-11", header="日期")
    ui.TimePicker(left, time="14:30", header="时间")
    ui.CalendarDatePicker(left, date="2026-11-11", header="下拉日历")

    rating = ui.RatingControl(left, value=4)
    rating.bind("valueChanged", lambda e: note(f"评分 → {e.data.get('value')}"))

    picker = ui.ColorPicker(left, color="#FF0078D4", height=260)
    picker.bind("colorChanged", lambda e: note(f"颜色 → {e.data.get('color')}"))

    suggest = ui.AutoSuggestBox(
        left, items=["Alpha", "Beta", "Gamma", "Delta"], placeholder="输入后按回车"
    )
    suggest.bind("querySubmitted", lambda e: note(f"搜索提交 → {e.data.get('text')}"))
    suggest.on_key_down(lambda e: note(f"按键 → {e.data.get('key')}"))
    suggest.on_got_focus(lambda e: note("搜索框获得焦点"))
    suggest.on_loaded(lambda e: note("搜索框已完成布局"))

    ui.RichTextBlock(left, "RichTextBlock：只读富文本（当前为纯文本模式）")
    ui.RichEditBox(left, "RichEditBox：可以直接改这段文字", height=90)
    ui.PasswordBox(left, text="", placeholder="密码框（change / submit 均已接通）")

    exp = ui.Expander(left, header="高级选项", is_expanded=False)
    ui.Checkbox(exp, "启用实验特性")
    exp.bind("expanding", lambda e: note("高级选项展开"))
    exp.bind("collapsed", lambda e: note("高级选项收起"))

    box = ui.BorderBox(left, background="#FFF2F2F2", padding=12, corner_radius=6)
    ui.Label(box, "BorderBox：带背景和圆角的通用容器")

    # ---------------- 右列：数据 / 导航 / 信息 ----------------
    ui.Label(right, "数据与导航", font_size=18, weight="semibold")

    grid_view = ui.GridView(
        right,
        items=["一", "二", "三", "四", "五"],
        selection_mode="multiple",
        height=150,
    )
    grid_view.bind("itemClick", lambda e: note(f"网格点击 → {e.data.get('value')}"))

    tree = ui.TreeView(
        right,
        items=[{"label": "项目", "children": ["src", {"label": "docs", "children": ["协议"]}]}],
        height=170,
    )
    tree.bind("itemClick", lambda e: note(f"树节点 → {e.data.get('value')}"))

    ui.TabView(right, items=["概览", "详情", "日志"], height=110)
    ui.Pivot(right, items=["第一页", "第二页"], height=110)
    ui.BreadcrumbBar(right, items=["根目录", "子目录", "当前"])
    ui.RadioButtons(right, items=["选项 A", "选项 B", "选项 C"])
    ui.SelectorBar(right, items=["全部", "未完成", "已完成"])

    menu = ui.MenuBar(right, items=["文件", "编辑", "帮助"])
    menu.bind("click", lambda e: note(f"菜单点击 → {e.data.get('value')}"))

    commands = ui.CommandBar(right, items=["新建", "保存", "删除"])
    commands.bind("click", lambda e: note(f"命令点击 → {e.data.get('value')}"))

    ui.PersonPicture(right, display_name="Ada Lovelace")

    hint = ui.InfoBar(right, title="提示", message="这条可以用下面的按钮隐藏", severity="success")
    ui.Button(right, "隐藏提示", command=hint.hide)
    ui.Button(right, "显示提示", command=hint.show)

    # ---------------- 底部：query 读回 ----------------
    ui.Separator(win)

    def read_state() -> None:
        lines = [
            f"窗口状态 {win.state} / 激活 {win.is_active}",
            f"NumberBox 真实宽度 {number.actual_width:.0f} × {number.actual_height:.0f}",
            f"GridView 选中 {grid_view.selected_items} / 项数 {grid_view.item_count}",
            f"NumberBox 值 {number.query('value')} / 类型 {number.control_type}",
            f"DPI 缩放 {number.dpi_scale}",
        ]
        note(" | ".join(lines))

    ui.Button(win, "读一次真实状态", command=read_state)

    win.on_state_changed(lambda e: note(f"窗口状态变化 → {e.data.get('state')}"))
    win.on_activate(lambda e: note(f"窗口焦点 → {e.data.get('active')}"))

    auto_quit = os.environ.get("PYWINUI3_DEMO_AUTOQUIT")
    if auto_quit:
        win.after(int(auto_quit), win.quit)

    win.mainloop()


if __name__ == "__main__":
    main()
