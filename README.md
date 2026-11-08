# PyWinUI

用 tkinter 的写法，渲染真正的 WinUI 3 界面。

```python
import pywinui as ui

win = ui.Window(title="Hello", width=640, height=400)
ui.Label(win, "你好，WinUI 3", font_size=28)
ui.Button(win, "点我", command=lambda: print("clicked"))
win.mainloop()
```

---

## 状态

**能跑起来了。** 界面由 Python 描述，C# 宿主渲染，点击事件回传到 Python 回调。

| 模块 | 状态 |
|---|---|
| 通信协议（[docs/protocol.md](docs/protocol.md)） | 完成，两端实现已对齐 |
| Python 侧控件树 / 布局 / 变量绑定 | 完成 |
| 宿主工具链（编译 + 弹窗） | 已验证（Win10 21H2 / .NET 8.0.427 / WinAppSDK 1.8） |
| C# 侧协议解析（`Protocol/`） | 完成 |
| C# 渲染器（控件工厂、属性映射、增量补丁） | 完成 |
| 管道服务端、事件桥 | 完成 |
| 端到端联调 | 已验证（渲染 / patch / insert / click 回传） |

首版能力边界见文末「已知限制」。

---

## 为什么这么做

Python 里的 Windows 桌面 UI 选择很有限：tkinter 老了，PyQt 授权重，Web 套壳要拖一个浏览器。
WinUI 3 是当前 Windows 上原生感最好的框架，但只有 C# / C++ 能用。

于是把两件事拆开：

- **Python 只管描述界面**——控件树、属性、回调，全是普通 Python 对象
- **C# 只管渲染**——一个独立的 WinUI 3 进程，接收界面描述并画出来

两者通过管道传 JSON 通信。

### 为什么是两个进程，而不是 pythonnet 进程内加载

这是整个设计里最关键的取舍。WinUI 3 的 `Application.Start()` 会把调用线程整个吃掉去跑消息泵：

- 用 pythonnet 在 Python 进程里起 CLR 再起 WinUI，Python 主线程就被这个泵占了，
  `mainloop()` 只能靠 hack 模拟，GIL 和 XAML 回调线程交织后死锁极难排查
- UI 层一个异常会直接带走 Python 解释器，用户脚本里任何错误都能让整个进程消失

分成两个进程之后：Python 侧只依赖标准库，`mainloop()` 天然就是「主线程阻塞在事件队列上」，
和 tkinter 心智完全一致；宿主崩了能被检测、能重启，Python 收到的是异常而不是进程消失。

代价是跨进程往返。本地管道单程 0.1–0.5ms，对 UI 交互可以忽略。

### 为什么用 stdin/stdout 而不是命名管道

Python 标准库没有 Windows 命名管道的实现，用 `ctypes` 自己调 Win32 API 太重；
TCP 会占端口还可能被防火墙拦。stdin/stdout 零依赖，而且句柄生命周期天然绑定进程，
对端一死立刻读到 EOF，连心跳包都省了。LSP 用的也是这套。

---

## 架构

```
┌─ Python 进程 ────────────────────────────────┐
│  控件树 API    Patch 引擎    mainloop        │
└──────────────────┬───────────────────────────┘
                   │  JSON Lines / stdin-stdout
┌──────────────────┴───────────────────────────┐
│  协议解析    节点表    渲染器    事件桥       │  C# 宿主进程
└──────────────────┬───────────────────────────┘
                   │
              WinUI 3 / Windows App SDK
```

界面用「表单」描述：一棵扁平的节点列表，父子关系靠 `children` 字段表达。
首帧发全量 `render`，之后只发增量 `patch`：

```json
{"op":"patch","rev":2,"ops":[
  {"t":"set","id":"n1","props":{"text":"Changed"}},
  {"t":"insert","parent":"n0","index":1,"node":{"id":"n3","type":"Label"}}
]}
```

节点有稳定 id，宿主维护 `id → 控件实例` 的映射，补丁按 id 定位。
完整定义见 [docs/protocol.md](docs/protocol.md)。

---

## 项目结构

```
host/PyWinUI.Host/        C# WinUI 3 渲染宿主
  Protocol/               消息 DTO 与解析
  Transport/              stdin/stdout 行分帧（待实现）
  Rendering/              控件工厂、属性映射、增量应用（待实现）
  Interop/                事件回传（待实现）
python/pywinui/           Python 库
  protocol.py             协议编解码
  transport.py            宿主进程与管道
  app.py                  生命周期与事件循环
  window.py / widgets.py / layout.py / variables.py
docs/protocol.md          协议权威定义
examples/                 示例
```

