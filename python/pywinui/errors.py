"""异常类型。

分三类，对应三种完全不同的处置方式：

- ``ProtocolError``  —— 协议写错了，是**库的 bug**，应该让开发者看见
- ``HostStartError`` / ``HostExited`` —— 宿主进程没起来或死了，是**环境问题**
- ``UiError`` —— 某个具体节点的操作失败，是**用户代码问题**，带 node_id 便于定位
"""

from __future__ import annotations


class PyWinUIError(Exception):
    """所有 pywinui 异常的基类。"""


class ProtocolError(PyWinUIError):
    """消息不符合协议。通常意味着两端版本不匹配或序列化有 bug。"""


class ProtocolVersionError(ProtocolError):
    """握手时发现协议版本不兼容。"""


class HostStartError(PyWinUIError):
    """宿主进程启动失败：可执行文件缺失、握手超时、进程立刻退出等。"""


class HostExited(PyWinUIError):
    """宿主进程在会话中途退出。"""

    def __init__(self, message: str, exit_code: int | None = None, stderr: str | None = None):
        super().__init__(message)
        self.exit_code = exit_code
        self.stderr = stderr


class HostTimeout(PyWinUIError):
    """等待宿主响应超时。"""


class UiError(PyWinUIError):
    """宿主报告的渲染错误，通常指向某个具体节点。"""

    def __init__(self, code: str, message: str, node_id: str | None = None):
        super().__init__(f"[{code}] {message}" + (f" (node={node_id})" if node_id else ""))
        self.code = code
        self.node_id = node_id
        self.raw_message = message
