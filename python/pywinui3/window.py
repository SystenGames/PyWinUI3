"""窗口。

``Window`` 同时承担两个角色：它是窗口（标题、尺寸、主题），
也是控件树的根（协议里的 ``root``）。这两件事共用同一个节点 id，
所以窗口内容根不需要额外包一层容器——``Column`` 的堆叠语义直接生效。
"""

from __future__ import annotations

from typing import Any, Callable

from . import protocol
from .app import App
from .widgets import Widget, camelize, normalize_value


class Window(Widget):
    """顶层窗口。

    ``layout`` 决定窗口内容根本身是什么容器：

    ==========  ================================  ==============================
    值            根节点                            适合
    ==========  ================================  ==============================
    ``column``   ``StackPanel``（默认，垂直堆叠）      普通表单
    ``row``      ``StackPanel``（水平堆叠）           横向排布
    ``grid``     ``Grid``                          侧栏 + 内容区这类整屏布局
    ==========  ================================  ==============================

    只有 ``layout="grid"`` 时，窗口根节点才会被当成 Grid：
    子控件的 ``.grid(row=, column=)`` 直接相对窗口定位，``*`` 行列也能拿到
    真实的剩余高度——侧边栏要撑满整窗高度就靠这个。

    ::

        win = ui.Window(title="工具", layout="grid", scroll=False,
                        rows=["*"], columns=["auto", "*"])
        side = ui.ListBox(win, width=220, auto_collapse=True, collapsed_width=56)
        side.grid(row=0, column=0)
    """

    #: 窗口内容根就是垂直容器，所以直接子控件默认从上往下排列。
    _type = "Column"

    #: 这些属性属于窗口本身，走 ``window`` 操作；其余走节点 ``set``。
    _WINDOW_ATTRS = frozenset(
        {
            "title",
            "width",
            "height",
            "min_width",
            "min_height",
            "resizable",
            "center",
            "x",
            "y",
            "icon",
            "always_on_top",
            "minimizable",
            "maximizable",
            "maximized",
            "scroll",
            "theme",
            "backdrop",
        }
    )

    #: 窗口根节点可用的布局类型。默认 Column：直接子控件从上往下排。
    _LAYOUTS = {"column": "Column", "row": "Row", "grid": "Grid"}

    def __init__(
        self,
        app: App | None = None,
        *,
        title: str = "PyWinUI3",
        width: int = 800,
        height: int = 600,
        scroll: bool = True,
        layout: str = "column",
        **kwargs: Any,
    ):
        # 不走 super().__init__：Window 没有 master，初始化路径与普通控件不同
        self._app = app if app is not None else App.default()
        self._type = self._LAYOUTS.get(layout, "Column")
        self._id = self._app._alloc_node_id()
        self._parent = None
        self._children = []
        # spacing 只有堆叠容器认；放到 Grid 上会变成一条「未知属性」告警
        self._props: dict[str, Any] = {"spacing": 0} if self._type in ("Column", "Row") else {}
        self._events: dict[str, Callable[..., Any]] = {}
        self._handler_ids: dict[str, str] = {}
        self._destroyed = False
        # Window 不绑定变量，但事件分发会统一调用 _sync_from_event，需要这个占位
        self._variable: Any = None

        self._window_props: dict[str, Any] = {
            "title": title,
            "width": width,
            "height": height,
            "scroll": scroll,
        }

        # 窗口专属属性从 kwargs 里摘出来，剩下的当成容器属性
        for key in list(kwargs):
            if key in self._WINDOW_ATTRS:
                self._window_props[camelize(key)] = normalize_value(kwargs.pop(key))

        if kwargs:
            from .widgets import build_props

            self._props.update(build_props(kwargs))

        # Grid 根节点默认给一个 "*" 行，否则没有行定义时子控件会按内容高度排
        if self._type == "Grid" and "rows" not in self._props:
            self._props["rows"] = ["*"]

        self._app._register_node(self)
        self._app._attach_window(self)

    # ---- 窗口属性 ----

    def _window_spec(self) -> dict[str, Any]:
        return dict(self._window_props)

    def configure(self, **kwargs: Any) -> None:
        window_props: dict[str, Any] = {}
        node_props: dict[str, Any] = {}

        for key, value in kwargs.items():
            if key in self._WINDOW_ATTRS:
                window_props[camelize(key)] = normalize_value(value)
            else:
                node_props[camelize(key)] = normalize_value(value)

        if window_props:
            self._window_props.update(window_props)
            self._app._send_patch([protocol.op_window(window_props)])

        if node_props:
            self._props.update(node_props)
            self._app._send_patch([protocol.op_set(self._id, node_props)])

    config = configure

    def __getattr__(self, name: str) -> Any:
        if name.startswith("_"):
            raise AttributeError(name)
        key = camelize(name)
        window_props = self.__dict__.get("_window_props", {})
        if key in window_props:
            return window_props[key]
        return super().__getattr__(name)

    # ---- 生命周期 ----

    def mainloop(self) -> None:
        """起宿主、渲染、进入事件循环。"""
        self._app.mainloop()

    def quit(self) -> None:
        self._app.quit()

    def destroy(self) -> None:
        self._app.quit()

    def after(self, ms: int, callback: Callable[[], None]) -> None:
        self._app.after(ms, callback)

    def on_close(self, callback: Callable[..., None]) -> None:
        """窗口即将关闭时回调。首版不支持取消关闭。"""
        self.bind("closing", callback)

    def on_resize(self, callback: Callable[..., None]) -> None:
        """窗口尺寸变化时回调，``e.data`` 里是 ``width`` / ``height``（逻辑像素）。"""
        self.bind("sizeChanged", callback)

    def on_activate(self, callback: Callable[..., None]) -> None:
        """窗口获得/失去焦点时回调，``e.data["active"]`` 是布尔值。"""
        self.bind("windowActivated", callback)

    def on_state_changed(self, callback: Callable[..., None]) -> None:
        """最大化 / 最小化 / 还原时回调，``e.data["state"]`` 是窗口状态。"""
        self.bind("windowStateChanged", callback)

    # ---- 运行时状态（query/result 读回） ----

    @property
    def state(self) -> str:
        """``normal`` / ``maximized`` / ``minimized``。"""
        return str(self.query("windowState") or "normal")

    @property
    def is_maximized(self) -> bool:
        return self.state == "maximized"

    @property
    def is_minimized(self) -> bool:
        return self.state == "minimized"

    @property
    def is_active(self) -> bool:
        return bool(self.query("isActive"))

    @property
    def window_bounds(self) -> dict[str, Any]:
        """``{"x", "y", "width", "height"}``，窗口在屏幕上的真实位置与尺寸。"""
        return dict(self.query("windowBounds") or {})

    # ---- 窗口动作 ----

    def minimize(self) -> None:
        self._app._send_invoke(self._id, "minimize")

    def maximize(self) -> None:
        self._app._send_invoke(self._id, "maximize")

    def restore(self) -> None:
        self._app._send_invoke(self._id, "restore")

    def activate(self) -> None:
        """把窗口提到前台并激活。"""
        self._app._send_invoke(self._id, "activate")

    def protocol(self, _name: str, callback: Callable[..., None]) -> None:
        """tkinter 兼容：``protocol("WM_DELETE_WINDOW", fn)``。"""
        self.on_close(callback)

    def __enter__(self) -> "Window":
        return self

    def __exit__(self, *exc_info: object) -> None:
        self._app.quit()

    def __repr__(self) -> str:
        return f"<Window id={self._id} title={self._window_props.get('title')!r}>"
