# PyWinUI 协议定义

版本：`1`

本文件是 Python 侧与 C# 宿主之间的**唯一契约**。两端实现都必须以本文为准，  
任何字段增删都要先改这里、再改两端。

---

## 1. 传输层

### 1.1 通道

宿主进程由 Python 用 `subprocess` 启动，通信走**标准输入 / 标准输出**：

```
Python ──stdin──▶ 宿主 ──▶ WinUI 3
Python ◀─stdout── 宿主
Python ◀─stderr── 宿主的日志与诊断信息
```

选择 stdin/stdout 而非命名管道或 TCP 的理由：

- Python 标准库原生支持 `subprocess.PIPE`，不需要 `pywin32` 或任何第三方包
- Windows 命名管道在 Python 标准库里没有对应实现，用 `ctypes` 自己调 Win32 API 太重
- TCP 会占端口，且可能被防火墙拦截
- 管道句柄的生命周期天然绑定进程，对端一死立刻读到 EOF，不需要额外心跳
- 这也是 LSP 采用的方式，久经验证

### 1.2 分帧

JSON Lines：**一行一条消息**，行尾 `\n`。

- 编码一律 UTF-8，**不带 BOM**
- 消息体内不得出现裸换行（由 JSON 序列化器保证）
- 单行长度上限 8 MiB，超限视为协议错误
- 读到 EOF 即视为对端退出

### 1.3 时序

```
Python                          Host
  │                              │
  ├── spawn(exe --stdio) ───────▶│
  │                              │  初始化 WinUI 3
  ├── hello ────────────────────▶│
  │                              │  校验版本
  │◀──────────────────── ready ──┤
  ├── render ───────────────────▶│  建树、显示窗口
  │◀───────────────────── ack ───┤
  │                              │
  │◀─────────────────── event ───┤  用户点击
  ├── patch ────────────────────▶│  回调里改界面
  │◀───────────────────── ack ───┤
  │                              │
  ├── close ────────────────────▶│
  │◀────────────────────── bye ───┤  进程退出
```

握手必须在 **10 秒**内完成，否则 Python 侧杀掉宿主进程并抛 `HostStartError`。

---

## 2. 消息类型

所有消息都是单行 JSON 对象。方向用 `op`（Python → Host）和 `t`（Host → Python）区分，  
两个字段名不同是刻意的：抓日志时一眼就能看出这行是请求还是响应。

### 2.1 Python → Host

#### `hello` — 握手

```json
{ "op": "hello", "protocol": 1, "client": "pywinui/0.1.0", "pid": 12345 }
```

| 字段         | 类型     | 必填 | 说明                     |
| ---------- | ------ | -- | ---------------------- |
| `protocol` | int    | 是  | 客户端协议版本                |
| `client`   | string | 是  | 客户端标识，`名称/版本`          |
| `pid`      | int    | 否  | Python 进程 pid，便于排查孤儿进程 |

宿主回 `ready`；版本不兼容则回 `error` 后退出。

#### `render` — 全量渲染

```json
{
  "op": "render",
  "rev": 1,
  "window": { "title": "Demo", "width": 900, "height": 600 },
  "root": "n0",
  "nodes": [
    { "id": "n0", "type": "Column", "props": { "padding": 24, "spacing": 12 },
      "children": ["n1", "n2"] },
    { "id": "n1", "type": "Label", "props": { "text": "Hello" } },
    { "id": "n2", "type": "Button", "props": { "text": "点击" }, "events": { "click": "h1" } }
  ]
}
```

- `nodes` 是**扁平列表**，父子关系由 `children` 表达，不是嵌套结构  
  （扁平结构让增量更新不必重新序列化整棵子树）
- `root` 指向根节点 id
- `nodes` 里所有 id 必须唯一
- 首次 `render` 创建窗口；后续 `render` 视为整棵树重建（用于热重载）

#### `patch` — 增量更新

```json
{
  "op": "patch",
  "rev": 2,
  "ops": [
    { "t": "set",    "id": "n1", "props": { "text": "Changed" } },
    { "t": "insert", "parent": "n0", "index": 1,
      "node": { "id": "n3", "type": "Label", "props": { "text": "新行" } } },
    { "t": "remove", "id": "n4" },
    { "t": "move",   "id": "n2", "parent": "n0", "index": 0 },
    { "t": "window", "props": { "title": "新标题" } }
  ]
}
```

