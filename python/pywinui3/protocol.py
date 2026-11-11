"""协议编解码。

本模块是 docs/protocol.md 在 Python 侧的实现，必须与 C# 的
``PyWinUI.Host.Protocol`` 完全对称。改动任何字段都要同步改两端。

设计上刻意把「消息构造」做成普通函数而不是类：消息就是 dict，
序列化只有一种正确形态，这样抓到的管道日志和代码一一对应，排查成本最低。
"""

from __future__ import annotations

import json
from typing import Any, Iterable

PROTOCOL_VERSION = 1
CLIENT_ID = "pywinui3/0.1.0"

#: 单行消息字节上限，与 C# 侧 Wire.MaxLineBytes 一致。
MAX_LINE_BYTES = 8 * 1024 * 1024

_encode = lambda obj: json.dumps(obj, ensure_ascii=False, separators=(",", ":"))


def encode(message: dict[str, Any]) -> bytes:
    """把消息编码成一行字节（含换行符），可直接写入管道。"""
    line = _encode(message).encode("utf-8")
    if len(line) > MAX_LINE_BYTES:
        raise ValueError(f"消息长度 {len(line)} 超过上限 {MAX_LINE_BYTES}")
    return line + b"\n"


def decode(line: str | bytes) -> dict[str, Any]:
    """解析一行消息。"""
    from .errors import ProtocolError

    if isinstance(line, bytes):
        try:
            line = line.decode("utf-8")
        except UnicodeDecodeError as exc:
            raise ProtocolError(f"消息不是合法 UTF-8: {exc}") from exc

    line = line.strip()
    if not line:
        raise ProtocolError("收到空行")

    try:
        message = json.loads(line)
    except json.JSONDecodeError as exc:
        raise ProtocolError(f"消息不是合法 JSON: {exc}") from exc

    if not isinstance(message, dict):
        raise ProtocolError("消息必须是 JSON 对象")
    return message


# --------------------------------------------------------------------------
# Python -> Host
# --------------------------------------------------------------------------


def hello(pid: int, client: str = CLIENT_ID) -> dict[str, Any]:
    return {"op": "hello", "protocol": PROTOCOL_VERSION, "client": client, "pid": pid}


def render(
    rev: int,
    window: dict[str, Any],
    root: str,
    nodes: Iterable[dict[str, Any]],
) -> dict[str, Any]:
    return {
        "op": "render",
        "rev": rev,
        "window": window,
        "root": root,
        "nodes": list(nodes),
    }


def patch(rev: int, ops: Iterable[dict[str, Any]]) -> dict[str, Any]:
    return {"op": "patch", "rev": rev, "ops": list(ops)}


def invoke(node_id: str, method: str, args: dict[str, Any] | None = None) -> dict[str, Any]:
    message: dict[str, Any] = {"op": "invoke", "id": node_id, "method": method}
    if args:
        message["args"] = args
    return message


def dialog(title: str, body: str, kind: str = "info") -> dict[str, Any]:
    """弹出模态对话框。会阻塞窗口交互，适合调试与演示。"""
    return {"op": "dialog", "title": title, "body": body, "kind": kind}


def close(code: int = 0) -> dict[str, Any]:
    return {"op": "close", "code": code}


# --------------------------------------------------------------------------
# patch 操作构造
# --------------------------------------------------------------------------


def op_set(
    node_id: str,
    props: dict[str, Any] | None = None,
    events: dict[str, str] | None = None,
) -> dict[str, Any]:
    """设置节点属性；``events`` 非 None 时整体替换该节点的事件绑定。"""
    op: dict[str, Any] = {"t": "set", "id": node_id, "props": props or {}}
    if events is not None:
        op["events"] = events
    return op


def op_insert(parent: str, index: int, node: dict[str, Any]) -> dict[str, Any]:
    return {"t": "insert", "parent": parent, "index": index, "node": node}


def op_remove(node_id: str) -> dict[str, Any]:
    return {"t": "remove", "id": node_id}


def op_move(node_id: str, parent: str, index: int) -> dict[str, Any]:
    return {"t": "move", "id": node_id, "parent": parent, "index": index}


def op_window(props: dict[str, Any]) -> dict[str, Any]:
    return {"t": "window", "props": props}


# --------------------------------------------------------------------------
# Host -> Python 的取值辅助
# --------------------------------------------------------------------------


def message_type(message: dict[str, Any]) -> str:
    """返回出站消息的 ``t`` 字段。"""
    value = message.get("t")
    if not isinstance(value, str):
        raise ValueError(f"出站消息缺少 t 字段: {message!r}")
    return value


def node(
    node_id: str,
    type_: str,
    props: dict[str, Any] | None = None,
    children: list[str] | None = None,
    events: dict[str, str] | None = None,
) -> dict[str, Any]:
    """构造一个节点声明，省略空字段以减小报文体积。"""
    spec: dict[str, Any] = {"id": node_id, "type": type_}
    if props:
        spec["props"] = props
    if children:
        spec["children"] = children
    if events:
        spec["events"] = events
    return spec
