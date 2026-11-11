"""扩展控件：补齐 WinUI 3 常用控件在 Python 侧的封装。

这些类和 :mod:`pywinui3.widgets` 里的基础控件遵循完全相同的规则：
只保存属性描述，赋值即发 patch，事件用 ``bind()`` 挂，运行时值用 ``query()`` 读。

命名与 C# 宿主 ``ExtendedControls`` 的 case 一一对应。少数控件因为
WinUI 自身的限制做了简化，见 README「已知限制」。
"""

from __future__ import annotations

from typing import Any, ClassVar, Iterable

from .widgets import Widget


class _ValueWidget(Widget):
    """带一个主值的控件共用的 ``value`` / ``get()`` / ``set()``。"""

    _value_key: ClassVar[str] = "value"
    _default: ClassVar[Any] = 0
    _var_prop: ClassVar[str | None] = "value"
    _var_field: ClassVar[str | None] = "value"

    @property
    def value(self) -> Any:
        return self._props.get(self._value_key, self._default)

    @value.setter
    def value(self, value: Any) -> None:
        self.configure(**{self._value_key: value})

    def get(self) -> Any:
        return self.value

    def set(self, value: Any) -> None:
        self.value = value


class _TextWidget(_ValueWidget):
    _value_key: ClassVar[str] = "text"
    _default: ClassVar[Any] = ""
    _var_prop: ClassVar[str | None] = "text"
    _var_field: ClassVar[str | None] = "text"


class _ItemsWidget(Widget):
    """带 ``items`` 列表的控件共用选中项读取。"""

    _var_prop: ClassVar[str | None] = "selectedIndex"
    _var_field: ClassVar[str | None] = "index"

    def __init__(self, master: Any, items: Iterable[Any] | None = None, **kwargs: Any):
        if items is not None:
            kwargs.setdefault("items", list(items))
        super().__init__(master, **kwargs)

    @property
    def selected_index(self) -> int:
        """本地记住的选中下标；用户操作后由 change 事件同步。"""
        return int(self._props.get("selectedIndex", -1))

    @selected_index.setter
    def selected_index(self, index: int) -> None:
        self.configure(selected_index=index)

    @property
    def selected_item(self) -> Any:
        """向宿主读当前选中项（真实状态，不依赖本地缓存）。"""
        return self.query("selectedItem")

    @property
    def selected_items(self) -> list[Any]:
        """多选模式下所有选中项。"""
        return list(self.query("selectedItems") or [])

    @property
    def item_count(self) -> int:
        return int(self.query("itemCount") or 0)

    def scroll_to(self, index: int = 0) -> None:
        self._app._send_invoke(self._id, "scroll_to", {"index": index})


# --------------------------------------------------------------------------
# 表单输入
# --------------------------------------------------------------------------


class NumberBox(_ValueWidget):
    """带上下微调按钮的数字输入框。

    ``value`` / ``min`` / ``max`` / ``step_frequency`` 都是数值；
    ``spin`` 可取 ``compact``（默认）/ ``inline`` / ``hidden``。
    """

    _type = "NumberBox"

    def __init__(
        self,
        master: Any,
        value: float = 0,
        *,
        min: float = -float("inf"),
        max: float = float("inf"),
        step_frequency: float = 1,
        **kwargs: Any,
    ):
        # ±inf 不能进 JSON，只在用户真的给了有限值时才下发
        props: dict[str, Any] = {"value": value, "step_frequency": step_frequency}
        if min != -float("inf"):
            props["min"] = min
        if max != float("inf"):
            props["max"] = max
        props.update(kwargs)
        super().__init__(master, **props)


class PasswordBox(_TextWidget):
    """密码输入框（比 ``Entry(password=True)`` 更直白的类型名）。"""

    _type = "PasswordBox"

    def __init__(self, master: Any, text: str = "", **kwargs: Any):
        super().__init__(master, text=text, **kwargs)


class RichTextBlock(_TextWidget):
    """只读富文本块。当前协议只支持纯文本，富文本 run 见已知限制。"""

    _type = "RichTextBlock"

    def __init__(self, master: Any, text: str = "", **kwargs: Any):
        super().__init__(master, text=text, wrap=True, **kwargs)


class RichEditBox(_TextWidget):
    """多行可编辑文本框。"""

    _type = "RichEditBox"

    def __init__(self, master: Any, text: str = "", **kwargs: Any):
        kwargs.setdefault("height", 120)
        super().__init__(master, text=text, **kwargs)


class AutoSuggestBox(_TextWidget):
    """带建议列表的搜索框。

    ``items`` 是候选列表；``query_submitted`` / ``suggestion_chosen`` 事件
    分别对应回车提交和选中建议，``submit`` 也可以用。
    """

    _type = "AutoSuggestBox"

    def __init__(
        self,
        master: Any,
        text: str = "",
        *,
        items: Iterable[str] | None = None,
        **kwargs: Any,
    ):
        if items is not None:
            kwargs.setdefault("items", list(items))
        super().__init__(master, text=text, **kwargs)


