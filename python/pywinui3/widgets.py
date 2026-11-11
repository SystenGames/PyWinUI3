"""控件基类与基础控件。

设计要点：

1. **控件只持有描述，不持有 UI 对象**。真正的实现活在 C# 宿主里，
   Python 侧保存的是「我长什么样」的属性字典，随时可以序列化成节点声明。

2. **属性赋值即渲染**。``w.text = "hi"`` 会立刻生成一条 patch 发给宿主，
   不需要显式调用 update()。这符合 tkinter 的心智，也是「实时渲染」的含义。

3. **命名风格转换集中在一处**。对外是 Python 的 snake_case，对内是协议的
   camelCase，只在 ``_to_props`` 里转一次，不在各处散落字符串拼接。
"""

from __future__ import annotations

import re
from typing import Any, Callable, ClassVar

from . import protocol

_SNAKE_CASE = re.compile(r"_([a-z])")

#: 宽度动画的默认步数与间隔。20 × 16ms ≈ 320ms，快起慢收。
ANIM_STEPS = 20
ANIM_INTERVAL = 16



def camelize(name: str) -> str:
    """``font_size`` -> ``fontSize``。已经是驼峰的串原样返回。"""
    return _SNAKE_CASE.sub(lambda m: m.group(1).upper(), name)


def normalize_value(value: Any) -> Any:
    """把 Python 值转成协议可接受的形态。

    只做协议层关心的规范化，不做类型校验——校验交给宿主，
    这样新增属性不需要在 Python 侧同步维护白名单。
    """
    if isinstance(value, tuple):
        return list(value)
    if isinstance(value, list):
        return [normalize_value(v) for v in value]
    if isinstance(value, dict):
        return {k: normalize_value(v) for k, v in value.items()}
    if isinstance(value, bool):
        return value
    return value


def build_props(kwargs: dict[str, Any]) -> dict[str, Any]:
    return {camelize(k): normalize_value(v) for k, v in kwargs.items()}