| `t`      | 语义               | 必需字段                      | 可选字段      |
| -------- | ---------------- | ------------------------- | --------- |
| `set`    | 设置已有节点的属性        | `id`, `props`             | `events`  |
| `insert` | 插入新节点到指定父节点的指定位置 | `parent`, `index`, `node` | —         |
| `remove` | 移除节点及其子树         | `id`                      | —         |
| `move`   | 把已有节点移到新父节点的新位置  | `id`, `parent`, `index`   | —         |
| `window` | 更新窗口属性           | `props`                   | —         |

`set` 带上 `events` 时，该节点的事件绑定被**整体替换**为给定映射
（传空对象即解绑全部）。之所以让事件走 `set` 而不是单独的 `bind` 操作：
事件映射本身就是节点声明的一部分，用一个操作维护它比维护两套路径更不容易出错。

```json
{ "t": "set", "id": "n2", "props": {}, "events": { "click": "h7" } }
```

`ops` **按数组顺序依次应用**。同一条 `patch` 里的操作可以前后依赖  
（例如先 `insert` 再对插入的节点 `set`）。

不用 RFC 6902 JSON Patch 的原因：UI 树的增删必须带父节点与兄弟序号，  
语义化的操作能一对一映射到 `Panel.Children.Insert/RemoveAt`，  
比通用 JSON 路径补丁更好实现、更好调试。

#### `invoke` — 调用控件方法

```json
{ "op": "invoke", "id": "n2", "method": "focus", "args": {} }
```

支持：`focus`、`select_all`、`scroll_to`（`args: {"index": 0}`）、
`play` / `pause` / `stop` / `seek`（仅 `Media`，`seek` 用 `args: {"seconds": 12.5}`）。

#### `dialog` — 弹出模态对话框

```json
{ "op": "dialog", "title": "控件事件", "body": "Button · click", "kind": "info" }
```

| 字段 | 必填 | 说明 |
|---|---|---|
| `title` | 否 | 标题，缺省 `"提示"` |
| `body` | 否 | 正文，纯文本 |
| `kind` | 否 | `info` / `warn` / `error`，默认 `info` |

宿主用 `ContentDialog` 模态显示，用户点「知道了」后关闭，宿主回 `ack`。

同一时刻只能有一个对话框：若上一个还没关，新请求被丢弃并回
`{"t":"error","code":"busy"}`。这是 WinUI 的硬性限制，不是设计选择。

对话框会阻塞窗口交互，所以它适合演示和调试，不适合当通知用——
生产场景请用界面内的状态文字。

#### `close` — 关闭

```json
{ "op": "close", "code": 0 }
```

宿主关闭窗口、回 `bye`、退出进程。

### 2.2 Host → Python

#### `ready` — 就绪

```json
{
  "t": "ready",
  "protocol": 1,
  "host": "PyWinUI.Host/0.1.0",
  "os": { "version": "10.0.19044", "build": 19044 },
  "capabilities": {
    "mica": false,
    "acrylic": true,
    "roundedCorners": false,
    "backdrop": "acrylic"
  }
}
```

`capabilities` 让 Python 侧知道哪些视觉效果可用。Windows 10 上没有 Mica，  
宿主会据此把 `backdrop: "auto"` 降级成 `acrylic`。

#### `ack` — 已应用

```json
{ "t": "ack", "rev": 2 }
```

`rev` 对应请求里的 `rev`。Python 侧用它做流控和错误归因。

#### `event` — 控件事件

```json
{ "t": "event", "id": "n2", "event": "click", "rev": 2, "data": { "x": 120, "y": 40 } }
```

`echo: true` 表示这次变化是程序自己 `set` 引起的、不是用户输入：

```json
{ "t": "event", "id": "n5", "event": "change", "rev": 3,
  "data": { "text": "hi" }, "echo": true }
```

Python 侧据此抑制双向绑定回环。

#### `error` — 渲染或解析错误

```json
{ "t": "error", "code": "unknown-type", "message": "未知控件类型 Foo", "id": "n7" }
```