---

## 构建

需要 **.NET SDK 8.0**（仅 SDK，Runtime 不够）。装好后：

```powershell
dotnet build host/PyWinUI.Host/PyWinUI.Host.csproj -c Release
```

输出目录里的 `PyWinUI.Host.exe` 就是渲染宿主。Python 侧会自动在源码树里找它，
也可以用环境变量 `PYWINUI_HOST` 直接指定路径。

> **不要删 `App.xaml`。** 它看起来只是放资源，实际上 XAML 编译器靠它生成
> `IXamlMetadataProvider` 和 `XamlTypeInfo`。少了它，`XamlControlsResources`
> 在构造时会抛「Cannot find a resource with the given key」然后进程以
> `0xC000027B`（stowed exception）直接退出。纯代码工程走不通这条路。

工程已经预置 `WindowsPackageType=None` + `WindowsAppSDKSelfContained=true`，
所以产物是免安装的裸 exe（约 138MB，含完整 WinAppSDK 运行时），
不需要目标机器预装 Windows App Runtime。

Python 包本身没有第三方依赖：

```powershell
pip install -e python
```

---

## API 概览

**窗口与布局**

| 类 | 说明 |
|---|---|
| `App` | 宿主进程与事件循环 |
| `Window` | 顶层窗口，同时是控件树根 |
| `Column` / `Row` | 垂直 / 水平堆叠 |
| `Grid` | 二维网格，子控件用 `.grid(row=, column=)` 定位 |
| `Scroll` | 纵向滚动区，**必须给 `height`**（或放进 Grid 的 `"*"` 行） |
| `Frame` | `Column` 的别名，照顾 tkinter 习惯 |

**控件**

`Label` `Button` `HyperlinkButton` `ToggleButton` `SplitButton` `RepeatButton`
`Entry` `Checkbox` `Radio` `Switch`
`Slider` `ProgressBar` `ProgressRing` `ComboBox` `ListBox` `Image` `Media` `Separator`

**进度指示**

```python
ui.ProgressBar(col, value=45)                     # 确定进度
ui.ProgressBar(col, indeterminate=True)           # 不确定态，光条来回跑
bar.indeterminate = True                          # 运行中切换也行

ui.ProgressRing(col, size=40)                     # 不确定态加载圈
ring.active = False                               # 停下
```

**媒体播放器**

```python
player = ui.Media(col, src=r"C:\videos\demo.mp4", height=360)
player.play()
player.pause()
player.stop()
player.seek(30)              # 跳到第 30 秒；也可以 player.position = 30
player.volume = 0.6
player.loop = True
player.muted = False
player.playback_rate = 1.5   # 0.25–4

player.bind("mediaOpened", lambda e: print("时长", e.data["duration"], "秒"))
player.bind("mediaEnded", lambda e: print("放完了"))
player.bind("mediaFailed", lambda e: print("失败", e.data["error"]))
```

本地路径和 http(s) URL 都支持。`show_controls=True`（默认）用系统自带的
播放/暂停/进度条；关掉就可以用自己的按钮通过 `play()` / `pause()` 控制。

**按钮外观**

```python
ui.Button(col, "普通")
ui.Button(col, "保存", icon="\uE74E")            # 图标 + 文字
ui.Button(col, "强调色", variant="accent")        # 系统强调色样式
ui.Button(col, "图标按钮", icon="\uE713")         # 只有图标
ui.Button(col, "自定义", background="#0F6E56")    # 任意颜色
```

自定义背景色会连同 hover / 按下两个状态一起覆盖——只设 `Background` 的话，
鼠标移上去会变回系统默认色。

**三种特殊按钮**

```python
bold = ui.ToggleButton(col, "加粗", icon="\uE8DD")     # 双态：按下保持，再按弹回
bold.value = True                                    # 程序里改状态
bold.bind("change", lambda e: print(e.data["checked"]))

save = ui.SplitButton(                               # 左边执行，右边弹菜单
    col, "保存", icon="\uE74E",
    items=[
        {"label": "另存为…", "value": "save-as"},
        {"label": "导出为 PDF", "value": "export"},
        "-",
        {"label": "导出为图片", "value": "export-image"},
    ],
)
save.bind("click", lambda e: print("左半边：默认动作"))
save.on_menu(lambda e: print("右半边：", e.data["value"]))

ui.RepeatButton(col, "+", repeat_interval=80)        # 按住连续触发
```