class Widget:
    """所有控件的基类。"""

    #: 协议里的控件类型名，子类必须覆盖。
    _type: ClassVar[str] = "Unknown"

    #: 变量绑定时「写入控件」用的属性名；None 表示该控件不支持变量。
    _var_prop: ClassVar[str | None] = None

    #: 宿主回传 change 事件时「读取值」用的字段名。
    _var_field: ClassVar[str | None] = None

    def __init__(self, master: Any, **kwargs: Any):
        self._app = master._app
        self._id: str = self._app._alloc_node_id()
        self._parent: Any = master
        self._children: list[Widget] = []
        self._props: dict[str, Any] = {}
        self._events: dict[str, Callable[..., Any]] = {}
        self._handler_ids: dict[str, str] = {}
        self._destroyed = False
        self._variable: Any = None

        # command 是 tkinter 的惯用写法，映射到 click 事件
        command = kwargs.pop("command", None)
        variable = kwargs.pop("textvariable", None)
        if variable is None:
            variable = kwargs.pop("variable", None)

        if kwargs:
            self._props.update(build_props(kwargs))

        self._app._register_node(self)
        master._adopt(self)

        if variable is not None:
            self._bind_variable(variable)
        if command is not None:
            # command 保持无参语义，与 tkinter 一致
            self.bind("click", lambda _event=None: command())

    # ---- 树结构 ----

    def _adopt(self, child: Widget) -> None:
        """把子控件收进自己名下。容器类会覆盖以改变布局行为。"""
        self._children.append(child)
        self._app._on_node_added(self, child)

    def _collect_specs(self, out: list[dict[str, Any]]) -> None:
        """递归收集自身与所有子孙的节点声明。"""
        spec = protocol.node(
            self._id,
            self._type,
            self._props or None,
            [c._id for c in self._children] or None,
            self._handler_ids or None,
        )
        out.append(spec)
        for child in self._children:
            child._collect_specs(out)

    def _collect_insert_ops(self, parent_id: str, index: int, out: list[dict[str, Any]]) -> None:
        """生成 insert 操作序列。

        顺序很关键：必须先插父再插子，否则子节点找不到容器。
        所以这里用前序遍历，每个节点插到它真实的父容器里。
        """
        spec = protocol.node(
            self._id,
            self._type,
            self._props or None,
            None,
            self._handler_ids or None,
        )
        out.append(protocol.op_insert(parent_id, index, spec))
        for child_index, child in enumerate(self._children):
            child._collect_insert_ops(self._id, child_index, out)

    # ---- 变量绑定 ----

    def _bind_variable(self, variable: Any) -> None:
        if self._var_prop is None:
            raise TypeError(
                f"{type(self).__name__} 不支持变量绑定"
                f"（只有 Entry / Checkbox / Switch / Slider 支持）"
            )
        self._variable = variable
        variable._attach(self)

    def _apply_variable(self, variable: Any, value: Any) -> None:
        """变量被 set 时，把值写进控件属性。"""
        prop = self._var_prop
        if prop is None:
            return
        key = camelize(prop)
        normalized = normalize_value(value)
        self._props[key] = normalized
        self._app._send_patch([protocol.op_set(self._id, {key: normalized})])

    def _sync_from_event(self, event: Any) -> None:
        """宿主回报变化时，把值同步回控件属性与变量。

        **即使没绑变量也要更新 ``_props``**：``bold.value`` / ``entry.get()``
        这类读法都依赖它。只更新变量的话，没绑变量的控件在用户操作后
        读到的还是旧值——按钮看着是按下状态，程序里却以为没按。
        """
        if self._var_field is None or self._var_field not in event.data:
            return

        value = event.data[self._var_field]

        if self._var_prop is not None:
            self._props[camelize(self._var_prop)] = value

        if self._variable is not None:
            # echo 事件只同步值，不触发 trace，否则会和程序侧的 set 形成回环
            self._variable._on_user_input(value, fire_trace=not event.echo)

    def _on_event(self, event: Any) -> None:
        """内部事件钩子。

        子类覆盖它做「与用户回调无关的副作用」，例如侧栏选中后把自己钉住。
        和 ``bind`` 分开是因为 bind 一个事件只有一个 handler，
        内部逻辑占了位置，用户就没法再绑同一个事件了。
        """

    # ---- 属性 ----

    def configure(self, **kwargs: Any) -> None:
        """批量设置属性，合并成一条 patch 发出。"""
        if not kwargs:
            return
        props = build_props(kwargs)
        self._props.update(props)
        self._app._send_patch([protocol.op_set(self._id, props)])

    config = configure

    def __setattr__(self, name: str, value: Any) -> None:
        # 下划线开头的内部属性走默认路径
        if name.startswith("_"):
            object.__setattr__(self, name, value)
            return
        # property 这类 data descriptor 必须优先，否则 Entry.value = x 会被
        # 误当成「设置协议属性 value」而不是走 value 的 setter
        descriptor = getattr(type(self), name, None)
        if hasattr(descriptor, "__set__"):
            descriptor.__set__(self, value)
            return
        self.configure(**{name: value})

    def __getattr__(self, name: str) -> Any:
        # 只在正常查找失败后触发，避免遮蔽真实的方法
        if name.startswith("_"):
            raise AttributeError(name)
        props = self.__dict__.get("_props", {})
        key = camelize(name)
        if key in props:
            return props[key]
        raise AttributeError(f"{type(self).__name__} 没有属性 {name}")

    # ---- 事件 ----

    def bind(self, event: str, handler: Callable[..., Any]) -> str:
        """绑定事件，返回 handler id。

        重复绑定同一事件会覆盖旧回调，与 tkinter 的 bind 行为一致。
        """
        handler_id = self._app._register_handler(handler)
        previous = self._handler_ids.get(event)
        if previous:
            self._app._unregister_handler(previous)
        self._handler_ids[event] = handler_id
        self._events[event] = handler

        self._app._send_patch([protocol.op_set(self._id, events=dict(self._handler_ids))])
        return handler_id

    # ---- 常用事件的语义化快捷绑定 ----

    def on_key_down(self, handler: Callable[..., Any]) -> str:
        """按键按下；``e.data`` 含 ``key`` / ``keyCode`` / ``ctrl`` / ``shift`` / ``alt``。"""
        return self.bind("keyDown", handler)

    def on_key_up(self, handler: Callable[..., Any]) -> str:
        return self.bind("keyUp", handler)

    def on_loaded(self, handler: Callable[..., Any]) -> str:
        """控件进入可视树、完成首次布局；``e.data`` 含真实宽高。"""
        return self.bind("loaded", handler)

    def on_unloaded(self, handler: Callable[..., Any]) -> str:
        return self.bind("unloaded", handler)

    def on_size_changed(self, handler: Callable[..., Any]) -> str:
        """控件自身尺寸变化；``e.data`` 含 ``width`` / ``height``。"""
        return self.bind("sizeChanged", handler)

    def on_got_focus(self, handler: Callable[..., Any]) -> str:
        return self.bind("gotFocus", handler)

    def on_lost_focus(self, handler: Callable[..., Any]) -> str:
        return self.bind("lostFocus", handler)

    def on_drop(self, handler: Callable[..., Any]) -> str:
        """拖放落下；配合 ``enable_drop()`` 使用。"""
        return self.bind("drop", handler)

    def enable_drop(self, enabled: bool = True) -> None:
        self.configure(allow_drop=bool(enabled))

    def set_name(self, name: str) -> None:
        """给 WinUI 元素设 ``Name``（UI 自动化 / 调试用，不影响协议 id）。"""
        self.configure(name=name)

    def unbind(self, event: str) -> None:
        handler_id = self._handler_ids.pop(event, None)
        self._events.pop(event, None)
        if handler_id:
            self._app._unregister_handler(handler_id)
            self._app._send_patch([protocol.op_set(self._id, events=dict(self._handler_ids))])

    # ---- 布局糖（tkinter 兼容面） ----

    def pack(self, padx: int = 0, pady: int = 0, fill: str | None = None, **_: Any) -> "Widget":
        """tkinter 风格的 pack。

        只做语义子集：父容器已经是垂直/水平堆叠，所以 ``side`` 不需要参与计算，
        这里只把间距与填充意图翻译成 margin 和对齐。**返回自身**，支持链式调用。
        """
        patch: dict[str, Any] = {}
        if padx or pady:
            patch["margin"] = [padx, pady, padx, pady]
        if fill == "x":
            patch["h_align"] = "stretch"
        elif fill == "y":
            patch["v_align"] = "stretch"
        elif fill == "both":
            patch["h_align"] = "stretch"
            patch["v_align"] = "stretch"
        if patch:
            self.configure(**patch)
        return self

    def grid(
        self,
        row: int = 0,
        column: int = 0,
        rowspan: int = 1,
        colspan: int = 1,
        sticky: str = "",
        padx: int = 0,
        pady: int = 0,
        **_: Any,
    ) -> "Widget":
        """把自身放进父 Grid 的指定格。父容器必须是 Grid。"""
        from .layout import Grid

        parent = self._parent
        # Window 在 layout="grid" 时根节点本身就是 Grid，但它不是 Grid 的实例
        is_grid = isinstance(parent, Grid) or getattr(parent, "_type", None) == "Grid"

        if not is_grid:
            raise TypeError(
                f"{type(self).__name__}.grid() 需要父容器是 Grid，"
                f"当前父容器是 {type(parent).__name__}。"
                f"请改用 Column/Row，或先创建一个 Grid"
                f"（也可以用 Window(layout=\"grid\") 让窗口根节点就是 Grid）。"
            )
        patch: dict[str, Any] = {"row": row, "column": column}
        if rowspan != 1:
            patch["row_span"] = rowspan
        if colspan != 1:
            patch["column_span"] = colspan
        patch.update(_sticky_to_align(sticky))
        if padx or pady:
            patch["margin"] = [padx, pady, padx, pady]
        self.configure(**patch)
        return self

    def place(self, **_: Any) -> "Widget":
        raise NotImplementedError("WinUI 没有绝对定位布局，请使用 Column / Row / Grid")

    # ---- 杂项 ----

    def focus(self) -> None:
        self._app._send_invoke(self._id, "focus")

    # ---- 运行时读取（协议 query/result） ----

    def query(self, key: str, *, timeout: float = 2.0, args: dict[str, Any] | None = None) -> Any:
        """向宿主读一个运行时值，见 ``App.query``。

        常用键：``actualWidth`` / ``actualHeight`` / ``bounds`` / ``isLoaded`` /
        ``isVisible`` / ``isEnabled`` / ``isFocused`` / ``text`` / ``value`` /
        ``selectedIndex`` / ``selectedItems`` / ``controlType`` / ``dpiScale``。
        窗口级键（``windowState`` / ``isActive`` / ``windowBounds``）也可以从
        任意控件发起，宿主按 key 识别，和节点无关。
        """
        return self._app.query(self._id, key, timeout=timeout, args=args)

    @property
    def actual_width(self) -> float:
        """布局完成后的真实宽度（逻辑像素），不是 ``width`` 的设定值。"""
        return float(self.query("actualWidth") or 0)

    @property
    def actual_height(self) -> float:
        """布局完成后的真实高度（逻辑像素）。"""
        return float(self.query("actualHeight") or 0)

    @property
    def actual_size(self) -> tuple[float, float]:
        """``(actual_width, actual_height)``。"""
        return self.actual_width, self.actual_height

    @property
    def is_loaded(self) -> bool:
        return bool(self.query("isLoaded"))

    @property
    def is_visible(self) -> bool:
        return bool(self.query("isVisible"))

    @property
    def is_enabled(self) -> bool:
        return bool(self.query("isEnabled"))

    @property
    def is_focused(self) -> bool:
        return bool(self.query("isFocused"))

    @property
    def control_type(self) -> str:
        """宿主实际创建的 WinUI 类名，排查「我写的类型到底变成了啥」时有用。"""
        return str(self.query("controlType") or "")

    @property
    def dpi_scale(self) -> float:
        return float(self.query("dpiScale") or 1.0)

    def scroll_to(self, index: int = 0) -> None:
        self._app._send_invoke(self._id, "scroll_to", {"index": index})

    def set_tooltip(self, text: str | None) -> None:
        """设置鼠标悬停提示，``None`` 清除。"""
        self.configure(tooltip=text)

    def animate_width(
        self,
        target: float,
        *,
        steps: int = ANIM_STEPS,
        interval: int = ANIM_INTERVAL,
    ) -> None:
        """把宽度按 ``steps`` 步指数缓出动画到 ``target``。

        每一步都算 ``w += (end_w - w) / 3`` 并**立刻赋值**，所以界面是一帧帧
        收到新宽度的，不是等算完再跳过去。

        最后一步直接取 ``target``：``(2/3)^20`` 还剩 0.03% 的零头，
        不 snap 的话宽度永远差一点。

        ::

            side.animate_width(56)     # 收起
            side.animate_width(200)    # 展开
        """
        start = float(getattr(self, "width", 0) or 0)
        if abs(target - start) < 0.5:
            self.width = target
            return

        def step(left: int) -> None:
            current = float(getattr(self, "width", 0) or 0)
            value = target if left == 1 else current + (target - current) / 3
            self.width = round(value, 1)
            if left > 1:
                self._app.after(interval, lambda: step(left - 1))

        step(steps)

    def set_menu(self, items: list[Any] | None) -> None:
        """设置右键菜单。

        每个菜单项是一个 dict：``label`` 必填，``glyph`` / ``value`` /
        ``enabled`` 可选；分隔线用字符串 ``"-"``。

        ::

            btn.set_menu([
                {"label": "复制", "glyph": "\\uE8C8", "value": "copy"},
                "-",
                {"label": "删除", "value": "delete"},
            ])
            btn.on_menu(lambda e: print(e.data["value"]))

        菜单项点击后统一回传 ``menu`` 事件，靠 ``value`` 区分点的是哪一条。
        传 ``None`` 或空列表移除菜单。
        """
        self.configure(contextMenu=list(items) if items else None)

    def on_menu(self, handler: Callable[..., Any]) -> str:
        """右键菜单项被点击。``e.data["value"]`` 是该项的 value。"""
        return self.bind("menu", handler)

    @property
    def id(self) -> str:
        return self._id

    @property
    def children(self) -> tuple["Widget", ...]:
        """子控件快照。

        返回元组而不是内部列表：调用方经常在遍历时增删控件，
        给出去可变列表会让迭代中途失效。
        """
        return tuple(self._children)

    def __repr__(self) -> str:
        return f"<{type(self).__name__} id={self._id}>"

    def destroy(self) -> None:
        if self._destroyed:
            return
        self._destroyed = True
        for child in list(self._children):
            child.destroy()
        self._app._send_patch([protocol.op_remove(self._id)])
        self._app._unregister_node(self._id)
        if self._parent is not None:
            try:
                self._parent._children.remove(self)
            except ValueError:
                pass