错误**不终止会话**（协议级错误除外），Python 侧把 `id` 对应的控件操作抛成 `UiError`。

#### `bye` — 退出

```json
{ "t": "bye", "code": 0 }
```

窗口被用户点关闭、或收到 `close` 之后发出，随后进程退出。

---

## 3. 表单结构

### 3.1 Window

| 字段          | 类型     | 默认          | 说明                                   |
| ----------- | ------ | ----------- | ------------------------------------ |
| `title`     | string | `"PyWinUI"` | 窗口标题                                 |
| `width`     | number | 800         | 逻辑像素                                 |
| `height`    | number | 600         | 逻辑像素                                 |
| `minWidth`  | number | 320         |                                      |
| `minHeight` | number | 240         |                                      |
| `resizable` | bool   | `true`      |                                      |
| `center`    | bool   | `true`      | 启动时居中；与 `x`/`y` 同时给时坐标优先             |
| `x` / `y`   | number | —           | 窗口左上角（逻辑像素）                          |
| `icon`      | string | —           | 图标路径（`.ico` / `.png`），相对路径按工作目录解析    |
| `alwaysOnTop` | bool | `false`     | 置顶                                   |
| `minimizable` | bool | `true`      | 是否显示最小化按钮                            |
| `maximizable` | bool | `true`      | 是否显示最大化按钮                            |
| `maximized`   | bool | `false`     | 启动即最大化                               |
| `scroll`      | bool | `true`      | 窗口内容可滚动；关掉后超出部分被裁切                    |
| `theme`     | string | `"system"`  | `system` / `light` / `dark`          |
| `backdrop`  | string | `"auto"`    | `auto` / `mica` / `acrylic` / `none` |

窗口尺寸上限（`maxWidth` / `maxHeight`）没有对应字段：`AppWindow` 不提供这个能力，
只能靠 Win32 子类化拦 `WM_GETMINMAXINFO`，代价与收益不成比例。

`backdrop: "auto"` 由宿主按能力挑：有 Mica 用 Mica，否则 Acrylic，再否则纯色。

### 3.2 Node

```json
{
  "id": "n1",
  "type": "Label",
  "props": { "text": "Hello", "fontSize": 20 },
  "children": ["n2"],
  "events": { "click": "h1" }
}
```

| 字段         | 必填 | 说明                                     |
| ---------- | -- | -------------------------------------- |
| `id`       | 是  | 稳定标识，一次会话内唯一，由 Python 侧生成（`n1`, `n2`…） |
| `type`     | 是  | 控件类型，见下表                               |
| `props`    | 否  | 属性字典，缺省为空对象                            |
| `children` | 否  | 子节点 id 列表，顺序即渲染顺序                      |
| `events`   | 否  | 事件名 → handler id                       |

### 3.3 控件类型

| `type`            | 映射到                          | 可含子节点  |
| ----------------- | ---------------------------- | ------ |
| `Window`          | 窗口内容根（通常由 `root` 承担，一般不显式出现） | 是（1 个） |
| `Column`          | `StackPanel`（垂直）             | 是      |
| `Row`             | `StackPanel`（水平）             | 是      |
| `Grid`            | `Grid`                       | 是      |
| `Scroll`          | `ScrollViewer`（内含 `StackPanel`） | 是      |
| `Label`           | `TextBlock`                  | 否      |
| `Button`          | `Button`                     | 否      |
| `HyperlinkButton` | `HyperlinkButton`            | 否      |
| `ToggleButton`    | `ToggleButton`               | 否      |
| `SplitButton`     | `SplitButton`                | 否      |
| `RepeatButton`    | `RepeatButton`               | 否      |
| `Entry`           | `TextBox`                    | 否      |
| `Checkbox`        | `CheckBox`                   | 否      |
| `Radio`           | `RadioButton`                | 否      |
| `Switch`          | `ToggleSwitch`               | 否      |
| `Slider`          | `Slider`                     | 否      |
| `ProgressBar`     | `ProgressBar`                | 否      |
| `ProgressRing`    | `ProgressRing`               | 否      |
| `ComboBox`        | `ComboBox`                   | 否      |
| `ListBox`         | `ListView`                   | 否      |
| `Image`           | `Image`                      | 否      |
| `Media`           | `MediaPlayerElement`         | 否      |
| `Separator`       | `Border`（1px 线）              | 否      |