`ToggleButton` 和 `Checkbox` 都是开/关，区别是前者当模式开关（工具栏加粗、视图切换），
后者当表单选项。`RepeatButton` 的 `click` 在按住期间会不断回传，回调里别做重活。

**窗口**

```python
win = ui.Window(
    title="工具",
    width=900, height=600,
    x=120, y=80,               # 位置（逻辑像素）；给了就不居中
    icon=r"C:\icons\app.ico",  # 标题栏和任务栏图标
    always_on_top=True,
    minimizable=True, maximizable=True,
)

win.always_on_top = False       # 运行中改也行，走 window 补丁
win.width = 1000
win.on_resize(lambda e: print(e.data["width"], e.data["height"]))
win.on_close(lambda e: print("要关了"))   # 关闭前触发，首版不能取消
```

**滚动**

整窗滚动默认开着：内容比窗口高时自动出滚动条，不用自己包一层。

```python
win = ui.Window(title="长表单")          # scroll=True 是默认值
col = ui.Column(win, padding=20, spacing=10)
for i in range(50):
    ui.Label(col, f"第 {i} 行")          # 排到窗口外面也能滚到
```

只想让某一块滚动，用 `Scroll`——它必须有一个确定高度：

```python
box = ui.Scroll(col, height=200, spacing=4)
for line in lines:
    ui.Label(box, line)
```

`Window(scroll=False)` 关掉整窗滚动（超出部分会被裁切）；运行中 `win.scroll = False` 也能切。

**可收缩侧栏**

`ListBox(auto_collapse=True)` 让列表自己当侧栏：鼠标移进去展开、点中某项后钉住、
点内容区才收起。宽度按 20 步指数缓出动画变化（`w += (end_w - w) / 3`，最后一步 snap）。

```python
win = ui.Window(title="工具", layout="grid", scroll=False,
                rows=["*"], columns=["auto", "*"])   # auto 列跟着侧栏宽度走

nav = ui.ListBox(win, [
    {"text": "概览", "glyph": "\uE80F"},
    {"text": "任务", "glyph": "\uE9D5"},
], width=200, auto_collapse=True, collapsed_width=56)
nav.grid(row=0, column=0)

body = ui.Scroll(win, padding=20)      # 右侧内容区，独立滚动
body.grid(row=0, column=1)
body.bind("pointerPressed", lambda e: nav.release())   # 点内容区收起
```

`layout="grid"` 是关键：窗口根节点直接就是 Grid，`*` 行列才拿得到真实剩余高度。
普通 `Grid(win)` 会被窗口的 StackPanel 包一层，高度变成无限，里面的东西撑不满也滚不动。

宽度必须显式给，且大于 `collapsed_width`——折叠只改宽度，不给宽度就没有「展开态」。
不想要悬停行为就 `hover_expand=False`，改用 `nav.toggle()` / `nav.set_open(True/False)`。

任意控件都能单独调这套动画：

```python
panel.animate_width(56)                       # 20 步缓出到 56
panel.animate_width(200, steps=12, interval=24)
```

想要自己的动画，任意控件都能调：

```python
panel.animate_width(56)        # 20 步缓出到 56
panel.animate_width(200, steps=12, interval=24)
```


**悬停提示与右键菜单**

```python
btn = ui.Button(col, "删除")
btn.set_tooltip("从列表里移除这一项")

btn.set_menu([
    {"label": "移到废纸篓", "glyph": "\uE74D", "value": "trash"},
    {"label": "彻底删除", "glyph": "\uE711", "value": "purge"},
    "-",                                              # 分隔线
    {"label": "撤销", "value": "undo", "enabled": False},
])
btn.on_menu(lambda e: print("点了", e.data["value"]))
```

两个属性对所有控件都可用，也可以直接 `ui.Label(col, "x", tooltip="提示")`。

**图片**

`src` 除本地路径和 URL 外，还接受 `data:image/png;base64,...`——
程序里算出来的图（截图、图表）可以直接贴上去，不必先写临时文件。

```python
ui.Image(col, src="data:image/png;base64," + b64, height=120)
img.on_error(lambda e: print("加载失败", e.data["error"]))
```


