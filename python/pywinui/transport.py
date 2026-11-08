"""宿主进程与管道传输。

职责边界很窄：只负责启动进程、按行收发消息、检测进程死亡。
不理解任何协议语义——那是 ``App`` 的事。这样它可以脱离 WinUI 单独测试。
"""

from __future__ import annotations

import collections
import os
import queue
import subprocess
import sys
import threading
from pathlib import Path
from typing import Any

from .errors import HostExited, HostStartError, HostTimeout
from .protocol import decode, encode

#: 宿主默认的可执行文件名。
HOST_EXE_NAME = "PyWinUI.Host.exe"

#: 队列里代表「读线程已结束」的哨兵。
_CLOSED = object()

#: 允许通过环境变量覆盖宿主路径，便于开发时指向自定义构建。
HOST_PATH_ENV = "PYWINUI_HOST"


def locate_host() -> Path:
    """定位宿主可执行文件。

    查找顺序：
      1. 环境变量 ``PYWINUI_HOST``
      2. 包内自带的 ``runtime/PyWinUI.Host/``（发布形态）
      3. 源码树里 ``host/PyWinUI.Host/bin/**``（开发形态，取最新构建）
    """
    override = os.environ.get(HOST_PATH_ENV)
    if override:
        path = Path(override)
        if not path.is_file():
            raise HostStartError(f"环境变量 {HOST_PATH_ENV} 指向的文件不存在: {path}")
        return path

    here = Path(__file__).resolve().parent

    bundled = here / "runtime" / "PyWinUI.Host" / HOST_EXE_NAME
    if bundled.is_file():
        return bundled

    root = here.parent.parent
    pattern = f"host/PyWinUI.Host/bin/**/{HOST_EXE_NAME}"
    candidates = sorted(
        root.glob(pattern),
        key=lambda p: p.stat().st_mtime,
        reverse=True,
    )
    if candidates:
        return candidates[0]

    raise HostStartError(
        "找不到 PyWinUI.Host.exe。\n"
        f"  已查找: {bundled}\n"
        f"  已查找: {root / pattern}\n"
        "请先构建宿主（见 tools/build_host.ps1），"
        f"或设置环境变量 {HOST_PATH_ENV} 指向可执行文件。"
    )


class HostProcess:
    """一个宿主进程实例，提供线程安全的消息收发。"""

    def __init__(self, exe: Path | str | None = None, *, extra_args: list[str] | None = None):
        self._exe = Path(exe) if exe else locate_host()
        self._extra_args = extra_args or []
        self._proc: subprocess.Popen[bytes] | None = None
        self._queue: queue.Queue[dict[str, Any] | object] = queue.Queue()
        self._write_lock = threading.Lock()
        self._stderr_tail: collections.deque[str] = collections.deque(maxlen=200)
        self._reader: threading.Thread | None = None
        self._stderr_reader: threading.Thread | None = None
        self._closed = False

    # ---- 生命周期 ----

    def start(self) -> None:
        if self._proc is not None:
            raise RuntimeError("宿主进程已经启动过了")

        if not self._exe.is_file():
            raise HostStartError(f"宿主可执行文件不存在: {self._exe}")

        creationflags = 0
        if sys.platform == "win32":
            # 宿主是 WinExe，本来就没有控制台；加上这个标志是为了杜绝意外闪窗
            creationflags = subprocess.CREATE_NO_WINDOW

        try:
            self._proc = subprocess.Popen(
                [str(self._exe), *self._extra_args],
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                bufsize=0,
                creationflags=creationflags,
            )
        except OSError as exc:
            raise HostStartError(f"无法启动宿主进程 {self._exe}: {exc}") from exc

        self._reader = threading.Thread(
            target=self._read_loop, name="pywinui-reader", daemon=True
        )
        self._reader.start()

        self._stderr_reader = threading.Thread(
            target=self._stderr_loop, name="pywinui-stderr", daemon=True
        )
        self._stderr_reader.start()

    def _read_loop(self) -> None:
        assert self._proc is not None and self._proc.stdout is not None
        stream = self._proc.stdout
        try:
            while True:
                raw = stream.readline()
                if not raw:
                    break
                text = raw.decode("utf-8", errors="replace").strip()
                if not text:
                    continue
                try:
                    self._queue.put(decode(text))
                except Exception:
                    # 单行坏掉不该拖垮整个会话；留痕即可
                    self._stderr_tail.append(f"[pywinui] 无法解析的行: {text[:500]}")
        except Exception as exc:  # noqa: BLE001 - 读线程不能让异常逃逸
            self._stderr_tail.append(f"[pywinui] 读线程异常: {exc}")
        finally:
            self._queue.put(_CLOSED)

    def _stderr_loop(self) -> None:
        assert self._proc is not None and self._proc.stderr is not None
        for raw in iter(self._proc.stderr.readline, b""):
            text = raw.decode("utf-8", errors="replace").rstrip()
            if text:
                self._stderr_tail.append(text)

    # ---- 收发 ----

    def send(self, message: dict[str, Any]) -> None:
        if self._closed:
            raise HostExited("宿主已关闭，无法发送消息")
        assert self._proc is not None and self._proc.stdin is not None
        payload = encode(message)
        with self._write_lock:
            try:
                self._proc.stdin.write(payload)
                self._proc.stdin.flush()
            except (BrokenPipeError, OSError) as exc:
                raise HostExited(
                    "写入宿主管道失败，进程可能已退出", self._exit_code, self.stderr_text
                ) from exc

    def recv(self, timeout: float | None = None) -> dict[str, Any]:
        """取下一条消息。超时抛 HostTimeout，宿主退出抛 HostExited。"""
        try:
            item = self._queue.get(timeout=timeout)
        except queue.Empty as exc:
            raise HostTimeout(f"等待宿主消息超时（{timeout}s）") from exc

        if item is _CLOSED:
            raise HostExited(
                "宿主进程已退出",
                self._exit_code,
                self.stderr_text,
            )
        return item  # type: ignore[return-value]

    def close(self, timeout: float = 3.0) -> None:
        """尽力体面地关掉宿主，失败则强杀。"""
        if self._closed:
            return
        self._closed = True
        proc = self._proc
        if proc is None:
            return

        try:
            if proc.stdin and not proc.stdin.closed:
                proc.stdin.close()
        except OSError:
            pass

        try:
            proc.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            proc.terminate()
            try:
                proc.wait(timeout=2.0)
            except subprocess.TimeoutExpired:
                proc.kill()
                try:
                    proc.wait(timeout=2.0)
                except subprocess.TimeoutExpired:
                    pass

    # ---- 状态 ----

    @property
    def pid(self) -> int | None:
        return self._proc.pid if self._proc else None

    @property
    def alive(self) -> bool:
        return self._proc is not None and self._proc.poll() is None

    @property
    def _exit_code(self) -> int | None:
        return self._proc.poll() if self._proc else None

    @property
    def stderr_text(self) -> str:
        return "\n".join(self._stderr_tail)

    def __enter__(self) -> "HostProcess":
        self.start()
        return self

    def __exit__(self, *exc_info: object) -> None:
        self.close()
