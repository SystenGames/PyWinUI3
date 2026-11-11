"""布局容器。

WinUI 没有 tkinter 的 pack/place 几何管理器，所以这里给出三套显式容器：

- ``Column`` —— 垂直堆叠（``StackPanel``），最常用
- ``Row``    —— 水平堆叠
- ``Grid``   —— 二维网格，子控件用 ``.grid(row=, column=)`` 定位

容器内部顺序就是创建顺序，这一点和 ``pack(side="top")`` 的心智一致。
"""

from __future__ import annotations

from typing import Any

from .widgets import Widget


class Column(Widget):
    """垂直堆叠容器。"""

    _type = "Column"

    def __init__(self, master: Any, spacing: int = 0, **kwargs: Any):
        super().__init__(master, spacing=spacing, **kwargs)


class Row(Widget):
    """水平堆叠容器。"""

    _type = "Row"

    def __init__(self, master: Any, spacing: int = 0, **kwargs: Any):
        super().__init__(master, spacing=spacing, **kwargs)


class Frame(Column):
    """tkinter 兼容别名。默认垂直堆叠，想要横向请用 ``Row``。"""


class Scroll(Widget):
    """可滚动容器（纵向）。

    子节点按顺序垂直排列，超出可视高度时可以滚轮或拖动滚动条。

    **它必须有一个有界高度**：要么写 ``height=...``，要么放进 ``Grid`` 的
    ``"*"`` 行里。放在 ``Column`` / ``Row`` 里又不给高度的话，父容器会把它
    测量成无限高，结果就是「内容全展开、永远不滚」——这是 WinUI 的测量机制
    决定的，不是 bug。

    ::

        box = ui.Scroll(col, height=200, spacing=4)
        for line in lines:
            ui.Label(box, line)

    只想让整个窗口能滚的话不需要它：``ui.Window`` 默认就开了滚动。
    """

    _type = "Scroll"

    def __init__(self, master: Any, spacing: int = 0, **kwargs: Any):
        super().__init__(master, spacing=spacing, **kwargs)

    @property
    def vertical_offset(self) -> float:
        """当前滚动位置（逻辑像素）。"""
        return float(self.query("verticalOffset") or 0)

    @property
    def viewport_height(self) -> float:
        return float(self.query("viewportHeight") or 0)

    @property
    def extent_height(self) -> float:
        return float(self.query("extentHeight") or 0)

    def set_scroll_bars(self, vertical: str = "auto", horizontal: str = "disabled") -> None:
        """控制滚动条显隐：``auto`` / ``visible`` / ``hidden`` / ``disabled``。"""
        self.configure(
            vertical_scroll_bar_visibility=vertical,
            horizontal_scroll_bar_visibility=horizontal,
        )


class Grid(Widget):
    """二维网格容器。

    ``rows`` / ``columns`` 接受三种值：

    =============  =========================================
    ``"auto"``     按内容自适应
    ``"*"``        占用剩余空间的等分（``"2*"`` 为两倍权重）
    ``数字``        固定像素
    =============  =========================================

    例::

        grid = Grid(win, rows=["auto", "*"], columns=["*", "*"], row_gap=8)
        Label(grid, "姓名", grid=(0, 0))
        Entry(grid, grid=(0, 1))
    """

    _type = "Grid"

    def __init__(
        self,
        master: Any,
        rows: list[Any] | None = None,
        columns: list[Any] | None = None,
        **kwargs: Any,
    ):
        if rows is not None:
            kwargs["rows"] = rows
        if columns is not None:
            kwargs["columns"] = columns
        super().__init__(master, **kwargs)
