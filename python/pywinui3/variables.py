"""可观察变量，对标 tkinter 的 StringVar / IntVar。

双向绑定的关键在于区分「谁改的」：

- 程序 ``var.set(x)`` → 推给控件 → 控件发 patch → 宿主回一条 ``echo: true`` 的事件
- 用户敲键盘 → 宿主发 ``echo: false`` 的事件 → 更新变量 → 触发 trace

``echo`` 事件只同步值、不触发 trace，这样就不会出现「设值 → 回调 → 再设值」的死循环。
"""

from __future__ import annotations

import traceback
from typing import Any, Callable, ClassVar


class Variable:
    """所有变量类型的基类。"""

    _default: ClassVar[Any] = None

    def __init__(self, value: Any = None, *, name: str = ""):
        self._name = name
        self._value = self._default if value is None else value
        self._widgets: list[Any] = []
        self._traces: list[Callable[[Any], None]] = []

    # ---- 取值 / 赋值 ----

    def get(self) -> Any:
        return self._value

    def set(self, value: Any) -> None:
        if value == self._value:
            return
        self._value = value
        for widget in list(self._widgets):
            widget._apply_variable(self, value)

    # ---- 与控件绑定 ----

    def _attach(self, widget: Any) -> None:
        if widget not in self._widgets:
            self._widgets.append(widget)
        widget._apply_variable(self, self._value)

    def _detach(self, widget: Any) -> None:
        if widget in self._widgets:
            self._widgets.remove(widget)

    def _on_user_input(self, value: Any, *, fire_trace: bool = True) -> None:
        """宿主回报变化时调用。

        ``fire_trace=False`` 用于 echo 事件：只同步值，不触发监听，
        否则「程序 set → 宿主 echo → trace → 再 set」会形成回环。
        """
        if value == self._value:
            return
        self._value = value
        if not fire_trace:
            return
        for callback in list(self._traces):
            try:
                callback(self._value)
            except Exception:  # noqa: BLE001 - 回调异常不能拖垮事件循环
                traceback.print_exc()

    # ---- 监听 ----

    def trace(self, callback: Callable[[Any], None]) -> None:
        """值变化时回调。回调收到新值。"""
        self._traces.append(callback)

    def trace_add(self, _mode: str, callback: Callable[..., None]) -> None:
        """tkinter 签名兼容。忽略 mode，回调按 tkinter 约定传 (name, index, mode)。"""
        self._traces.append(lambda _v: callback(self._name, "", _mode))

    def __repr__(self) -> str:
        return f"<{type(self).__name__} {self._value!r}>"


class StringVar(Variable):
    _default = ""

    def get(self) -> str:
        return str(self._value)


class IntVar(Variable):
    _default = 0

    def get(self) -> int:
        return int(self._value)


class DoubleVar(Variable):
    _default = 0.0

    def get(self) -> float:
        return float(self._value)


class BooleanVar(Variable):
    _default = False

    def get(self) -> bool:
        return bool(self._value)

    def toggle(self) -> None:
        self.set(not self.get())