class DatePicker(_ValueWidget):
    """日期选择。``date`` 用 ``YYYY-MM-DD`` 字符串。"""

    _type = "DatePicker"
    _value_key = "date"
    _default = ""
    _var_prop = "date"
    _var_field = "date"

    def __init__(self, master: Any, date: str = "", **kwargs: Any):
        super().__init__(master, date=date, **kwargs)


class TimePicker(_ValueWidget):
    """时间选择。``time`` 用 ``HH:MM`` 字符串。"""

    _type = "TimePicker"
    _value_key = "time"
    _default = ""
    _var_prop = "time"
    _var_field = "time"

    def __init__(self, master: Any, time: str = "12:00", **kwargs: Any):
        super().__init__(master, time=time, **kwargs)


class CalendarDatePicker(DatePicker):
    """下拉日历式的日期选择。"""

    _type = "CalendarDatePicker"


class CalendarView(DatePicker):
    """整月日历。``selection_mode`` 可取 ``single`` / ``multiple`` / ``none``。"""

    _type = "CalendarView"

    def __init__(self, master: Any, date: str = "", **kwargs: Any):
        kwargs.setdefault("height", 320)
        super().__init__(master, date=date, **kwargs)


class RatingControl(_ValueWidget):
    """星级评分，``value`` 默认 0，``max_rating`` 默认 5。"""

    _type = "RatingControl"

    def __init__(self, master: Any, value: float = 0, **kwargs: Any):
        super().__init__(master, value=value, **kwargs)


class ColorPicker(_ValueWidget):
    """颜色选择器。``color`` 用 ``#AARRGGBB`` 或 ``#RRGGBB``。"""

    _type = "ColorPicker"
    _value_key = "color"
    _default = "#FFFFFFFF"
    _var_prop = "color"
    _var_field = "color"

    def __init__(self, master: Any, color: str = "#FFFFFFFF", **kwargs: Any):
        super().__init__(master, color=color, **kwargs)


class DropDownButton(Widget):
    """点击弹出菜单的按钮；菜单项用 ``set_menu()`` 设置，点击回 ``menu`` 事件。"""

    _type = "DropDownButton"

    def __init__(self, master: Any, text: str = "选择", **kwargs: Any):
        super().__init__(master, text=text, **kwargs)


class ToggleSplitButton(Widget):
    """带开关状态的分段按钮。"""

    _type = "ToggleSplitButton"
    _var_prop = "checked"
    _var_field = "checked"

    def __init__(self, master: Any, text: str = "选项", checked: bool = False, **kwargs: Any):
        super().__init__(master, text=text, checked=checked, **kwargs)

    @property
    def value(self) -> bool:
        return bool(self._props.get("checked", False))

    @value.setter
    def value(self, checked: bool) -> None:
        self.configure(checked=checked)


class PersonPicture(Widget):
    """头像。``display_name`` 显示姓名，``initials`` 直接指定首字母。"""

    _type = "PersonPicture"

    def __init__(self, master: Any, display_name: str = "", **kwargs: Any):
        super().__init__(master, display_name=display_name, **kwargs)


# --------------------------------------------------------------------------
# 数据 / 导航
# --------------------------------------------------------------------------


class GridView(_ItemsWidget):
    """网格列表。``selection_mode`` 支持 ``single`` / ``multiple`` / ``extended``。"""

    _type = "GridView"

    def __init__(self, master: Any, items: Iterable[Any] | None = None, **kwargs: Any):
        kwargs.setdefault("height", 200)
        super().__init__(master, items, **kwargs)


class FlipView(_ItemsWidget):
    """一次显示一项、可左右翻页的列表。"""

    _type = "FlipView"

    def __init__(self, master: Any, items: Iterable[Any] | None = None, **kwargs: Any):
        kwargs.setdefault("height", 200)
        super().__init__(master, items, **kwargs)


class Pivot(_ItemsWidget):
    """顶部标签切换。"""

    _type = "Pivot"

    def __init__(self, master: Any, items: Iterable[Any] | None = None, **kwargs: Any):
        kwargs.setdefault("height", 200)
        super().__init__(master, items, **kwargs)


class TabView(_ItemsWidget):
    """可关闭的标签页容器；关闭请求回 ``tabCloseRequested`` 事件。"""

    _type = "TabView"

    def __init__(self, master: Any, items: Iterable[Any] | None = None, **kwargs: Any):
        kwargs.setdefault("height", 200)
        super().__init__(master, items, **kwargs)


class TreeView(_ItemsWidget):
    """树。``items`` 里可以放 ``{"label": "父", "children": [...]}`` 表示层级。"""

    _type = "TreeView"

    def __init__(self, master: Any, items: Iterable[Any] | None = None, **kwargs: Any):
        kwargs.setdefault("height", 220)
        super().__init__(master, items, **kwargs)


class NavigationView(_ItemsWidget):
    """WinUI 原生导航侧栏（和用 ListBox 模拟的自动收缩侧栏不是一回事）。

    ``pane_display_mode`` 可取 ``left`` / ``top`` / ``compact`` / ``minimal``。
    子控件会放进右侧内容区。
    """

    _type = "NavigationView"

    def __init__(self, master: Any, items: Iterable[Any] | None = None, **kwargs: Any):
        kwargs.setdefault("height", 320)
        super().__init__(master, items, **kwargs)