_STICKY_MAP = {"w": "left", "e": "right", "n": "top", "s": "bottom"}


def _sticky_to_align(sticky: str) -> dict[str, str]:
    """把 tkinter 的 sticky 字符翻译成对齐方式。返回 snake_case 键。"""
    if not sticky:
        return {}
    horizontal = "".join(c for c in sticky if c in "we")
    vertical = "".join(c for c in sticky if c in "ns")
    result: dict[str, str] = {}
    if horizontal == "we":
        result["h_align"] = "stretch"
    elif horizontal in ("w", "e"):
        result["h_align"] = _STICKY_MAP[horizontal]
    if vertical == "ns":
        result["v_align"] = "stretch"
    elif vertical in ("n", "s"):
        result["v_align"] = _STICKY_MAP[vertical]
    return result


# --------------------------------------------------------------------------
# 基础控件
# --------------------------------------------------------------------------


class Label(Widget):
    _type = "Label"

    def __init__(self, master: Any, text: str = "", **kwargs: Any):
        super().__init__(master, text=text, **kwargs)

    def set_text(self, text: str) -> None:
        self.configure(text=text)


class Button(Widget):
    _type = "Button"

    def __init__(
        self,
        master: Any,
        text: str = "",
        icon: str | None = None,
        **kwargs: Any,
    ):
        if icon is not None:
            kwargs["icon"] = icon
        super().__init__(master, text=text, **kwargs)

    def set_icon(self, glyph: str | None) -> None:
        """换图标。``glyph`` 是 Segoe Fluent Icons 的码点，传 ``None`` 去掉图标。"""
        self.configure(icon=glyph)


