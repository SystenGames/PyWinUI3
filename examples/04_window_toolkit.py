"""窗口能力 / 按钮图标 / 图片与媒体 / 悬停提示 / 右键菜单。

跑起来之后可以试这几件事：

- 鼠标停在文字或按钮上，看提示气泡
- 右键点「删除」按钮，选一项，Python 侧打印菜单的 ``value``
- 拖窗口边缘改变大小，Python 打印新的逻辑尺寸
- 关窗时打印 ``closing``

``python 04_window_toolkit.py --auto`` 会自己跑完退出，用来做冒烟验证。
"""

from __future__ import annotations

import base64
import binascii
import os
import struct
import sys
import tempfile
import zlib

import pywinui3 as ui


def _png(width: int, height: int) -> bytes:
    """生成一张 RGBA 渐变 PNG。

    演示 data URI 用：图片是算出来的，不需要仓库里放资源文件。
    """
    rows = bytearray()
    for y in range(height):
        rows.append(0)  # 每行的 filter type
        for x in range(width):
            rows += bytes(((60 + x * 3) % 200, (110 + y * 2) % 180, 200, 255))

    def chunk(kind: bytes, data: bytes) -> bytes:
        return (
            struct.pack(">I", len(data))
            + kind
            + data
            + struct.pack(">I", binascii.crc32(kind + data) & 0xFFFFFFFF)
        )

    ihdr = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", ihdr)
        + chunk(b"IDAT", zlib.compress(bytes(rows), 6))
        + chunk(b"IEND", b"")
    )


def _ico(png: bytes, size: int = 32) -> bytes:
    """把 PNG 包成单图 ICO。Windows 支持 PNG 压缩的图标条目。"""
    header = struct.pack("<HHH", 0, 1, 1)
    entry = struct.pack("<BBBBHHII", size, size, 0, 0, 1, 32, len(png), 22)
    return header + entry + png


def main() -> None:
    icon_path = os.path.join(tempfile.gettempdir(), "pywinui3-demo.ico")
    with open(icon_path, "wb") as handle:
        handle.write(_ico(_png(32, 32)))

    win = ui.Window(
        title="PyWinUI3 能力演示",
        width=780,
        height=660,
        x=140,
        y=90,
        icon=icon_path,
        min_width=560,
        min_height=460,
    )

    col = ui.Column(win, padding=20, spacing=14)
    ui.Label(col, "窗口 · 按钮 · 图片 · 媒体", font_size=24, weight="semibold")

    # ---- 按钮：图标 + 右键菜单 ----
    row = ui.Row(col, spacing=10)

    save = ui.Button(row, "保存", icon="\uE74E", variant="accent")
    save.set_tooltip("把当前内容存盘")

    delete = ui.Button(row, "删除", icon="\uE74D")
    delete.set_menu(
        [
            {"label": "移到废纸篓", "glyph": "\uE74D", "value": "trash"},
            {"label": "彻底删除", "glyph": "\uE711", "value": "purge"},
            "-",
            {"label": "撤销", "glyph": "\uE7A7", "value": "undo", "enabled": False},
        ]
    )

    ui.HyperlinkButton(row, "项目主页", url="https://github.com/", icon="\uE774")

    # ---- 悬停提示 ----
    hint = ui.Label(col, "把鼠标停在这行字上，会浮出提示", tooltip="这是 tooltip，来自 tooltip 属性")
    hint.set_menu([{"label": "复制文字", "value": "copy-text"}])

    # ---- 图片：data URI ----
    ui.Label(col, "内存里生成的图片（data URI，未落盘）", weight="semibold")
    ui.Image(
        col,
        src="data:image/png;base64," + base64.b64encode(_png(160, 90)).decode("ascii"),
        height=110,
        stretch="uniform",
        h_align="left",
        tooltip="这张图是 Python 现算出来的",
    )

    # ---- 图片：故意给个坏路径，验证 imageFailed ----
    broken = ui.Image(col, src=r"Z:\nope\missing.png", height=48, stretch="uniform", h_align="left")
    broken.on_error(lambda e: print(f"[imageFailed] {e.data.get('error')}", flush=True))

    # ---- 媒体 ----
    video = r"C:\Users\Administrator\Videos\202402092217.mp4"
    if os.path.exists(video):
        player = ui.Media(col, src=video, height=240, stretch="uniform")
        player.loop = True
        player.volume = 0.4
        player.playback_rate = 1.0
        player.set_tooltip("右键这里也能测试菜单")
        player.set_menu([{"label": "从头播放", "value": "restart"}, {"label": "静音", "value": "mute"}])
        player.on_menu(lambda e: print(f"[menu] {e.data['value']}", flush=True))
        player.bind("mediaOpened", lambda e: print(f"[mediaOpened] {e.data['duration']:.1f}s", flush=True))
        player.bind("mediaFailed", lambda e: print(f"[mediaFailed] {e.data['error']}", flush=True))
    else:
        ui.Label(col, f"（找不到示例视频 {video}，已跳过媒体）", opacity=0.6)

    # ---- 窗口事件 ----
    win.on_resize(lambda e: print(f"[sizeChanged] {e.data['width']}x{e.data['height']}", flush=True))
    win.on_close(lambda e: print("[closing] 窗口即将关闭", flush=True))

    for widget in (save, delete):
        widget.on_menu(lambda e: print(f"[menu] {e.data['value']}", flush=True))

    if "--auto" in sys.argv:
        win.after(2500, lambda: setattr(win, "width", 900))
        win.after(6000, win.quit)

    win.mainloop()


if __name__ == "__main__":
    main()
