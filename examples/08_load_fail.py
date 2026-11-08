"""加载 → 失败。

故意发一张「根节点不存在」的表单，用来看两件事：

1. 加载圈有 0.5 秒保底，不会闪一下就没
2. 失败时宿主显示的错误界面长什么样

正常写界面不会遇到这种表单，这个示例就是为了看失败态。

运行：``python 08_load_fail.py``，它会自己退出。
"""

from __future__ import annotations

import time

import pywinui as ui
from pywinui import protocol


def main() -> None:
    # 用 with 而不是 mainloop：这里没有控件树，只是想手工发一条协议消息
    with ui.App() as app:
        app._host.send(
            protocol.render(
                1,
                {"title": "加载失败演示", "width": 620, "height": 420},
                # root 指向一个根本不存在的节点，宿主的防御逻辑会拦住
                "n404",
                [
                    {
                        "id": "n0",
                        "type": "Column",
                        "props": {"padding": 20, "spacing": 8},
                        "children": ["n1"],
                    },
                    {
                        "id": "n1",
                        "type": "Label",
                        "props": {"text": "这棵树不会被显示，因为 root 指向了 n404"},
                    },
                ],
            )
        )

        time.sleep(3)


if __name__ == "__main__":
    main()