class HyperlinkButton(Widget):
    """超链接按钮。

    ``url`` 交给系统浏览器打开，点击事件照样会回传，
    所以「既跳转又想记一笔」不需要额外处理。
    """

    _type = "HyperlinkButton"

    def __init__(
        self,
        master: Any,
        text: str = "",
        url: str | None = None,
        icon: str | None = None,
        **kwargs: Any,
    ):
        if url is not None:
            kwargs["navigate_uri"] = url
        if icon is not None:
            kwargs["icon"] = icon
        super().__init__(master, text=text, **kwargs)

    @property
    def url(self) -> str | None:
        return self._props.get("navigateUri")

    @url.setter
    def url(self, value: str | None) -> None:
        self.configure(navigateUri=value)


class ToggleButton(Widget):
    """双态按钮：按下去保持按下，再按一次弹回。

    和 ``Checkbox`` 都是开/关，区别在语义与长相：``Checkbox`` 是表单里的一个选项，
    ``ToggleButton`` 是一个模式开关——工具栏的加粗、视图切换、筛选器都用它。

    ::

        bold = ui.ToggleButton(col, "加粗", icon="\\uE8DD")
        bold.value = True                     # 程序里改状态
        bold.bind("change", lambda e: print(e.data["checked"]))

    支持 ``variable=BooleanVar()`` 双向绑定。
    """

    _type = "ToggleButton"
    _var_prop = "checked"
    _var_field = "checked"

    def __init__(
        self,
        master: Any,
        text: str = "",
        checked: bool = False,
        icon: str | None = None,
        **kwargs: Any,
    ):
        if icon is not None:
            kwargs["icon"] = icon
        super().__init__(master, text=text, checked=checked, **kwargs)

    @property
    def value(self) -> bool:
        return bool(self._props.get("checked", False))

    @value.setter
    def value(self, checked: bool) -> None:
        self.configure(checked=bool(checked))

    def toggle(self) -> None:
        self.value = not self.value


class SplitButton(Widget):
    """两段式按钮：左边执行默认动作，右边箭头弹出相关动作。

    ``items`` 的写法和 ``contextMenu`` 完全一样（``label`` / ``glyph`` /
    ``value`` / ``enabled``，分隔线用 ``"-"``）。

    ::

        save = ui.SplitButton(col, "保存", icon="\\uE74E", items=[
            {"label": "另存为…", "value": "save-as"},
            {"label": "导出为 PDF", "value": "export"},
        ])
        save.bind("click", lambda e: do_save())          # 左半边
        save.on_menu(lambda e: print(e.data["value"]))   # 右半边菜单

    继承来的 ``set_menu`` 仍然设右键菜单，下拉菜单用 ``set_items``，别混。
    """

    _type = "SplitButton"

    def __init__(
        self,
        master: Any,
        text: str = "",
        items: list[Any] | None = None,
        icon: str | None = None,
        **kwargs: Any,
    ):
        if icon is not None:
            kwargs["icon"] = icon
        super().__init__(master, text=text, **kwargs)
        if items:
            self.set_items(items)

    def set_items(self, items: list[Any] | None) -> None:
        """设置右拉菜单。传 ``None`` 或空列表则去掉箭头。"""
        self.configure(menu=list(items) if items else None)


