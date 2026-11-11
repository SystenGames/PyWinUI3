"""宿主端到端冒烟测试。

刻意不 import pywinui3：直接按 docs/protocol.md 手写 JSON 与宿主对话。
这样测的是「宿主有没有正确实现协议」，而不是「Python 库能不能自洽」。

用法：
    python tests/smoke_render.py                 # 跑一遍，3 秒后关闭
    python tests/smoke_render.py --hold 15       # 渲染后保持 15 秒，便于截图
    python tests/smoke_render.py --exe <path>    # 指定宿主可执行文件
"""

from __future__ import annotations

import argparse
import ctypes
import json
import queue
import subprocess
import sys
import threading
import time
from ctypes import wintypes
from pathlib import Path

HOST_EXE = "PyWinUI.Host.exe"
PROTOCOL_VERSION = 1


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
        self.exe = exe
        self.proc = subprocess.Popen(
            [str(exe)],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            bufsize=0,
            creationflags=0x08000000,  # CREATE_NO_WINDOW
        )
        self._queue: queue.Queue[str | None] = queue.Queue()
        threading.Thread(target=self._read_loop, daemon=True).start()
        threading.Thread(target=self._stderr_loop, daemon=True).start()

    def _read_loop(self) -> None:
        assert self.proc.stdout is not None
        for raw in self.proc.stdout:
            self._queue.put(raw.decode("utf-8", "replace").strip())
        self._queue.put(None)

    def _stderr_loop(self) -> None:
        assert self.proc.stderr is not None
        for raw in self.proc.stderr:
            line = raw.decode("utf-8", "replace").rstrip()
            if line:
                print(f"  [host stderr] {line}")

    def send(self, message: dict) -> None:
        assert self.proc.stdin is not None
        payload = (json.dumps(message, ensure_ascii=False) + "\n").encode("utf-8")
        self.proc.stdin.write(payload)
        self.proc.stdin.flush()

    def recv(self, timeout: float = 10.0) -> dict:
        try:
            item = self._queue.get(timeout=timeout)
        except queue.Empty:
            raise TimeoutError("等待宿主响应超时") from None
        if item is None:
            raise RuntimeError("宿主已关闭连接")
        return json.loads(item)

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


def list_windows(pid: int) -> list[tuple[int, bool, str, int, int]]:
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    enum_proc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    found: list[tuple[int, bool, str, int, int]] = []

    def callback(hwnd, _lparam):
        owner = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid:
            length = user32.GetWindowTextLengthW(hwnd)
            buf = ctypes.create_unicode_buffer(length + 1)
            user32.GetWindowTextW(hwnd, buf, length + 1)
            rect = wintypes.RECT()
            user32.GetWindowRect(hwnd, ctypes.byref(rect))
            found.append(
                (hwnd, bool(user32.IsWindowVisible(hwnd)), buf.value,
                 rect.right - rect.left, rect.bottom - rect.top)
            )
        return True

    user32.EnumWindows(enum_proc(callback), 0)
    return [w for w in found if w[1]]


