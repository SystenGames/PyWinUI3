"""扩展控件、query/result 与新增事件面的单元测试（不需要宿主进程）。"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))

import pywinui3 as ui  # noqa: E402
from pywinui3 import protocol  # noqa: E402
from pywinui3.event import Event  # noqa: E402
from pywinui3.extended import __all__ as EXTENDED_NAMES  # noqa: E402


class TestQueryMessage(unittest.TestCase):
    def test_shape(self):
        self.assertEqual(
            protocol.query("q1", "n2", "actualWidth"),
            {"op": "query", "id": "q1", "node": "n2", "key": "actualWidth"},
        )

    def test_optional_args(self):
        self.assertEqual(
            protocol.query("q2", "n2", "selectedItems", {"max": 5}),
            {"op": "query", "id": "q2", "node": "n2", "key": "selectedItems", "args": {"max": 5}},
        )
        self.assertNotIn("args", protocol.query("q3", "n2", "text", None))

    def test_roundtrip_on_wire(self):
        message = protocol.query("q9", "n3", "windowState")
        self.assertEqual(protocol.decode(protocol.encode(message)), message)

    def test_result_is_decodable(self):
        for value in (128.0, "Button", True, None, {"x": 1, "y": 2}, ["一", "二"]):
            line = '{"t":"result","id":"q1","ok":true,"value":%s}' % __import__("json").dumps(
                value, ensure_ascii=False
            )
            self.assertEqual(protocol.decode(line)["value"], value)

    def test_query_without_host_raises(self):
        with self.assertRaises(RuntimeError):
            ui.App().query("n0", "windowState")


class TestExtendedExports(unittest.TestCase):
    def test_all_names_exported(self):
        for name in EXTENDED_NAMES:
            self.assertTrue(hasattr(ui, name), f"{name} 没有从包顶层导出")

    def test_extended_count(self):
        self.assertEqual(len(EXTENDED_NAMES), 33)

    def test_no_duplicate_exports(self):
        self.assertEqual(len(ui.__all__), len(set(ui.__all__)))

    def test_type_names(self):
        expected = {
            "NumberBox": "NumberBox",
            "NavigationView": "NavigationView",
            "InfoBar": "InfoBar",
            "BorderBox": "BorderBox",
            "RichEditBox": "RichEditBox",
            "TwoPaneView": "TwoPaneView",
        }
        for class_name, type_name in expected.items():
            self.assertEqual(getattr(ui, class_name)._type, type_name)


class TestExtendedProps(unittest.TestCase):
    def setUp(self):
        self.app = ui.App()
        self.win = ui.Window(self.app, title="test")

    def test_number_box_camelizes_and_skips_infinity(self):
        box = ui.NumberBox(self.win, value=42, min=0, max=100, step_frequency=2)
        self.assertEqual(box._props["value"], 42)
        self.assertEqual(box._props["min"], 0)
        self.assertEqual(box._props["max"], 100)
        self.assertEqual(box._props["stepFrequency"], 2)

        default_box = ui.NumberBox(self.win)
        self.assertNotIn("min", default_box._props)
        self.assertNotIn("max", default_box._props)
        self.assertIn("stepFrequency", default_box._props)

    def test_value_property_sends_patch_shape(self):
        box = ui.NumberBox(self.win, value=1)
        box.value = 9
        self.assertEqual(box._props["value"], 9)
        self.assertEqual(box.get(), 9)

    def test_items_are_lists(self):
        view = ui.GridView(self.win, items=["a", "b"], selection_mode="multiple")
        self.assertEqual(view._props["items"], ["a", "b"])
        self.assertEqual(view._props["selectionMode"], "multiple")
        self.assertEqual(view.selected_index, -1)

    def test_tree_items_nested(self):
        tree = ui.TreeView(self.win, items=[{"label": "根", "children": ["子"]}])
        self.assertEqual(tree._props["items"][0]["label"], "根")

    def test_info_bar_fields(self):
        bar = ui.InfoBar(self.win, title="标题", message="正文", severity="warning")
        self.assertEqual(bar._props["title"], "标题")
        self.assertEqual(bar._props["message"], "正文")
        self.assertEqual(bar._props["severity"], "warning")

    def test_expander_var_field_is_expanded(self):
        exp = ui.Expander(self.win, header="更多", is_expanded=True)
        self.assertEqual(exp._props["isExpanded"], True)
        event = Event(widget=exp, name="change", data={"expanded": False}, echo=False)
        exp._sync_from_event(event)
        self.assertFalse(exp.value)

    def test_change_event_syncs_number_box(self):
        box = ui.NumberBox(self.win, value=1)
        box._sync_from_event(Event(widget=box, name="change", data={"value": 7.5}, echo=False))
        self.assertEqual(box.value, 7.5)

    def test_container_accepts_children(self):
        box = ui.BorderBox(self.win, background="#FFF0F0F0")
        label = ui.Label(box, "子控件")
        self.assertIn(label, box._children)

    def test_user_height_overrides_defaults(self):
        """默认 height 不能和用户显式传的 height 撞车（曾导致示例直接崩）。"""
        cases = [
            (ui.RichEditBox, {"text": "x", "height": 90}),
            (ui.CalendarView, {"date": "2026-11-11", "height": 180}),
            (ui.GridView, {"items": ["a"], "height": 150}),
            (ui.TreeView, {"items": ["a"], "height": 170}),
            (ui.NavigationView, {"items": ["a"], "height": 300}),
            (ui.RelativePanel, {"height": 100}),
            (ui.TwoPaneView, {"height": 100}),
            (ui.RefreshContainer, {"height": 100}),
        ]
        for cls, kwargs in cases:
            with self.subTest(cls=cls.__name__):
                widget = cls(self.win, **kwargs)
                self.assertEqual(widget._props["height"], kwargs["height"])

    def test_specs_use_extended_type(self):
        box = ui.BorderBox(self.win)
        specs: list[dict] = []
        box._collect_specs(specs)
        self.assertEqual(specs[0]["type"], "BorderBox")


class TestNewConveniences(unittest.TestCase):
    def setUp(self):
        self.app = ui.App()
        self.win = ui.Window(self.app, title="test")

    def test_event_shortcuts_use_expected_names(self):
        entry = ui.Entry(self.win)
        checks = {
            "on_key_down": "keyDown",
            "on_key_up": "keyUp",
            "on_loaded": "loaded",
            "on_unloaded": "unloaded",
            "on_size_changed": "sizeChanged",
            "on_got_focus": "gotFocus",
            "on_lost_focus": "lostFocus",
            "on_drop": "drop",
        }
        for method, event_name in checks.items():
            getattr(entry, method)(lambda e: None)
            self.assertIn(event_name, entry._handler_ids)

    def test_window_event_shortcuts(self):
        self.win.on_activate(lambda e: None)
        self.win.on_state_changed(lambda e: None)
        self.assertIn("windowActivated", self.win._handler_ids)
        self.assertIn("windowStateChanged", self.win._handler_ids)

    def test_webview_script_helpers_exist(self):
        web = ui.WebView2(self.win)
        self.assertTrue(callable(web.execute_script))
        self.assertTrue(callable(web.on_script_result))
        self.assertIn("sourceChanged", web._handler_ids)

    def test_dialog_kind_is_sent(self):
        self.assertEqual(protocol.dialog("a", "b", "error")["kind"], "error")


if __name__ == "__main__":
    unittest.main()