**单选框**

同 `group` 自动互斥，不需要手动取消其它项：

```python
ui.Radio(col, "选项甲", group="g1", checked=True)
ui.Radio(col, "选项乙", group="g1")
ui.Radio(col, "选项丙", group="g1")
```

**列表项图标**

`items` 既可以传字符串，也可以传带图标的对象（可混用）：

```python
ui.ListBox(col, [
    {"text": "文档", "glyph": "\uE8A5"},
    {"text": "图片", "glyph": "\uEB9F"},
    "没有图标的普通项",
])
```

`glyph` 是 Segoe Fluent Icons 的码点，WinUI 自带该字体，不需要额外资源文件。

**变量**

`StringVar` `IntVar` `DoubleVar` `BooleanVar`，双向绑定：

```python
name = ui.StringVar("world")
ui.Entry(win, textvariable=name)
name.trace(lambda v: print("变成", v))
```

**事件**

`command=` 无参，和 tkinter 一致；`bind()` 的回调收到 `Event`：

```python
entry.bind("change", lambda e: print(e.text))
entry.bind("submit", lambda e: print("回车了", e.text))
```

**弹窗**

```python
ui.dialog("控件事件", "Button 被点击了")
```

模态显示，会阻塞窗口交互，适合演示和调试。生产界面请用界面内的状态文字。

---

## 示例

| 文件 | 内容 |
|---|---|
| `examples/01_hello.py` | 最小示例：文字 + 按钮 + 回调改界面 |
| `examples/02_controls.py` | 控件总览：所有控件摆一屏，操作任一个都弹窗回报「哪个控件、什么事件、当前值」 |
| `examples/03_live.py` | 实时渲染：时钟/进度条/色块每 50ms 变一次，还能运行中动态插入控件 |
| `examples/04_window_toolkit.py` | 窗口图标/位置/置顶、按钮图标、data URI 图片、媒体控制、悬停提示、右键菜单 |
| `examples/05_scroll.py` | 整窗滚动 + 局部滚动区 |
| `examples/06_buttons.py` | ToggleButton / SplitButton / RepeatButton |
| `examples/07_sidebar.py` | Grid 布局 + 可折叠 ListBox 侧边栏（20 步宽度动画） |
| `examples/08_load_fail.py` | 加载 → 失败：加载圈 0.5 秒保底 + 失败错误界面 |

`02_controls.py` 是用来肉眼验收的——不用读日志就能确认每种控件和每种事件都通了。
`03_live.py` 用来确认「实时」这件事：全程没有手动刷新调用。

---

## 已知限制

- **`pack()` 只做语义子集**。WinUI 没有 tkinter 的几何管理器，`pack` 的 `side` 不参与布局计算，
  只把 `padx/pady` 翻译成 margin、`fill` 翻译成对齐。新代码建议直接用 `Column` / `Row` / `Grid`。
- **`place()` 不实现**。WinUI 没有绝对定位，会直接抛 `NotImplementedError`。
- **Windows 10 上部分视觉效果降级**：Mica 背景、控件圆角需要 Windows 11，
  宿主会按能力位自动回落（Acrylic 或纯色）。
- **单窗口**。一个宿主进程对应一个窗口，多窗口（`Toplevel`）尚未支持。
- **窗口没有尺寸上限**。`AppWindow` 不提供 `maxWidth` / `maxHeight`，
  只能靠 Win32 子类化拦 `WM_GETMINMAXINFO`，代价与收益不成比例。
- **关闭事件不可取消**。`closing` 只是通知，拦不住关窗；需要「确认退出」请在界面里做。
- **启动至少 0.5 秒**。加载圈有保底显示时间，否则在快机器上只闪一两帧，看起来像界面抖了一下。
- **回调在主线程执行**。回调里阻塞会卡住整个 UI，和 tkinter 行为一致。

---

## License

**GPL-3.0-only**（GNU General Public License v3.0）。完整条款见仓库根目录 [LICENSE](LICENSE)。

选择强著佐权（copyleft）而非 MIT：任何人都可以自由使用、修改和分发本项目，
但只要分发基于本项目的衍生作品（包括打包进商业软件），就必须在同样协议下公开
对应的完整源代码，且不得附加额外限制。这保留了原作者对其代码传播方式的控制权，
也避免代码被闭源产品直接拿去用。

> 如果你需要把本库链接进闭源程序，请单独联系作者获取商业授权。
