"""App —— 宿主进程的生命周期与事件循环。

这个类承担的是 tkinter 里 ``Tk`` 的角色，但把「窗口」这件事拆了出去：
``App`` 管进程与循环，``Window`` 管窗口与控件树。

事件循环的模型刻意做得和 tkinter 一样：主线程阻塞在消息接收上，
回调就在这个线程里执行。所以回调里阻塞会卡住整个 UI，
这一点和 tkinter 完全一致，不做隐藏线程池的魔法。
"""

from __future__ import annotations

import os
import sys
import time
import traceback
from pathlib import Path
from typing import Any, Callable, ClassVar

from . import protocol
from .errors import (
    HostExited,
    HostStartError,
    HostTimeout,
    ProtocolError,
    ProtocolVersionError,
    UiError,
)
from .event import Event
from .transport import HostProcess

#: 握手超时（秒）。宿主冷启动要初始化 WinUI，留足余量。
DEFAULT_TIMEOUT = 10.0

#: 事件循环的单次轮询间隔。足够跟上 60fps，又不会空转烧 CPU。
POLL_INTERVAL = 0.05


class App:
    """宿主进程与应用生命周期。"""

    _default: ClassVar[App | None] = None

    def __init__(
        self,
        host: str | Path | None = None,
        *,
        timeout: float = DEFAULT_TIMEOUT,
        on_error: Callable[[UiError], None] | None = None,
    ):
        self._host_path = host
        self._timeout = timeout
        self._on_error = on_error

        self._host: HostProcess | None = None
        self._rev = 0
        self._node_seq = 0
        self._handler_seq = 0

        self._nodes: dict[str, Any] = {}
        self._handlers: dict[str, Callable[..., Any]] = {}
        self._window: Any = None

        self._rendered = False
        self._running = False
        self._graceful = False
        self._timers: list[tuple[float, Callable[[], None]]] = []

        #: 宿主回报的能力位，握手后可用。
        self.capabilities: dict[str, Any] = {}

        App._default = self

    # ------------------------------------------------------------------
    # 单例
    # ------------------------------------------------------------------

    @classmethod
    def default(cls) -> App:
        """返回当前默认 App，没有就创建一个。供 ``Window()`` 无参调用时使用。"""
        if cls._default is None:
            cls._default = cls()
        return cls._default

    # ------------------------------------------------------------------
    # 启动与握手
    # ------------------------------------------------------------------

    def start(self) -> None:
        if self._host is not None:
            return

        host = HostProcess(self._host_path)
        host.start()
        self._host = host
        try:
            self._handshake()
        except Exception:
            host.close()
            self._host = None
            raise

    def _handshake(self) -> None:
        assert self._host is not None
        self._host.send(protocol.hello(os.getpid()))

        try:
            message = self._host.recv(timeout=self._timeout)
        except HostTimeout as exc:
            raise HostStartError(
                f"宿主在 {self._timeout:.0f} 秒内没有完成握手，可能卡在 WinUI 初始化"
            ) from exc
        except HostExited as exc:
            detail = f"\n宿主 stderr:\n{exc.stderr}" if exc.stderr else ""
            raise HostStartError(f"宿主在握手前就退出了（exit={exc.exit_code}）{detail}") from exc

        kind = message.get("t")
        if kind == "error":
            raise HostStartError(
                f"宿主拒绝握手：[{message.get('code')}] {message.get('message')}"
            )
        if kind != "ready":
            raise ProtocolError(f"握手时期望收到 ready，实际收到 {kind!r}")

        version = message.get("protocol")
        if version != protocol.PROTOCOL_VERSION:
            raise ProtocolVersionError(
                f"协议版本不匹配：客户端 {protocol.PROTOCOL_VERSION}，宿主 {version}。"
                "请重新构建宿主。"
            )

        self.capabilities = message.get("capabilities") or {}

    # ------------------------------------------------------------------
    # 渲染
    # ------------------------------------------------------------------

    def _attach_window(self, window: Any) -> None:
        self._window = window

    def _render(self) -> None:
        if self._window is None:
            raise RuntimeError("还没有创建 Window，无法渲染")
        assert self._host is not None

        specs: list[dict[str, Any]] = []
        self._window._collect_specs(specs)

        self._rev += 1
        self._host.send(
            protocol.render(
                self._rev,
                self._window._window_spec(),
                self._window._id,
                specs,
            )
        )
        self._rendered = True

    # ------------------------------------------------------------------
    # 事件循环
    # ------------------------------------------------------------------

    def mainloop(self) -> None:
        """启动宿主、渲染窗口、进入事件循环。阻塞直到窗口关闭。"""
        self.start()
        try:
            self._render()
            self._loop()
        finally:
            self._shutdown()

    def _loop(self) -> None:
        assert self._host is not None
        self._running = True

        while self._running:
            try:
                message = self._host.recv(timeout=self._next_timeout())
            except HostTimeout:
                self._run_timers()
                continue
            except HostExited as exc:
                self._running = False
                if self._graceful:
                    break
                detail = f"\n宿主 stderr:\n{exc.stderr}" if exc.stderr else ""
                raise HostExited(
                    f"宿主进程意外退出（exit={exc.exit_code}）{detail}",
                    exc.exit_code,
                    exc.stderr,
                ) from exc

            self._dispatch(message)
            self._run_timers()

    def _dispatch(self, message: dict[str, Any]) -> None:
        kind = message.get("t")
        if kind == "event":
            self._dispatch_event(message)
        elif kind == "error":
            self._dispatch_error(message)
        elif kind == "bye":
            self._graceful = True
            self._running = False
        # ack / ready 无需处理：rev 只是序号，不做同步等待

    def _dispatch_event(self, message: dict[str, Any]) -> None:
        node = self._nodes.get(str(message.get("id", "")))
        if node is None:
            return

        name = message.get("event")
        if not isinstance(name, str):
            return

        data = message.get("data")
        event = Event(
            widget=node,
            name=name,
            data=data if isinstance(data, dict) else {},
            echo=bool(message.get("echo")),
        )

        # 先同步变量，再决定要不要回调——echo 事件也要同步，否则变量会落后
        node._sync_from_event(event)

        if event.echo:
            return

        node._on_event(event)

        handler_id = node._handler_ids.get(name)
        handler = self._handlers.get(handler_id) if handler_id else None
        if handler is None:
            return

        try:
            handler(event)
        except Exception:  # noqa: BLE001 - 回调异常不能杀掉事件循环
            traceback.print_exc()

    def _dispatch_error(self, message: dict[str, Any]) -> None:
        error = UiError(
            str(message.get("code", "internal")),
            str(message.get("message", "")),
            message.get("id"),
        )
        if self._on_error is not None:
            try:
                self._on_error(error)
            except Exception:  # noqa: BLE001
                traceback.print_exc()
        else:
            print(f"[pywinui3] {error}", file=sys.stderr)

    # ------------------------------------------------------------------
    # 定时器
    # ------------------------------------------------------------------

    def after(self, ms: int, callback: Callable[[], None]) -> None:
        """在 ms 毫秒后执行一次。对应 tkinter 的 ``after``。"""
        self._timers.append((time.monotonic() + ms / 1000.0, callback))

    def _next_timeout(self) -> float:
        if not self._timers:
            return POLL_INTERVAL
        nearest = min(deadline for deadline, _ in self._timers)
        return max(0.0, min(POLL_INTERVAL, nearest - time.monotonic()))

    def _run_timers(self) -> None:
        if not self._timers:
            return
        now = time.monotonic()
        due = [(d, fn) for d, fn in self._timers if d <= now]
        if not due:
            return
        self._timers = [(d, fn) for d, fn in self._timers if d > now]
        for _, callback in sorted(due, key=lambda item: item[0]):
            try:
                callback()
            except Exception:  # noqa: BLE001
                traceback.print_exc()

    # ------------------------------------------------------------------
    # 退出
    # ------------------------------------------------------------------

    def quit(self) -> None:
        """请求关闭窗口并退出事件循环。"""
        self._graceful = True
        self._running = False
        if self._host is not None:
            try:
                self._host.send(protocol.close(0))
            except Exception:  # noqa: BLE001 - 退出路径上不抛
                pass

    destroy = quit

    def dialog(self, title: str, body: str, kind: str = "info") -> None:
        """弹出模态对话框。

        会阻塞窗口交互，所以只适合演示和调试；生产界面请用界面内的状态文字。
        同一时刻只能有一个对话框，前一个没关时新的请求会被宿主丢弃。
        """
        if self._host is None:
            return
        try:
            self._host.send(protocol.dialog(title, body, kind))
        except HostExited:
            pass

    def _shutdown(self) -> None:
        host = self._host
        self._host = None
        if host is None:
            return
        if not self._graceful:
            try:
                host.send(protocol.close(0))
            except Exception:  # noqa: BLE001
                pass
        host.close()

    # ------------------------------------------------------------------
    # 内部注册表（供 Widget 调用）
    # ------------------------------------------------------------------

    def _alloc_node_id(self) -> str:
        node_id = f"n{self._node_seq}"
        self._node_seq += 1
        return node_id

    def _register_node(self, widget: Any) -> None:
        self._nodes[widget._id] = widget

    def _unregister_node(self, node_id: str) -> None:
        self._nodes.pop(node_id, None)

    def _register_handler(self, handler: Callable[..., Any]) -> str:
        self._handler_seq += 1
        handler_id = f"h{self._handler_seq}"
        self._handlers[handler_id] = handler
        return handler_id

    def _unregister_handler(self, handler_id: str) -> None:
        self._handlers.pop(handler_id, None)

    def _on_node_added(self, parent: Any, child: Any) -> None:
        """控件树变化时同步给宿主。首次渲染前只改本地状态，不发消息。"""
        if not self._rendered or self._host is None:
            return
        index = len(parent._children) - 1
        ops: list[dict[str, Any]] = []
        child._collect_insert_ops(parent._id, index, ops)
        self._send_patch(ops)

    def _send_patch(self, ops: list[dict[str, Any]]) -> None:
        if not ops or not self._rendered or self._host is None:
            return
        self._rev += 1
        self._host.send(protocol.patch(self._rev, ops))

    def _send_invoke(self, node_id: str, method: str, args: dict[str, Any] | None = None) -> None:
        if self._host is None:
            return
        self._host.send(protocol.invoke(node_id, method, args))

    # ------------------------------------------------------------------
    # 上下文管理
    # ------------------------------------------------------------------

    def __enter__(self) -> App:
        self.start()
        return self

    def __exit__(self, *exc_info: object) -> None:
        self._shutdown()