def build_nodes() -> tuple[str, list[dict]]:
    """一张覆盖主要控件的测试表单。"""
    nodes = [
        {
            "id": "n0", "type": "Column",
            "props": {"padding": 32, "spacing": 14, "background": "#F3F3F3"},
            "children": ["n1", "n2", "n3", "n4", "n7"],
        },
        {"id": "n1", "type": "Label",
         "props": {"text": "PyWinUI3", "fontSize": 34, "weight": "semibold"}},
        {"id": "n2", "type": "Label",
         "props": {"text": "由 Python 描述界面，C# 宿主用 WinUI 3 渲染。",
                   "fontSize": 15, "foreground": "#5A5A5A", "wrap": True}},
        {"id": "n3", "type": "Entry",
         "props": {"text": "输入点什么", "placeholder": "占位文本"}},
        {"id": "n4", "type": "Row", "props": {"spacing": 10},
         "children": ["n5", "n6"]},
        {"id": "n5", "type": "Button",
         "props": {"text": "点我"}, "events": {"click": "h1"}},
        {"id": "n6", "type": "Checkbox",
         "props": {"text": "勾选项", "checked": True},
         "events": {"change": "h2"}},
        {"id": "n7", "type": "ProgressBar",
         "props": {"value": 45, "min": 0, "max": 100}},
    ]
    return "n0", nodes


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", type=Path, default=None)
    parser.add_argument("--hold", type=float, default=3.0,
                        help="渲染完成后保持窗口的秒数")
    args = parser.parse_args()

    exe = args.exe or default_exe()
    print(f"host: {exe}")

    failures: list[str] = []

    def check(label: str, ok: bool, detail: str = "") -> None:
        print(f"  {'PASS' if ok else 'FAIL'}  {label}{'  ' + detail if detail else ''}")
        if not ok:
            failures.append(label)

    host = Host(exe)
    try:
        # --- 握手 ---
        print("\n[1] handshake")
        host.send({"op": "hello", "protocol": PROTOCOL_VERSION,
                   "client": "smoke/0.1", "pid": 0})
        ready = host.recv()
        check("收到 ready", ready.get("t") == "ready", str(ready.get("t")))
        check("协议版本一致", ready.get("protocol") == PROTOCOL_VERSION,
              str(ready.get("protocol")))
        caps = ready.get("capabilities") or {}
        print(f"        capabilities = {caps}")

        # --- 全量渲染 ---
        print("\n[2] render")
        root, nodes = build_nodes()
        host.send({
            "op": "render", "rev": 1,
            "window": {"title": "PyWinUI3 渲染测试", "width": 720, "height": 460,
                       "center": True, "backdrop": "auto"},
            "root": root,
            "nodes": nodes,
        })
        ack = host.recv()
        check("收到 ack", ack.get("t") == "ack", str(ack.get("t")))
        check("rev 匹配", ack.get("rev") == 1, str(ack.get("rev")))

        time.sleep(1.5)
        windows = list_windows(host.proc.pid)
        check("窗口已创建", len(windows) > 0, f"{len(windows)} 个可见窗口")
        if windows:
            _hwnd, _visible, title, w, h = windows[0]
            print(f"        title={title!r} size={w}x{h}")
            check("标题正确", title == "PyWinUI3 渲染测试", title)

        # --- 增量补丁 ---
        print("\n[3] patch")
        host.send({
            "op": "patch", "rev": 2,
            "ops": [
                {"t": "set", "id": "n1", "props": {"text": "补丁生效了"}},
                {"t": "set", "id": "n7", "props": {"value": 88}},
                {"t": "set", "id": "n3", "props": {"text": "程序设置的值"}},
            ],
        })
        ack2 = host.recv()
        check("patch 收到 ack", ack2.get("t") == "ack", str(ack2.get("t")))
        check("rev=2", ack2.get("rev") == 2, str(ack2.get("rev")))

        # --- 插入节点 ---
        print("\n[4] insert")
        host.send({
            "op": "patch", "rev": 3,
            "ops": [{
                "t": "insert", "parent": "n0", "index": 7,
                "node": {"id": "n8", "type": "Label",
                         "props": {"text": "这是运行中插入的一行", "fontSize": 13}},
            }],
        })
        ack3 = host.recv()
        check("insert 收到 ack", ack3.get("t") == "ack", str(ack3.get("t")))

        # --- 保持窗口以便外部截图 ---
        if args.hold > 0:
            print(f"\n[5] 保持窗口 {args.hold:.0f} 秒供截图…")
            time.sleep(args.hold)

        # --- 关闭 ---
        print("\n[6] close")
        host.send({"op": "close", "code": 0})
        try:
            bye = host.recv(timeout=5)
            check("收到 bye", bye.get("t") == "bye", str(bye.get("t")))
        except (TimeoutError, RuntimeError) as exc:
            check("收到 bye", False, str(exc))

    finally:
        host.close()

    print("\n" + "=" * 50)
    if failures:
        print(f"FAILED: {len(failures)} 项未通过 -> {failures}")
        return 1

    print("ALL PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