未知 `type` 不崩溃：回一条 `error`，该节点渲染成占位 `TextBlock` 显示 `[未知控件: Foo]`。

`Scroll` 在宿主侧是 `ScrollViewer` 套一层 `StackPanel`：节点模型允许一个节点有多个子节点，
而 `ScrollViewer.Content` 只收一个元素，里层那层 StackPanel 就是子节点的宿主，
`spacing` 也作用在它身上。注意 `ScrollViewer` 只有在**父容器给出有界高度**时才会滚动——
放在 `Column` / `Row` 里必须显式给 `height`，否则会被测量成无限高。
整窗滚动不需要它，`window.scroll` 默认就是开的。

### 3.4 通用属性

所有控件都接受：

| 属性                                                  | 类型                                  | 说明                |
| --------------------------------------------------- | ----------------------------------- | ----------------- |
| `width` / `height`                                  | number                              | 固定尺寸，缺省为自动        |
| `minWidth` / `minHeight` / `maxWidth` / `maxHeight` | number                              |                   |
| `margin`                                            | number | [l,t,r,b] | {l,t,r,b}      | 外边距               |
| `padding`                                           | 同上                                  | 内边距（容器类）          |
| `hAlign`                                            | `left`/`center`/`right`/`stretch`   | 水平对齐              |
| `vAlign`                                            | `top`/`center`/`bottom`/`stretch`   | 垂直对齐              |
| `visible`                                           | bool                                |                   |
| `enabled`                                           | bool                                |                   |
| `opacity`                                           | number 0–1                          |                   |
| `background`                                        | color                               | 见 3.6             |
| `foreground`                                        | color                               |                   |
| `borderColor` / `borderWidth`                       | color / number                      |                   |
| `cornerRadius`                                      | number | [tl,tr,br,bl]              | Win10 上不生效，宿主静默忽略 |
| `fontSize`                                          | number                              |                   |
| `fontFamily`                                        | string                              |                   |
| `weight`                                            | `normal`/`medium`/`semibold`/`bold` |                   |
| `italic`                                            | bool                                |                   |
| `tooltip`                                           | string                              | 鼠标悬停提示，空串清除      |
| `contextMenu`                                       | array                               | 右键菜单，见 3.5.2       |

### 3.5 各控件专有属性

| 控件               | 属性                                                                                                |
| ---------------- | ------------------------------------------------------------------------------------------------- |
| `Column` / `Row` | `spacing`(number), `padding`                                                                      |
| `Grid`           | `rows`([`auto`/`*`/数字]), `columns` 同上, `rowGap`, `columnGap`, `padding`                           |
| `Scroll`         | `spacing`, `padding`, `height`（**必须给一个有界高度，否则不会滚**）                                            |
| `Grid` 子节点       | `row`, `column`, `rowSpan`, `columnSpan`                                                          |
| `Label`          | `text`, `wrap`(bool), `maxLines`(number), `textAlign`                                             |
| `Button`         | `text`, `icon`(glyph), `variant`(`normal`/`accent`)                                               |
| `HyperlinkButton` | `text`, `icon`(glyph), `navigateUri`(string)                                                     |
| `ToggleButton`   | `text`, `icon`(glyph), `checked`(bool)                                                            |
| `SplitButton`    | `text`, `icon`(glyph), `menu`（下拉菜单，格式见 3.5.2）                                                  |
| `RepeatButton`   | `text`, `icon`(glyph), `repeatDelay`(ms), `repeatInterval`(ms)                                    |
| `Entry`          | `text`, `placeholder`, `readOnly`(bool), `multiline`(bool), `password`(bool), `maxLength`(number) |
| `Checkbox`       | `text`, `checked`(bool)                                                                           |
| `Radio`          | `text`, `checked`(bool), `groupName`                                                              |
| `Switch`         | `header`, `on`(bool)                                                                              |
| `Slider`         | `min`, `max`, `value`, `stepFrequency`, `orientation`                                             |
| `ProgressBar`    | `value`, `min`, `max`, `indeterminate`(bool)                                                      |
| `ProgressRing`   | `active`(bool), `size`(number), `foreground`                                                      |
| `Media`          | `src`, `autoPlay`(bool), `showControls`(bool), `volume`(0–1), `position`(秒), `loop`(bool), `muted`(bool), `playbackRate`(0.25–4), `stretch` |
| `ComboBox`       | `items`(见 3.5.1), `selectedIndex`(number), `placeholder`                                          |
| `ListBox`        | `items`(见 3.5.1), `selectedIndex`(number)                                                         |
| `Image`          | `src`(路径 / URL / data URI), `stretch`(`fill`/`uniform`/`uniformToFill`/`none`)                      |
| `Separator`      | `color`, `thickness`                                                                              |

