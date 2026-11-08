"""事件对象。

回调统一接收一个 ``Event``，而不是裸的参数列表。
这样后续给事件加字段不需要改所有回调签名，也避免「这个事件到底传几个参数」的记忆负担。

``command=` 是个例外：它是 tkinter 里最常用的写法，保持**无参**。
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any


@dataclass(slots=True)
class Event:
    """一次控件事件。"""

    #: 触发事件的控件。
    widget: Any

    #: 事件名，如 ``click`` / ``change`` / ``submit``。
    name: str

    #: 宿主回传的原始载荷。
    data: dict[str, Any] = field(default_factory=dict)

    #: 为 True 表示这次变化是程序自己设置属性引起的，不是用户操作。
    echo: bool = False

    # ---- 常用字段的便捷读取 ----

    @property
    def text(self) -> str | None:
        value = self.data.get("text")
        return value if isinstance(value, str) else None

    @property
    def value(self) -> Any:
        return self.data.get("value")

    @property
    def checked(self) -> bool | None:
        value = self.data.get("checked")
        return value if isinstance(value, bool) else None

    @property
    def index(self) -> int | None:
        value = self.data.get("index")
        return value if isinstance(value, int) else None

    def __repr__(self) -> str:
        return f"<Event {self.name} on {self.widget!r} data={self.data!r}>"