class RepeatButton(Widget):
    """按住会连续触发的按钮，常见于数值增减。

    别和普通 ``Button`` 混用：它的 ``click`` 在按住期间会不断回传，
    回调里做重活会拖慢整个界面。

    ::

        ui.RepeatButton(col, "+", icon="\\uE710", repeat_interval=80)
    """

    _type = "RepeatButton"

    def __init__(
        self,
        master: Any,
        text: str = "",
        icon: str | None = None,
        repeat_delay: int | None = None,
        repeat_interval: int | None = None,
        **kwargs: Any,
    ):
        if icon is not None:
            kwargs["icon"] = icon
        if repeat_delay is not None:
            kwargs["repeat_delay"] = repeat_delay
        if repeat_interval is not None:
            kwargs["repeat_interval"] = repeat_interval
        super().__init__(master, text=text, **kwargs)


class Entry(Widget):
    _type = "Entry"
    _var_prop = "text"
    _var_field = "text"

    def __init__(self, master: Any, text: str = "", **kwargs: Any):
        super().__init__(master, text=text, **kwargs)

    @property
    def value(self) -> str:
        return self._props.get("text", "")

    @value.setter
    def value(self, text: str) -> None:
        self.configure(text=text)

    def get(self) -> str:
        return self.value

    def set(self, text: str) -> None:
        self.value = text

    def select_all(self) -> None:
        self._app._send_invoke(self._id, "select_all")

    def select_range(self, start: int, length: int = 0) -> None:
        """选中 ``[start, start+length)`` 区间的字符。"""
        self._app._send_invoke(self._id, "select_range", {"start": start, "length": length})

    def clear(self) -> None:
        self._app._send_invoke(self._id, "clear")

    def copy(self) -> None:
        """把选区复制到剪贴板。"""
        self._app._send_invoke(self._id, "copy")

    def cut(self) -> None:
        self._app._send_invoke(self._id, "cut")

    def paste(self) -> None:
        self._app._send_invoke(self._id, "paste")

    @property
    def selection(self) -> tuple[int, int]:
        """``(起始位置, 选中长度)``，向宿主实时读取。"""
        start = int(self.query("selectionStart") or 0)
        length = int(self.query("selectionLength") or 0)
        return start, length

    @property
    def selected_text(self) -> str:
        return str(self.query("selectedText") or "")

    def set_spellcheck(self, enabled: bool = True) -> None:
        """开关拼写检查红线；同时可用 ``accepts_return`` 控制多行回车。"""
        self.configure(is_spell_check_enabled=bool(enabled))


class Checkbox(Widget):
    _type = "Checkbox"
    _var_prop = "checked"
    _var_field = "checked"

    def __init__(self, master: Any, text: str = "", **kwargs: Any):
        super().__init__(master, text=text, **kwargs)

    @property
    def value(self) -> bool:
        return bool(self._props.get("checked", False))

    @value.setter
    def value(self, checked: bool) -> None:
        self.configure(checked=checked)

    def get(self) -> bool:
        return self.value

    def toggle(self) -> None:
        self.value = not self.value


class Radio(Widget):
    """单选框。

    同一个 ``group`` 内的选项自动互斥——这是 WinUI ``RadioButton.GroupName``
    的行为，不需要 Python 侧手动取消其它项。

    ::

        ui.Radio(col, "选项一", group="g1", checked=True)
        ui.Radio(col, "选项二", group="g1")
    """

    _type = "Radio"
    _var_prop = "checked"
    _var_field = "checked"

    def __init__(
        self,
        master: Any,
        text: str = "",
        group: str | None = None,
        checked: bool = False,
        **kwargs: Any,
    ):
        if group is not None:
            kwargs["group_name"] = group
        super().__init__(master, text=text, checked=checked, **kwargs)

    @property
    def value(self) -> bool:
        return bool(self._props.get("checked", False))

    @value.setter
    def value(self, checked: bool) -> None:
        self.configure(checked=checked)


class Switch(Widget):
    _type = "Switch"
    _var_prop = "on"
    # 宿主回传的 change 事件统一用 checked 字段，和 Checkbox 一致
    _var_field = "checked"

    def __init__(self, master: Any, header: str = "", **kwargs: Any):
        super().__init__(master, header=header, **kwargs)

    @property
    def value(self) -> bool:
        return bool(self._props.get("on", False))

    @value.setter
    def value(self, on: bool) -> None:
        self.configure(on=on)


class Slider(Widget):
    _type = "Slider"
    _var_prop = "value"
    _var_field = "value"

    def __init__(
        self,
        master: Any,
        minimum: float = 0,
        maximum: float = 100,
        value: float = 0,
        **kwargs: Any,
    ):
        super().__init__(master, min=minimum, max=maximum, value=value, **kwargs)

    @property
    def value(self) -> float:
        return float(self._props.get("value", 0))

    @value.setter
    def value(self, v: float) -> None:
        self.configure(value=v)