class MenuBar(_ItemsWidget):
    """顶部菜单栏，点击项回 ``click`` 事件，``e.data["index"]`` 是下标。"""

    _type = "MenuBar"

    def __init__(self, master: Any, items: Iterable[Any] | None = None, **kwargs: Any):
        super().__init__(master, items, **kwargs)


class CommandBar(_ItemsWidget):
    """命令栏，点击回 ``click`` 事件。"""

    _type = "CommandBar"

    def __init__(self, master: Any, items: Iterable[Any] | None = None, **kwargs: Any):
        super().__init__(master, items, **kwargs)


class BreadcrumbBar(_ItemsWidget):
    """面包屑导航，点击回 ``itemClick`` 事件。"""

    _type = "BreadcrumbBar"

    def __init__(self, master: Any, items: Iterable[Any] | None = None, **kwargs: Any):
        super().__init__(master, items, **kwargs)


class ItemsView(_ItemsWidget):
    """WinUI 1.8 的现代化集合视图（列表/网格统一控件）。"""

    _type = "ItemsView"

    def __init__(self, master: Any, items: Iterable[Any] | None = None, **kwargs: Any):
        kwargs.setdefault("height", 200)
        super().__init__(master, items, **kwargs)


class RadioButtons(_ItemsWidget):
    """成组单选按钮。"""

    _type = "RadioButtons"


class SelectorBar(_ItemsWidget):
    """WinUI 1.8 的选择条。"""

    _type = "SelectorBar"


# --------------------------------------------------------------------------
# 信息 / 容器
# --------------------------------------------------------------------------


class InfoBar(Widget):
    """应用内提示条。``severity`` 可取 ``info`` / ``success`` / ``warning`` / ``error``。"""

    _type = "InfoBar"

    def __init__(
        self,
        master: Any,
        title: str = "",
        message: str = "",
        *,
        severity: str = "info",
        **kwargs: Any,
    ):
        super().__init__(master, title=title, message=message, severity=severity, **kwargs)

    def show(self) -> None:
        self._app._send_invoke(self._id, "show")

    def hide(self) -> None:
        self._app._send_invoke(self._id, "hide")


class Expander(Widget):
    """可折叠容器，子控件放在 Content 里。"""

    _type = "Expander"
    _var_prop = "isExpanded"
    _var_field = "expanded"

    def __init__(self, master: Any, header: str = "", is_expanded: bool = False, **kwargs: Any):
        super().__init__(master, header=header, is_expanded=is_expanded, **kwargs)

    @property
    def value(self) -> bool:
        return bool(self._props.get("isExpanded", False))

    @value.setter
    def value(self, expanded: bool) -> None:
        self.configure(is_expanded=expanded)

    def show(self) -> None:
        self._app._send_invoke(self._id, "show")

    def hide(self) -> None:
        self._app._send_invoke(self._id, "hide")


class BorderBox(Widget):
    """带背景/边框的通用容器，可以放子控件。"""

    _type = "BorderBox"


class Viewbox(Widget):
    """把子内容等比缩放填满可用空间。"""

    _type = "Viewbox"


class RelativePanel(Widget):
    """相对布局面板。当前协议只开放其容器能力，未暴露附加对齐属性。"""

    _type = "RelativePanel"

    def __init__(self, master: Any, **kwargs: Any):
        kwargs.setdefault("height", 160)
        super().__init__(master, **kwargs)


class TwoPaneView(Widget):
    """双栏容器，子控件进 Pane1；``pane_priority`` 可切到 Pane2。"""

    _type = "TwoPaneView"

    def __init__(self, master: Any, **kwargs: Any):
        kwargs.setdefault("height", 220)
        super().__init__(master, **kwargs)


class RefreshContainer(Widget):
    """下拉刷新容器，触发 ``refreshRequested`` 事件。"""

    _type = "RefreshContainer"

    def __init__(self, master: Any, **kwargs: Any):
        kwargs.setdefault("height", 200)
        super().__init__(master, **kwargs)


__all__ = [
    "NumberBox",
    "PasswordBox",
    "RichTextBlock",
    "RichEditBox",
    "AutoSuggestBox",
    "DatePicker",
    "TimePicker",
    "CalendarDatePicker",
    "CalendarView",
    "RatingControl",
    "ColorPicker",
    "DropDownButton",
    "ToggleSplitButton",
    "PersonPicture",
    "GridView",
    "FlipView",
    "Pivot",
    "TabView",
    "TreeView",
    "NavigationView",
    "MenuBar",
    "CommandBar",
    "BreadcrumbBar",
    "ItemsView",
    "RadioButtons",
    "SelectorBar",
    "InfoBar",
    "Expander",
    "BorderBox",
    "Viewbox",
    "RelativePanel",
    "TwoPaneView",
    "RefreshContainer",
]
