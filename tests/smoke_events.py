"""事件回传冒烟测试。

验证「用户点击 → 宿主捕获 → 回传 Python」这条链路。
用 Win32 直接搬运鼠标，不依赖 pyautogui，避免测试脚本引入额外依赖。

用法：
    python tests/smoke_events.py
"""

from __future__ import annotations

import ctypes
import json
import queue
import subprocess
import sys
import threading
import time
from ctypes import wintypes
from pathlib import Path

PROTOCOL_VERSION = 1
HOST_EXE = "PyWinUI.Host.exe"

user32 = ctypes.WinDLL("user32", use_last_error=True)


def default_exe() -> Path:
    root = Path(__file__).resolve().parents[1]
    candidates = sorted(
        root.glob(f"host/PyWinUI.Host/bin/**/{HOST_EXE}"),
        key=lambda p: p.stat().st_mtime,
        reverse=True,
    )
    if not candidates:
        raise SystemExit(f"找不到 {HOST_EXE}，先 dotnet build")
    return candidates[0]


class Host:
    def __init__(self, exe: Path):
        self.proc = subprocess.Popen(
            [str(exe)],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            bufsize=0,
            creationflags=0x08000000,
        )
        self._queue: queue.Queue[str | None] = queue.Queue()
        threading.Thread(target=self._read_loop, daemon=True).start()

    def _read_loop(self) -> None:
        assert self.proc.stdout is not None
        for raw in self.proc.stdout:
            self._queue.put(raw.decode("utf-8", "replace").strip())
        self._queue.put(None)

    def send(self, message: dict) -> None:
        assert self.proc.stdin is not None
        self.proc.stdin.write((json.dumps(message, ensure_ascii=False) + "\n").encode("utf-8"))
        self.proc.stdin.flush()

    def recv(self, timeout: float = 10.0) -> dict:
        try:
            item = self._queue.get(timeout=timeout)
        except queue.Empty:
            raise TimeoutError("等待宿主消息超时") from None
        if item is None:
            raise RuntimeError("宿主已关闭连接")
        return json.loads(item)

    def wait_event(self, name: str, timeout: float = 8.0) -> dict:
        """一直读到指定事件为止，沿途的 ack 等消息跳过。"""
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            message = self.recv(timeout=max(0.1, deadline - time.monotonic()))
            if message.get("t") == "event" and message.get("event") == name:
                return message
        raise TimeoutError(f"没有等到事件 {name}")

    def close(self) -> None:
        try:
            if self.proc.stdin:
                self.proc.stdin.close()
        except OSError:
            pass
        try:
            self.proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=3)
            except subprocess.TimeoutExpired:
                self.proc.kill()


def find_window(pid: int) -> tuple[int, int, int, int, int] | None:
    enum_proc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    result: list[tuple[int, int, int, int, int]] = []

    def callback(hwnd, _lparam):
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            rect = wintypes.RECT()
            user32.GetWindowRect(hwnd, ctypes.byref(rect))
            result.append((hwnd, rect.left, rect.top, rect.right, rect.bottom))
        return True

    user32.EnumWindows(enum_proc(callback), 0)
    return result[0] if result else None


def click(x: int, y: int) -> None:
    user32.SetCursorPos(x, y)
    time.sleep(0.15)
    user32.mouse_event(0x0002, 0, 0, 0, 0)  # LEFTDOWN
    time.sleep(0.05)
    user32.mouse_event(0x0004, 0, 0, 0, 0)  # LEFTUP


def main() -> int:
    exe = default_exe()
    print(f"host: {exe}")

    failures: list[str] = []

    def check(label: str, ok: bool, detail: str = "") -> None:
        print(f"  {'PASS' if ok else 'FAIL'}  {label}{'  ' + detail if detail else ''}")
        if not ok:
            failures.append(label)

    host = Host(exe)
    try:
        host.send({"op": "hello", "protocol": PROTOCOL_VERSION, "client": "smoke/0.1"})
        check("握手", host.recv().get("t") == "ready")

        # 按钮放在客户区左上角，位置可预测，方便算点击坐标
        host.send({
            "op": "render", "rev": 1,
            "window": {"title": "PyWinUI3 事件测试", "width": 480, "height": 320},
            "root": "n0",
            "nodes": [
                {"id": "n0", "type": "Column", "props": {"padding": 24, "spacing": 16},
                 "children": ["n1", "n2"]},
                {"id": "n1", "type": "Button",
                 "props": {"text": "点我", "width": 240, "height": 72},
                 "events": {"click": "h1"}},
                {"id": "n2", "type": "Label",
                 "props": {"text": "等待点击", "fontSize": 16}},
            ],
        })
        check("渲染", host.recv().get("t") == "ack")

        time.sleep(1.2)
        window = find_window(host.proc.pid)
        if not window:
            check("窗口存在", False)
            return 1

        hwnd, left, top, right, bottom = window
        print(f"        window rect = ({left},{top}) - ({right},{bottom})")
        user32.SetForegroundWindow(hwnd)
        time.sleep(0.4)

        # 标题栏约 32px，内容 padding 24，按钮高 72 → 点在按钮垂直中心
        target_x = left + 24 + 120
        target_y = top + 32 + 24 + 36
        print(f"        click at ({target_x}, {target_y})")
        click(target_x, target_y)

        print("\n[event] 等待 click 回传")
        event = host.wait_event("click", timeout=8.0)
        check("收到 click", True, str(event))
        check("节点 id 正确", event.get("id") == "n1", str(event.get("id")))
        check("携带 rev", isinstance(event.get("rev"), int), str(event.get("rev")))

        host.send({"op": "close", "code": 0})
        try:
            check("收到 bye", host.recv(timeout=5).get("t") == "bye")
        except (TimeoutError, RuntimeError) as exc:
            check("收到 bye", False, str(exc))

    finally:
        host.close()

    print("\n" + "=" * 50)
    if failures:
        print(f"FAILED: {failures}")
        return 1
    print("ALL PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