class ProgressBar(Widget):
    _type = "ProgressBar"

    def __init__(
        self,
        master: Any,
        value: float = 0,
        indeterminate: bool = False,
        **kwargs: Any,
    ):
        super().__init__(master, value=value, indeterminate=indeterminate, **kwargs)

    @property
    def value(self) -> float:
        return float(self._props.get("value", 0))

    @value.setter
    def value(self, v: float) -> None:
        self.configure(value=v)

    @property
    def indeterminate(self) -> bool:
        """不确定态：不显示具体进度，光条来回跑。用于无法预估耗时的任务。"""
        return bool(self._props.get("indeterminate", False))

    @indeterminate.setter
    def indeterminate(self, value: bool) -> None:
        self.configure(indeterminate=value)


class ProgressRing(Widget):
    """不确定态加载圈。

    和 ``ProgressBar(indeterminate=True)`` 表达同一件事，区别只在形状：
    圈适合塞在窄小的地方（按钮旁、面板角落），条适合横向铺开。
    """

    _type = "ProgressRing"

    def __init__(self, master: Any, size: int = 40, active: bool = True, **kwargs: Any):
        super().__init__(master, size=size, active=active, **kwargs)

    @property
    def active(self) -> bool:
        """True 时旋转，False 时停下并隐藏。"""
        return bool(self._props.get("active", True))

    @active.setter
    def active(self, value: bool) -> None:
        self.configure(active=value)


class ComboBox(Widget):
    _type = "ComboBox"

    def __init__(self, master: Any, items: list[str] | None = None, **kwargs: Any):
        super().__init__(master, items=items or [], **kwargs)

    @property
    def selected_index(self) -> int:
        return int(self._props.get("selectedIndex", -1))

    @selected_index.setter
    def selected_index(self, index: int) -> None:
        self.configure(selectedIndex=index)

    @property
    def value(self) -> str | None:
        items = self._props.get("items") or []
        index = self.selected_index
        return items[index] if 0 <= index < len(items) else None


def _item_text(item: Any) -> str:
    return item if isinstance(item, str) else item.get("text", "")


def _item_glyph(item: Any) -> str | None:
    if isinstance(item, str):
        return None
    return item.get("glyph")


class ListBox(Widget):
    """列表。既当普通列表用，也可以当会自动收缩的侧边导航。

    打开 ``auto_collapse=True`` 后行为是：

    - 鼠标移到它上面 → 展开
    - 点中某一项 → 钉住，鼠标移开也保持展开
    - 点内容区 → 调 ``release()`` 取消钉住并收起

    展开/收起都是 20 步指数缓出动画（``w += (end_w - w) / 3``）。
    折叠只改宽度，所以构造时必须给一个大于 ``collapsed_width`` 的 ``width``。

    ::

        nav = ui.ListBox(win, [
            {"text": "概览", "glyph": "\\uE80F"},
            {"text": "设置", "glyph": "\\uE713"},
        ], width=200, auto_collapse=True, collapsed_width=56)

        body.bind("pointerPressed", lambda e: nav.release())
    """

    _type = "ListBox"

    def __init__(
        self,
        master: Any,
        items: list[str] | None = None,
        *,
        auto_collapse: bool = False,
        collapsed_width: int = 56,
        hover_expand: bool = True,
        pin_on_select: bool = True,
        expanded: bool | None = None,
        **kwargs: Any,
    ):
        start_open = (not auto_collapse) if expanded is None else expanded
        expanded_width = int(kwargs.get("width") or 0)

        # 自动收缩并且一开始就收起时，初始宽度得直接给窄值。
        # 只把文字藏起来不改宽度的话，它会以 200 宽的样子躺在那儿。
        if auto_collapse and not start_open and expanded_width:
            kwargs["width"] = collapsed_width

        super().__init__(master, items=items or [], **kwargs)

        self._auto_collapse = auto_collapse
        self._collapsed_width = collapsed_width
        self._expanded_width = expanded_width
        self._hover_expand = hover_expand
        self._pin_on_select = pin_on_select
        self._raw_items = list(items or [])
        self._pinned = False
        self._opened = start_open

        if auto_collapse and hover_expand:
            self.bind("pointerEntered", lambda e: self.set_open(True))
            self.bind("pointerExited", lambda e: self._collapse_unless_pinned())

        if auto_collapse and not self._opened:
            # 一开始就是窄条，文字先收起来，否则能看到半截汉字
            self._render_items(collapsed=True)

    @property
    def selected_index(self) -> int:
        return int(self._props.get("selectedIndex", -1))

    @selected_index.setter
    def selected_index(self, index: int) -> None:
        self.configure(selectedIndex=index)

    # ---- 折叠 ----

    @property
    def is_open(self) -> bool:
        return self._opened

    @property
    def is_pinned(self) -> bool:
        return self._pinned

    def _on_event(self, event: Any) -> None:
        # 用户点了某一项就钉住；echo 事件是程序设的选中，不算
        if self._auto_collapse and self._pin_on_select and event.name == "change":
            self._pinned = True

    def _collapse_unless_pinned(self) -> None:
        if not self._pinned:
            self.set_open(False)

    def release(self) -> None:
        """取消钉住并收起。点内容区时调它。"""
        self._pinned = False
        self.set_open(False)

    def toggle(self) -> None:
        self.set_open(not self._opened)

    def set_open(self, opened: bool) -> None:
        if not self._auto_collapse or opened == self._opened:
            return

        if self._expanded_width <= self._collapsed_width:
            raise ValueError(
                "auto_collapse 需要构造时给一个大于 collapsed_width 的 width，"
                f"当前 width={self._expanded_width}，collapsed_width={self._collapsed_width}"
            )

        self._opened = opened
        if opened:
            # 先把文字放回去，展开过程中标题就是完整的
            self._render_items(collapsed=False)
            self.animate_width(self._expanded_width)
        else:
            self.animate_width(self._collapsed_width)
            # 收完再抹掉文字：只改宽度的话汉字会被裁成半个，很难看
            self._app.after(
                ANIM_STEPS * ANIM_INTERVAL,
                lambda: self._render_items(collapsed=True),
            )

    def _render_items(self, *, collapsed: bool) -> None:
        """折叠时只留图标。

        没有图标的项在折叠态会变成空白条，所以只要有一项缺 glyph，
        就整体保持原样，让宽度去裁——总比一排空按钮好。
        """
        if not self._raw_items:
            return
        if collapsed and not all(_item_glyph(item) for item in self._raw_items):
            return

        index = self.selected_index
        rendered: list[Any] = []
        for item in self._raw_items:
            text, glyph = _item_text(item), _item_glyph(item)
            if glyph:
                rendered.append({"text": "" if collapsed else text, "glyph": glyph})
            else:
                rendered.append("" if collapsed else text)

        self.configure(items=rendered)

        # ItemsSource 换掉会清空选中，按原索引选回来
        if index >= 0:
            self.configure(selectedIndex=index)