#### 3.5.1 `items` 的两种形态

纯文本：

```json
"items": ["选项 A", "选项 B"]
```

带图标（数组元素换成对象）：

```json
"items": [
  { "text": "文档", "glyph": "\uE8A5" },
  { "text": "图片", "glyph": "\uEB9F" }
]
```

| 字段 | 必填 | 说明 |
|---|---|---|
| `text` | 是 | 显示文字，同时作为事件的 `value` 回传 |
| `glyph` | 否 | Segoe Fluent Icons 的码点，如 `"\uE8A5"` |

两种形态可以混用——数组里的裸字符串按纯文本项处理。

图标用的是 WinUI 自带的 Segoe Fluent Icons 字体，不需要额外资源文件。
常用码点：

| 码点 | 图标 | 码点 | 图标 |
|---|---|---|---|
| `\uE8A5` | 文档 | `\uE8B7` | 文件夹 |
| `\uEB9F` | 图片 | `\uE713` | 设置 |
| `\uE77B` | 联系人 | `\uE8F1` | 库 |
| `\uE896` | 下载 | `\uE72D` | 分享 |

完整列表见微软文档 "Segoe Fluent Icons font"。

#### 3.5.2 `contextMenu` 与 `menu` 的菜单项

右键菜单（`contextMenu`）和 `SplitButton` 的下拉菜单（`menu`）用同一套格式，任何控件都能挂右键菜单：

```json
"contextMenu": [
  { "label": "复制", "glyph": "\uE8C8", "value": "copy" },
  "-",
  { "label": "删除", "value": "delete", "enabled": false }
]
```

| 字段        | 必填 | 说明                    |
| --------- | -- | --------------------- |
| `label`   | 是  | 菜单文字                  |
| `glyph`   | 否  | Segoe Fluent Icons 码点 |
| `value`   | 否  | 点击时回传的值，缺省用 `label`    |
| `enabled` | 否  | 缺省 `true`             |

分隔线写成字符串 `"-"` 或 `{"separator": true}`。

菜单项点击后统一回传 `menu` 事件，`data` 为 `{ "value": "...", "label": "..." }`。
之所以用「一个事件 + value」而不是每条菜单一个事件名：菜单项属于数据，
让 Python 侧按 `value` 分派，比动态注册一堆事件好维护。

`Image` 的 `src` 除路径与 URL 外还支持 `data:image/png;base64,...`；
真正开始解码前不会阻塞 UI，解码失败回 `imageFailed`。

### 3.6 值类型约定

**颜色**接受三种写法：

```
"#FF5500"          十六进制，支持 #RGB / #RRGGBB / #AARRGGBB
"red"              140 个 CSS 具名颜色
{ "a": 255, "r": 255, "g": 85, "b": 0 }   显式通道
"accent"           系统强调色
```

**尺寸**一律是逻辑像素（DIP），由 WinUI 的 DPI 缩放自动处理，Python 侧不需要关心显示器 DPI。

**枚举值**全部小写 camelCase，例如 `"semibold"`、`"uniformToFill"`、`"stretch"`。  
收到无法识别的枚举值时用默认值并记一条 `error`，不崩溃。

---

## 4. 事件

### 4.1 事件名

