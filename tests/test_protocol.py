"""协议层单元测试。

这些测试不需要宿主进程，也不需要 Windows UI，可以在任何机器上跑：

    python -m unittest discover -s tests -v
"""

from __future__ import annotations

import json
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))

from pywinui import protocol  # noqa: E402
from pywinui.errors import ProtocolError  # noqa: E402


class TestEncode(unittest.TestCase):
    def test_single_line_with_trailing_newline(self):
        data = protocol.encode({"op": "close", "code": 0})
        self.assertTrue(data.endswith(b"\n"))
        self.assertEqual(data.count(b"\n"), 1)

    def test_no_bom(self):
        data = protocol.encode({"op": "close", "code": 0})
        self.assertFalse(data.startswith(b"\xef\xbb\xbf"))

    def test_chinese_is_not_escaped(self):
        """中文必须原样输出，否则管道日志全是 \\uXXXX，排查成本极高。"""
        data = protocol.encode({"op": "render", "root": "你好"})
        self.assertIn("你好".encode("utf-8"), data)
        self.assertNotIn(b"\\u", data)

    def test_no_spaces(self):
        """紧凑输出，减少管道传输量。"""
        data = protocol.encode({"op": "close", "code": 0})
        self.assertNotIn(b", ", data)
        self.assertNotIn(b": ", data)


class TestDecode(unittest.TestCase):
    def test_roundtrip(self):
        original = {"op": "patch", "rev": 3, "ops": [{"t": "remove", "id": "n1"}]}
        self.assertEqual(protocol.decode(protocol.encode(original)), original)

    def test_accepts_bytes(self):
        self.assertEqual(protocol.decode(b'{"t":"ack","rev":1}'), {"t": "ack", "rev": 1})

    def test_strips_whitespace(self):
        self.assertEqual(protocol.decode('  {"t":"bye","code":0}  \n'), {"t": "bye", "code": 0})

    def test_empty_line(self):
        with self.assertRaises(ProtocolError):
            protocol.decode("   ")

    def test_malformed_json(self):
        with self.assertRaises(ProtocolError):
            protocol.decode("{not json}")

    def test_non_object(self):
        with self.assertRaises(ProtocolError):
            protocol.decode("[1,2,3]")

    def test_invalid_utf8(self):
        with self.assertRaises(ProtocolError):
            protocol.decode(b"\xff\xfe\x00")


class TestMessages(unittest.TestCase):
    def test_hello(self):
        msg = protocol.hello(pid=1234)
        self.assertEqual(msg["op"], "hello")
        self.assertEqual(msg["protocol"], protocol.PROTOCOL_VERSION)
        self.assertEqual(msg["pid"], 1234)
        self.assertIn("pywinui", msg["client"])

    def test_render(self):
        node = protocol.node("n1", "Label", {"text": "hi"})
        msg = protocol.render(1, {"title": "T"}, "n1", [node])
        self.assertEqual(msg["op"], "render")
        self.assertEqual(msg["root"], "n1")
        self.assertEqual(len(msg["nodes"]), 1)
        self.assertEqual(msg["nodes"][0]["type"], "Label")

    def test_patch(self):
        msg = protocol.patch(2, [protocol.op_set("n1", {"text": "x"})])
        self.assertEqual(msg["rev"], 2)
        self.assertEqual(msg["ops"][0]["t"], "set")

    def test_invoke_omits_empty_args(self):
        self.assertNotIn("args", protocol.invoke("n1", "focus"))
        self.assertIn("args", protocol.invoke("n1", "scroll_to", {"index": 0}))

    def test_close(self):
        self.assertEqual(protocol.close(), {"op": "close", "code": 0})


class TestPatchOps(unittest.TestCase):
    def test_op_set_without_events(self):
        op = protocol.op_set("n1", {"text": "a"})
        self.assertEqual(op, {"t": "set", "id": "n1", "props": {"text": "a"}})
        self.assertNotIn("events", op)

    def test_op_set_with_events(self):
        """事件绑定走 set 的 events 字段，不混进 props。"""
        op = protocol.op_set("n1", events={"click": "h1"})
        self.assertEqual(op["events"], {"click": "h1"})
        self.assertEqual(op["props"], {})
        self.assertNotIn("__events__", json.dumps(op))

    def test_op_set_empty_events_means_unbind_all(self):
        op = protocol.op_set("n1", events={})
        self.assertIn("events", op)
        self.assertEqual(op["events"], {})

    def test_op_insert(self):
        node = protocol.node("n2", "Label")
        op = protocol.op_insert("n1", 0, node)
        self.assertEqual(op["t"], "insert")
        self.assertEqual(op["parent"], "n1")
        self.assertEqual(op["index"], 0)

    def test_op_remove(self):
        self.assertEqual(protocol.op_remove("n3"), {"t": "remove", "id": "n3"})

    def test_op_move(self):
        self.assertEqual(
            protocol.op_move("n2", "n1", 3),
            {"t": "move", "id": "n2", "parent": "n1", "index": 3},
        )

    def test_op_window(self):
        self.assertEqual(protocol.op_window({"title": "X"}), {"t": "window", "props": {"title": "X"}})


class TestNodeBuilder(unittest.TestCase):
    def test_omits_empty_fields(self):
        node = protocol.node("n1", "Label")
        self.assertEqual(node, {"id": "n1", "type": "Label"})
        self.assertNotIn("props", node)
        self.assertNotIn("children", node)

    def test_keeps_children_and_events(self):
        node = protocol.node("n1", "Column", children=["n2"], events={"click": "h1"})
        self.assertEqual(node["children"], ["n2"])
        self.assertEqual(node["events"], {"click": "h1"})


class TestMessageType(unittest.TestCase):
    def test_returns_t(self):
        self.assertEqual(protocol.message_type({"t": "ack"}), "ack")

    def test_missing_t(self):
        with self.assertRaises(ValueError):
            protocol.message_type({"op": "close"})


if __name__ == "__main__":
    unittest.main(verbosity=2)