class Image(Widget):
    """图片。

    ``src`` 接受三种写法：本地路径、http(s) URL、``data:image/png;base64,...``。
    最后一种适合程序里生成的图（截图、图表），不必先落盘。
    """

    _type = "Image"

    def __init__(self, master: Any, src: str = "", **kwargs: Any):
        super().__init__(master, src=src, **kwargs)

    def on_error(self, handler: Callable[..., Any]) -> str:
        """加载失败回调，错误信息在 ``e.data["error"]``。"""
        return self.bind("imageFailed", handler)

    def set_decode_size(self, width: int | None = None, height: int | None = None) -> None:
        """按目标像素解码大图，避免把 4000px 的原图整个塞进内存。

        要在 ``src`` 之前（或同一批 ``configure``）设置；宿主会先收集这两个
        值再创建 ``BitmapImage``，和 kwargs 的书写顺序无关。
        """
        self.configure(decode_pixel_width=width, decode_pixel_height=height)


class Media(Widget):
    """媒体播放器（视频 / 音频）。

    自带播放、暂停、进度条那套传输控件；把 ``show_controls=False`` 关掉，
    就可以用 ``play()`` / ``pause()`` 配自己的按钮来控制。

    ::

        player = ui.Media(col, src=r"C:\\videos\\demo.mp4", height=360)
        player.bind("mediaEnded", lambda e: print("放完了"))
    """

    _type = "Media"

    def __init__(
        self,
        master: Any,
        src: str = "",
        auto_play: bool = False,
        show_controls: bool = True,
        **kwargs: Any,
    ):
        super().__init__(
            master,
            src=src,
            auto_play=auto_play,
            show_controls=show_controls,
            **kwargs,
        )

    @property
    def volume(self) -> float:
        return float(self._props.get("volume", 1.0))

    @volume.setter
    def volume(self, value: float) -> None:
        self.configure(volume=max(0.0, min(1.0, float(value))))

    @property
    def position(self) -> float:
        """当前播放位置（秒）。赋值即跳转。"""
        return float(self._props.get("position", 0.0))

    @position.setter
    def position(self, seconds: float) -> None:
        self.configure(position=max(0.0, float(seconds)))

    def seek(self, seconds: float) -> None:
        """跳转到指定秒数。

        和 ``position = seconds`` 等价，留着是因为「跳转」这个动作在代码里
        写成动词比写成赋值更好读。
        """
        self._app._send_invoke(self._id, "seek", {"seconds": max(0.0, float(seconds))})

    # 下面这些是播放器运行时的真实状态，必须问宿主；
    # ``position`` / ``volume`` / ``loop`` 是「设定值」，两者不要混用。
    @property
    def current_position(self) -> float:
        return float(self.query("position") or 0)

    @property
    def duration(self) -> float:
        """媒体总时长（秒），未打开时返回 0。"""
        return float(self.query("duration") or 0)

    @property
    def is_playing(self) -> bool:
        return bool(self.query("isPlaying"))

    @property
    def is_muted_now(self) -> bool:
        return bool(self.query("isMuted"))

    @property
    def is_full_window(self) -> bool:
        return bool(self.query("isFullWindow"))

    @is_full_window.setter
    def is_full_window(self, value: bool) -> None:
        self.configure(is_full_window=bool(value))

    @property
    def loop(self) -> bool:
        return bool(self._props.get("loop", False))

    @loop.setter
    def loop(self, value: bool) -> None:
        self.configure(loop=bool(value))

    @property
    def muted(self) -> bool:
        return bool(self._props.get("muted", False))

    @muted.setter
    def muted(self, value: bool) -> None:
        self.configure(muted=bool(value))

    @property
    def playback_rate(self) -> float:
        return float(self._props.get("playbackRate", 1.0))

    @playback_rate.setter
    def playback_rate(self, rate: float) -> None:
        self.configure(playbackRate=max(0.25, min(4.0, float(rate))))

    def play(self) -> None:
        self._app._send_invoke(self._id, "play")

    def pause(self) -> None:
        self._app._send_invoke(self._id, "pause")

    def stop(self) -> None:
        """停止并回到开头。宿主侧是「暂停 + 位置归零」。"""
        self._app._send_invoke(self._id, "stop")