| 事件                 | 触发控件                                               | `data`                           |
| ------------------ | -------------------------------------------------- | -------------------------------- |
| `click`            | Button, HyperlinkButton, ToggleButton, RepeatButton, SplitButton（左半边）, Checkbox | `{}`                    |
| `change`           | Entry, Checkbox, Switch, ToggleButton, Slider, ComboBox, ListBox | 见下                               |
| `submit`           | Entry（回车）                                          | `{ "text": "..." }`              |
| `focus` / `blur`   | 所有交互控件                                             | `{}`                             |
| `selectionChanged` | ComboBox, ListBox                                  | `{ "index": 0, "value": "..." }` |
| `menu`             | 挂了 `contextMenu` 的任意控件                            | `{ "value": "...", "label": "..." }` |
| `pointerEntered` / `pointerExited` | 任意节点                              | `{}`，鼠标进出元素边界          |
| `pointerPressed`   | 任意节点                                               | `{}`，按下（含子元素冒泡）              |
| `imageFailed`      | Image                                              | `{ "error": "...", "src": "..." }` |
| `closing`          | Window                                             | `{}`，关闭前触发，首版不支持取消              |
| `sizeChanged`      | Window                                             | `{ "width": 640.0, "height": 480.0 }` |

窗口事件挂在根节点上：Python 侧照常 `win.bind("closing", fn)`，
宿主会自动把处理器挂到 `Window` 而不是根元素。

`change` 的 `data` 按控件类型：

- Entry → `{ "text": "..." }`
- Checkbox / Switch / ToggleButton → `{ "checked": true }`
- Slider / ProgressBar → `{ "value": 42.0 }`
- ComboBox / ListBox → `{ "index": 1, "value": "..." }`

### 4.2 回声抑制

Python 侧 `entry.configure(text="x")` 会触发宿主的 TextChanged，  
宿主无法区分「用户输入」和「程序赋值」。

解决方式：宿主在**应用 `set` 操作期间**给该控件打标记，  
期间产生的事件带上 `echo: true`。Python 侧收到 `echo` 事件时更新内部变量值，  
但**不触发**用户回调。

时序：

```
Python: entry.set("x")
   → patch {set: {text: "x"}}
Host:   打 echo 标记 → 赋值 → TextChanged 触发 → 事件带 echo:true → 清标记
Python: 收到 echo 事件 → 同步变量 → 不调用 on_change
```

---

## 5. 错误

| `code`             | 含义            | 是否终止 |
| ------------------ | ------------- | ---- |
| `bad-json`         | 行不是合法 JSON    | 是    |
| `bad-protocol`     | 缺少必需字段 / 类型不对 | 是    |
| `version-mismatch` | 协议版本不兼容       | 是    |
| `unknown-type`     | 未知控件类型        | 否    |
| `unknown-node`     | 引用了不存在的节点 id  | 否    |
| `bad-property`     | 属性值无法解析       | 否    |
| `bad-op`           | 未知的 patch 操作  | 否    |
| `internal`         | 宿主内部异常        | 视情况  |

非终止错误会在 Python 侧以 `log` 回调或 `UiError` 形式暴露。

---

## 6. 完整示例

Python 侧：

```python
win = Window(title="Demo", width=640, height=480)
col = Column(win, padding=24, spacing=12)
Label(col, "你好", font_size=28)
Button(col, "点我", command=lambda: print("clicked"))
win.mainloop()
```

对应线上流量：

```
→ {"op":"hello","protocol":1,"client":"pywinui/0.1.0","pid":8123}
← {"t":"ready","protocol":1,"host":"PyWinUI.Host/0.1.0","os":{"version":"10.0.19044","build":19044},"capabilities":{"mica":false,"acrylic":true,"roundedCorners":false,"backdrop":"acrylic"}}
→ {"op":"render","rev":1,"window":{"title":"Demo","width":640,"height":480,"backdrop":"auto"},"root":"n0","nodes":[{"id":"n0","type":"Column","props":{"padding":24,"spacing":12},"children":["n1","n2"]},{"id":"n1","type":"Label","props":{"text":"你好","fontSize":28}},{"id":"n2","type":"Button","props":{"text":"点我"},"events":{"click":"h1"}}]}
← {"t":"ack","rev":1}
← {"t":"event","id":"n2","event":"click","rev":1,"data":{}}
```

---

## 7. 版本演进规则

- 协议版本是单调整数，**不兼容变更**才 +1
- 新增可选字段**不算**不兼容变更，旧端忽略即可
- 新增控件类型、新增事件名**不算**不兼容变更
- 重命名字段、改变字段语义、删除字段**算**不兼容变更
