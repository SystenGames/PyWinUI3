"""WebView2 网页视图，以及「没装运行时」时的降级页面。

正常跑（本机装了 WebView2 Runtime，会真的渲染网页）::

    python examples/09_webview2.py

强制验证降级页（不动系统、也不用卸载运行时）::

    set PYWINUI3_WEBVIEW2_FALLBACK=1 && python examples/09_webview2.py

降级时网页区域会变成灰色矩形：居中「加载失败」，下方一行
「该控件无法正常加载：Webview2 Runtime未找到。」，
窗口里其余的按钮和文字照常可用。
"""

from __future__ import annotations

import os
import sys
from pathlib import Path

# 允许从任意目录直接跑这个示例，不依赖事先 pip install
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "python"))

import pywinui3 as ui


def main() -> None:
    forced = os.environ.get("PYWINUI3_WEBVIEW2_FALLBACK") == "1"

    win = ui.Window(
        title="WebView2 演示（强制降级）" if forced else "WebView2 演示",
        width=900,
        height=640,
        x=200,
        y=120,
    )

    col = ui.Column(win, padding=16, spacing=10)
    ui.Label(col, "WebView2 网页视图", font_size=22, weight="semibold")

    if forced:
        ui.Label(
            col,
            "已通过 PYWINUI3_WEBVIEW2_FALLBACK=1 强制判定运行时缺失，下面应显示灰色降级页。",
            wrap=True,
            opacity=0.75,
        )
    else:
        ui.Label(
            col,
            "本机检测到 WebView2 Runtime，下面由 Edge 内核渲染网页。",
            wrap=True,
            opacity=0.75,
        )

    bar = ui.Row(col, spacing=8)
    web = ui.WebView2(col, source="https://www.example.com", height=400)

    ui.Button(bar, "后退", command=web.go_back)
    ui.Button(bar, "前进", command=web.go_forward)
    ui.Button(bar, "刷新", command=web.reload)
    ui.Button(bar, "换到 Bing", command=lambda: web.navigate("https://www.bing.com"))
    ui.Button(bar, "换到 GitHub", command=lambda: web.navigate("https://github.com"))

    ui.Label(
        col,
        "降级时这些按钮都是安全的空操作：不会报错，也不会让窗口崩。",
        opacity=0.6,
        wrap=True,
    )

    win.mainloop()


if __name__ == "__main__":
    main()