class Separator(Widget):
    _type = "Separator"

    def __init__(self, master: Any, **kwargs: Any):
        super().__init__(master, **kwargs)


class WebView2(Widget):
    """WebView2 网页渲染控件。

    依赖系统安装的 **WebView2 Runtime**。宿主在渲染表单时会先探测运行时：
    没装的话不会白屏、也不会让窗口崩，而是把这个控件换成一块灰色矩形，
    居中显示「加载失败」，下方写明原因——本控件的方法调用此时是安全的空操作。

    ::

        web = ui.WebView2(root, source="https://www.example.com", height=360)
        web.navigate("https://www.bing.com")
        web.on_navigate(lambda e: print("已到", e.data["url"]))
        print(web.current_url)          # 浏览器里的真实地址
        web.reload()
        web.go_back()

    ``source`` 必须是绝对 URL，``https://`` / ``file:///`` / ``http://localhost``
    都可以；``navigate_to_string(html)`` 则直接渲染一段 HTML 字符串。

    ``source`` 是**你最后一次写进去的目标**，``current_url`` 是**浏览器回报的
    实际地址**——页面里点链接、服务端重定向、JS 改 ``location.hash`` 之后两者会不同。
    缺运行时走降级页时不会有地址事件回报，``current_url`` 保持最后一次设置的值。

    想强制走降级占位做演示，启动 Python 前设环境变量
    ``PYWINUI3_WEBVIEW2_FALLBACK=1``。
    """

    _type = "WebView2"
    _URL_EVENTS = frozenset(
        {"navigationStarting", "navigationCompleted", "sourceChanged"}
    )

    def __init__(self, master: Any, source: str = "", **kwargs: Any):
        super().__init__(master, source=source, **kwargs)
        self._current_url = str(source)

        # 常驻订阅 sourceChanged：宿主看到这个事件名才会去挂 CoreWebView2 的地址监听。
        # 用空回调占住事件名，真正的同步写在 _sync_from_event 里，所以用户之后
        # bind("sourceChanged", ...) 覆盖掉空回调也不会影响 current_url。
        self.bind("sourceChanged", lambda _event: None)

    def _sync_from_event(self, event: Any) -> None:
        # 导航事件在 echo 抑制期间也可能到达，所以同步放在这里而不是 _on_event。
        if event.name in self._URL_EVENTS:
            url = event.data.get("url")
            if isinstance(url, str):
                self._current_url = url
        super()._sync_from_event(event)

    @property
    def source(self) -> str:
        return str(self._props.get("source", ""))

    @source.setter
    def source(self, url: str) -> None:
        self._current_url = str(url)
        self.configure(source=str(url))

    @property
    def current_url(self) -> str:
        """浏览器当前的真实地址（含页面内跳转与重定向），由宿主导航事件回报。"""
        return self._current_url

    def on_navigate(self, handler: Callable[..., Any]) -> str:
        """导航完成时回调；``Event.data`` 含 ``url`` / ``isSuccess`` / ``httpStatusCode``。"""
        return self.bind("navigationCompleted", handler)

    def navigate(self, url: str) -> None:
        """导航到绝对 URL，等价于 ``web.source = url``。"""
        self.source = str(url)

    def navigate_to_string(self, html: str) -> None:
        """直接渲染一段 HTML 字符串，不需要文件或本地服务器。

        地址栏概念在这类页面上不成立，宿主回报的地址通常是空串。
        """
        self._app._send_invoke(self._id, "navigate_to_string", {"html": str(html)})

    def reload(self) -> None:
        """重新加载当前页。"""
        self._app._send_invoke(self._id, "reload")

    def go_back(self) -> None:
        """后退。没有历史记录时宿主侧静默跳过。"""
        self._app._send_invoke(self._id, "go_back")

    def go_forward(self) -> None:
        """前进。没有可前进的历史时宿主侧静默跳过。"""
        self._app._send_invoke(self._id, "go_forward")

    # ---- 直接向宿主读状态（事件驱动之外的一条路） ----

    @property
    def browser_url(self) -> str:
        """立刻向宿主读一次浏览器地址，不等导航事件。冷启动 Core 未就绪时可能是空串。"""
        return str(self.query("currentUrl") or "")

    @property
    def can_go_back(self) -> bool:
        return bool(self.query("canGoBack"))

    @property
    def can_go_forward(self) -> bool:
        return bool(self.query("canGoForward"))

    @property
    def web_title(self) -> str:
        return str(self.query("webTitle") or "")

    def execute_script(self, script: str) -> None:
        """在页面里执行一段 JavaScript。

        invoke 没有返回值，结果以 ``scriptResult`` 事件回推，
        用 ``on_script_result()`` 接收；``e.data["result"]`` 是 JSON 字符串，
        出错时给 ``e.data["error"]``。
        """
        self._app._send_invoke(self._id, "execute_script", {"script": str(script)})

    def on_script_result(self, handler: Callable[..., Any]) -> str:
        return self.bind("scriptResult", handler)
