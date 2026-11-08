using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PyWinUI.Host.Interop;
using PyWinUI.Host.Protocol;
using Windows.Graphics;
using Windows.UI;

namespace PyWinUI.Host.Rendering;

/// <summary>
/// 表单渲染器：持有节点表，负责全量建树与增量补丁。
///
/// 节点表的键是协议里的稳定 id。所有 patch 操作都靠它定位控件，
/// 所以 id 的唯一性由 Python 侧保证，这里只做防御性校验。
/// </summary>
internal sealed class FormRenderer
{
    private readonly Window _window;
    private readonly PropertyApplier _applier;
    private readonly EventBridge _bridge;
    private readonly Action<string> _warn;
    private readonly Action<string, string, string?> _error;

    private readonly Dictionary<string, FrameworkElement> _nodes = new();
    private readonly Dictionary<string, NodeSpec> _specs = new();
    private readonly Dictionary<string, string> _parentOf = new();

    private string? _pendingTheme;

    /// <summary>当前根节点 id。窗口级事件按它识别「这条事件属于窗口」。</summary>
    private string? _rootId;

    public FormRenderer(
        Window window,
        Action<string, string, JsonObject?, bool> emit,
        Action<string> warn,
        Action<string, string, string?> error)
    {
        _window = window;
        _warn = warn;
        _error = error;
        _applier = new PropertyApplier(warn, emit);
        _bridge = new EventBridge(emit, warn);
    }

    public int NodeCount => _nodes.Count;

    public bool TryGetElement(string id, out FrameworkElement element) =>
        _nodes.TryGetValue(id, out element!);

    // ==================================================================
    // 全量渲染
    // ==================================================================

    /// <summary>
    /// 全量渲染。
    /// </summary>
    /// <returns>
    /// 成功返回 true。失败时会把加载圈换成错误提示并返回 false，
    /// 调用方据此决定要不要把窗口装饰（最大化/最小化）交还给用户。
    /// </returns>
    public bool Render(RenderMessage message)
    {
        HostLog.Write($"[render] start nodes={message.Nodes.Count} root={message.Root}");

        Reset();
        HostLog.Write("[render] reset ok");

        _pendingTheme = message.Window.Theme;
        _rootId = message.Root;

        // 注意：这里刻意先不碰窗口尺寸和内容。
        // 建树要几百毫秒，如果先把窗口放大，用户会看到「窗口变大但内容还是加载圈」。
        // 尺寸调整放到最后，和切换 Content 紧挨着，让 WinUI 在同一帧里一起处理。

        // 第一步：把所有节点创建出来。
        // 必须早于任何父子关系操作，因为 children 可以引用后面才定义的节点。
        foreach (var spec in message.Nodes)
        {
            if (string.IsNullOrEmpty(spec.Id))
            {
                _error("bad-protocol", "节点缺少 id", null);
                continue;
            }

            if (_nodes.ContainsKey(spec.Id))
            {
                _error("bad-protocol", $"节点 id 重复: {spec.Id}", spec.Id);
                continue;
            }

            _nodes[spec.Id] = NodeFactory.Create(spec.Type, spec.Id, spec.Props, _warn);
            _specs[spec.Id] = spec;
        }

        HostLog.Write($"[render] created {_nodes.Count} nodes");

        // 第二步：应用属性。
        // 放在组装之前，这样 Grid 的行列定义已经就绪，子元素加入时布局才正确。
        foreach (var spec in message.Nodes)
        {
            if (!_nodes.TryGetValue(spec.Id, out var element))
            {
                continue;
            }

            ApplyPropsSuppressed(spec.Id, element, spec.Props);
        }

        HostLog.Write("[render] props ok");

        // 第三步：组装父子关系。
        foreach (var spec in message.Nodes)
        {
            if (spec.Children is null || spec.Children.Count == 0)
            {
                continue;
            }

            if (!_nodes.TryGetValue(spec.Id, out var parentElement))
            {
                continue;
            }

            if (ChildHostOf(parentElement) is not { } panel)
            {
                _error("bad-protocol", $"节点 {spec.Id}（{spec.Type}）不能包含子节点", spec.Id);
                continue;
            }

            panel.Children.Clear();
            foreach (var childId in spec.Children)
            {
                if (!_nodes.TryGetValue(childId, out var childElement))
                {
                    _error("unknown-node", $"子节点 {childId} 不存在", spec.Id);
                    continue;
                }

                // 同一个控件不能被挂到两个父容器下（WinUI 会直接抛异常终止进程），
                // 这里提前拦下来，退化成一个可报告的协议错误
                if (_parentOf.TryGetValue(childId, out var existingParent))
                {
                    _error(
                        "bad-protocol",
                        $"子节点 {childId} 已挂在 {existingParent} 下，不能重复挂载",
                        childId);
                    continue;
                }

                if (childElement.Parent is not null)
                {
                    _error("bad-protocol", $"子节点 {childId} 已有父容器", childId);
                    continue;
                }

                panel.Children.Add(childElement);
                _parentOf[childId] = spec.Id;
            }
        }

        HostLog.Write("[render] tree ok");

        // 第四步：挂事件。放在最后，避免组装过程中的中间状态触发回调。
        foreach (var spec in message.Nodes)
        {
            if (!_nodes.TryGetValue(spec.Id, out var element))
            {
                continue;
            }

            if (spec.Events is { Count: > 0 })
            {
                BindEvents(spec.Id, element, spec.Events.Keys);
            }
        }

        HostLog.Write("[render] events ok");

        // 第五步：挂到窗口上
        if (!_nodes.TryGetValue(message.Root, out var root))
        {
            _error("unknown-node", $"根节点 {message.Root} 不存在", message.Root);
            ShowErrorView($"根节点 {message.Root} 不存在");
            return false;
        }

        if (_parentOf.ContainsKey(message.Root))
        {
            _error("bad-protocol", $"根节点 {message.Root} 不能有父节点", message.Root);
            ShowErrorView("根节点不能有父节点");
            return false;
        }

        // 最后一步：先调窗口尺寸，紧接着换内容。
        // 两步之间不放任何耗时操作，这样窗口放大和内容出现落在同一帧，
        // 不会出现「先变大再出现控件」的割裂感。
        ApplyWindowSpec(message.Window);

        // 整窗滚动默认开启。包在这一层是刻意的：只有窗口内容区的高度是有界的，
        // 放进 StackPanel 里的 ScrollViewer 会被测量成无限高，永远不滚。
        _window.Content = message.Window.Scroll == false ? root : WrapForScroll(root);
        HostLog.Write("[render] window sized + content swapped");

        ApplyTheme(_pendingTheme);
        HostLog.Write("[render] done");
        return true;
    }

    private void Reset()
    {
        _nodes.Clear();
        _specs.Clear();
        _parentOf.Clear();

        // 刻意不清空 Content：旧内容（加载圈，或上一版界面）留到新树建好再整体替换。
        // 提前清空的话，建树那几百毫秒里窗口会是一片空白，比割裂感更难受。
    }

    /// <summary>
    /// render 失败时把加载圈换成错误提示。
    ///
    /// 只处理「表单本身有问题」这类失败（根节点缺失、根节点有父节点）。
    /// 协议层的致命错误会直接关掉宿主，那时窗口已经不在了，轮不到这里。
    /// </summary>
    private void ShowErrorView(string detail)
    {
        try
        {
            var icon = new FontIcon
            {
                Glyph = "\uE783",
                FontSize = 44,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 196, 43, 28)),
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            var title = new TextBlock
            {
                Text = "加载失败",
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 12,
            };
            stack.Children.Add(icon);
            stack.Children.Add(title);

            if (!string.IsNullOrWhiteSpace(detail))
            {
                // 窗口此刻还是加载态那个小尺寸，所以细节文字要限宽换行
                stack.Children.Add(new TextBlock
                {
                    Text = detail,
                    FontSize = 12,
                    Opacity = 0.6,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 220,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
            }

            _window.Content = stack;

            // 标题也一起改，否则标题栏还挂着「正在加载」，和内容自相矛盾
            _window.Title = "加载失败";

            HostLog.Write("[render] error view shown: " + detail);
        }
        catch (Exception ex)
        {
            HostLog.Write("show error view failed: " + ex.Message);
        }
    }

    // ==================================================================
    // 增量补丁
    // ==================================================================

    public void Apply(PatchMessage message)
    {
        foreach (var op in message.Ops)
        {
            try
            {
                switch (op.Kind)
                {
                    case PatchOpKind.Set:
                        ApplySet(op);
                        break;
                    case PatchOpKind.Insert:
                        ApplyInsert(op);
                        break;
                    case PatchOpKind.Remove:
                        ApplyRemove(op);
                        break;
                    case PatchOpKind.Move:
                        ApplyMove(op);
                        break;
                    case PatchOpKind.Window:
                        ApplyWindowProps(op.Props);
                        break;
                }
            }
            catch (Exception ex)
            {
                _error("internal", $"patch 操作 {op.Kind} 失败: {ex.Message}", op.Id);
            }
        }
    }

    private void ApplySet(PatchOp op)
    {
        if (op.Id is null || !_nodes.TryGetValue(op.Id, out var element))
        {
            _error("unknown-node", $"set 的目标节点不存在: {op.Id}", op.Id);
            return;
        }

        ApplyPropsSuppressed(op.Id, element, op.Props);

        if (op.Events is not null && _specs.TryGetValue(op.Id, out var spec))
        {
            spec.Events = op.Events;
            BindEvents(op.Id, element, op.Events.Keys);
        }
    }

    private void ApplyInsert(PatchOp op)
    {
        if (op.Node is null || op.Parent is null)
        {
            _error("bad-op", "insert 缺少 node 或 parent", null);
            return;
        }

        var spec = op.Node;
        if (_nodes.ContainsKey(spec.Id))
        {
            _error("bad-protocol", $"insert 的节点 id 已存在: {spec.Id}", spec.Id);
            return;
        }

        if (!_nodes.TryGetValue(op.Parent, out var parentElement))
        {
            _error("unknown-node", $"insert 的父节点不存在: {op.Parent}", op.Parent);
            return;
        }

        if (ChildHostOf(parentElement) is not { } panel)
        {
            _error("bad-protocol", $"节点 {op.Parent} 不是容器，无法插入子节点", op.Parent);
            return;
        }

        var element = NodeFactory.Create(spec.Type, spec.Id, spec.Props, _warn);
        _nodes[spec.Id] = element;
        _specs[spec.Id] = spec;

        ApplyPropsSuppressed(spec.Id, element, spec.Props);

        var index = Math.Clamp(op.Index, 0, panel.Children.Count);
        panel.Children.Insert(index, element);
        _parentOf[spec.Id] = op.Parent;

        if (spec.Events is { Count: > 0 })
        {
            _bridge.Bind(element, spec.Id, spec.Events.Keys);
        }
    }

    private void ApplyRemove(PatchOp op)
    {
        if (op.Id is null || !_nodes.TryGetValue(op.Id, out var element))
        {
            _error("unknown-node", $"remove 的目标节点不存在: {op.Id}", op.Id);
            return;
        }

        var doomed = CollectSubtree(op.Id);

        if (element.Parent is Panel parent)
        {
            parent.Children.Remove(element);
        }
        else if (ReferenceEquals(_window.Content, element))
        {
            _window.Content = null;
        }

        foreach (var id in doomed)
        {
            _nodes.Remove(id);
            _specs.Remove(id);
            _parentOf.Remove(id);
        }
    }

    private void ApplyMove(PatchOp op)
    {
        if (op.Id is null || op.Parent is null)
        {
            _error("bad-op", "move 缺少 id 或 parent", null);
            return;
        }

        if (!_nodes.TryGetValue(op.Id, out var element))
        {
            _error("unknown-node", $"move 的目标节点不存在: {op.Id}", op.Id);
            return;
        }

        if (!_nodes.TryGetValue(op.Parent, out var newParentElement))
        {
            _error("unknown-node", $"move 的新父节点不存在: {op.Parent}", op.Parent);
            return;
        }

        if (ChildHostOf(newParentElement) is not { } newPanel)
        {
            _error("bad-protocol", $"节点 {op.Parent} 不是容器", op.Parent);
            return;
        }

        if (element.Parent is Panel oldPanel)
        {
            oldPanel.Children.Remove(element);
        }

        var index = Math.Clamp(op.Index, 0, newPanel.Children.Count);
        newPanel.Children.Insert(index, element);
        _parentOf[op.Id] = op.Parent;
    }

    /// <summary>
    /// 找到节点真正能挂子元素的地方。
    ///
    /// 普通容器本身就是 Panel；Scroll 节点是 ScrollViewer，它没有 Children，
    /// 真正装内容的是里面那层 StackPanel。把差异收在这里，
    /// 建树、插入、移动三条路径就不用各自判断一次。
    /// </summary>
    private static Panel? ChildHostOf(FrameworkElement element) => element switch
    {
        Panel panel => panel,
        ScrollViewer { Content: Panel inner } => inner,
        _ => null,
    };

    /// <summary>收集某个节点及其全部子孙的 id。删除时要一起清出节点表。</summary>
    private List<string> CollectSubtree(string rootId)
    {
        var result = new List<string> { rootId };
        var queue = new Queue<string>();
        queue.Enqueue(rootId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var pair in _parentOf)
            {
                if (pair.Value == current && !result.Contains(pair.Key))
                {
                    result.Add(pair.Key);
                    queue.Enqueue(pair.Key);
                }
            }
        }

        return result;
    }

    /// <summary>应用属性，期间把元素标记为「程序在改」，压住回声事件。</summary>
    private void ApplyPropsSuppressed(string nodeId, FrameworkElement element, JsonObject? props)
    {
        _bridge.BeginSuppress(element);
        try
        {
            _applier.Apply(nodeId, element, props);
        }
        finally
        {
            _bridge.EndSuppress(element);
        }
    }

    /// <summary>
    /// 分发事件名：根节点上的窗口级事件挂到 Window 上，其余挂到元素本身。
    /// Python 侧统一用 <c>win.bind("closing", ...)</c>，不需要知道宿主把处理器挂在哪。
    /// </summary>
    private void BindEvents(string nodeId, FrameworkElement element, IEnumerable<string> names)
    {
        List<string>? windowEvents = null;
        List<string>? elementEvents = null;

        foreach (var name in names)
        {
            if (nodeId == _rootId && name is "closing" or "sizeChanged")
            {
                (windowEvents ??= new List<string>()).Add(name);
            }
            else
            {
                (elementEvents ??= new List<string>()).Add(name);
            }
        }

        if (windowEvents is not null)
        {
            foreach (var name in windowEvents)
            {
                _bridge.BindWindow(_window, nodeId, name);
            }
        }

        if (elementEvents is not null)
        {
            _bridge.Bind(element, nodeId, elementEvents);
        }
    }

    // ==================================================================
    // 窗口
    // ==================================================================

    private void ApplyWindowProps(JsonObject? props)
    {
        if (props is null)
        {
            return;
        }

        var spec = new WindowSpec
        {
            Title = ValueParsers.AsString(props["title"]),
            Width = ValueParsers.AsDouble(props["width"]),
            Height = ValueParsers.AsDouble(props["height"]),
            MinWidth = ValueParsers.AsDouble(props["minWidth"]),
            MinHeight = ValueParsers.AsDouble(props["minHeight"]),
            Resizable = ValueParsers.AsBool(props["resizable"]),
            Center = ValueParsers.AsBool(props["center"]),
            X = ValueParsers.AsDouble(props["x"]),
            Y = ValueParsers.AsDouble(props["y"]),
            Icon = ValueParsers.AsString(props["icon"]),
            AlwaysOnTop = ValueParsers.AsBool(props["alwaysOnTop"]),
            Minimizable = ValueParsers.AsBool(props["minimizable"]),
            Maximizable = ValueParsers.AsBool(props["maximizable"]),
            Maximized = ValueParsers.AsBool(props["maximized"]),
            Theme = ValueParsers.AsString(props["theme"]),
            Backdrop = ValueParsers.AsString(props["backdrop"]),
            Scroll = ValueParsers.AsBool(props["scroll"]),
        };

        // 补丁路径下内容已经在窗口上，可以就地换包裹层
        ApplyWindowSpec(spec, contentReady: true);
    }

    /// <param name="contentReady">
    /// 为 true 表示窗口里已经有内容，可以就地调整「要不要滚动」。
    /// 首次 render 传 false——那时新树还没挂上去，包旧内容纯属白做。
    /// </param>
    private void ApplyWindowSpec(WindowSpec spec, bool contentReady = false)
    {
        try
        {
            if (!string.IsNullOrEmpty(spec.Title))
            {
                _window.Title = spec.Title;
            }

            HostLog.Write("[window] title ok");

            var appWindow = _window.AppWindow;
            var scale = GetDpiScale();
            HostLog.Write($"[window] appWindow ok scale={scale}");

            if (spec.Width is { } width || spec.Height is { } height)
            {
                var current = appWindow.Size;
                var pixelWidth = spec.Width is { } w
                    ? (int)Math.Round(w * scale)
                    : current.Width;
                var pixelHeight = spec.Height is { } h
                    ? (int)Math.Round(h * scale)
                    : current.Height;

                appWindow.Resize(new SizeInt32(pixelWidth, pixelHeight));
                HostLog.Write($"[window] resized to {pixelWidth}x{pixelHeight}");
            }

            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                // 走到这里说明内容已经渲染成功，先把加载态收走的窗口能力还给用户，
                // 再用协议里的显式值覆盖。顺序反了的话上一步会把这里的设置抹掉。
                presenter.IsMinimizable = true;
                presenter.IsMaximizable = true;
                presenter.IsResizable = true;

                if (spec.Resizable is { } resizable)
                {
                    presenter.IsResizable = resizable;
                }

                if (spec.Minimizable is { } minimizable)
                {
                    presenter.IsMinimizable = minimizable;
                }

                if (spec.Maximizable is { } maximizable)
                {
                    presenter.IsMaximizable = maximizable;
                }

                if (spec.AlwaysOnTop is { } alwaysOnTop)
                {
                    presenter.IsAlwaysOnTop = alwaysOnTop;
                }

                if (spec.MinWidth is { } minWidth)
                {
                    presenter.PreferredMinimumWidth = (int)Math.Round(minWidth * scale);
                }

                if (spec.MinHeight is { } minHeight)
                {
                    presenter.PreferredMinimumHeight = (int)Math.Round(minHeight * scale);
                }

                HostLog.Write("[window] presenter ok");
            }

            if (spec.Center == true)
            {
                CenterOnScreen(appWindow);
                HostLog.Write("[window] centered");
            }

            // 显式坐标优先于居中：两者同时给时按坐标落地
            if (spec.X is { } posX || spec.Y is { } posY)
            {
                var current = appWindow.Position;
                appWindow.Move(new PointInt32(
                    spec.X is { } x ? (int)Math.Round(x * scale) : current.X,
                    spec.Y is { } y ? (int)Math.Round(y * scale) : current.Y));
                HostLog.Write("[window] moved");
            }

            ApplyWindowIcon(appWindow, spec.Icon);

            if (spec.Maximized is { } maximized
                && appWindow.Presenter is OverlappedPresenter windowPresenter)
            {
                if (maximized)
                {
                    windowPresenter.Maximize();
                }
                else
                {
                    windowPresenter.Restore();
                }

                HostLog.Write($"[window] maximized={maximized}");
            }

            if (spec.Theme is { Length: > 0 } theme)
            {
                _pendingTheme = theme;
                ApplyTheme(theme);
                HostLog.Write("[window] theme ok");
            }

            if (spec.Backdrop is { Length: > 0 } backdrop)
            {
                HostLog.Write($"[window] applying backdrop \"{backdrop}\"");
                ApplyBackdrop(backdrop);
                HostLog.Write("[window] backdrop ok");
            }

            if (contentReady && spec.Scroll is { } scroll)
            {
                ApplyScrollMode(scroll);
            }
        }
        catch (Exception ex)
        {
            _warn($"窗口属性应用失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 把根节点包进 ScrollViewer。
    ///
    /// 几个参数刻意不取默认值：
    /// - 关掉横向滚动：界面是纵向流式布局，横向滚动只会带来误触
    /// - HorizontalContentAlignment 改成 Stretch：ScrollViewer 默认是 Left，
    ///   会让原本撑满宽度的子元素突然缩到内容宽度，看起来像布局坏了
    /// </summary>
    private static ScrollViewer WrapForScroll(FrameworkElement root) => new()
    {
        Content = root,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalScrollMode = ScrollMode.Disabled,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Top,
    };

    /// <summary>运行中切换整窗滚动。只在窗口补丁路径调用。</summary>
    private void ApplyScrollMode(bool enabled)
    {
        if (_rootId is not { } id || !_nodes.TryGetValue(id, out var root))
        {
            return;
        }

        var current = _window.Content as FrameworkElement;

        if (enabled && current is not ScrollViewer)
        {
            // 必须先摘下来：同一个控件挂在两个父容器上，WinUI 会直接杀进程
            _window.Content = null;
            _window.Content = WrapForScroll(root);
            HostLog.Write("[window] scroll on");
        }
        else if (!enabled && current is ScrollViewer scroller)
        {
            _window.Content = null;
            scroller.Content = null;
            _window.Content = root;
            HostLog.Write("[window] scroll off");
        }
    }

    /// <summary>
    /// 设置窗口图标。只接受磁盘上的绝对/相对路径，且文件必须存在——
    /// AppWindow.SetIcon 对不存在的路径是静默忽略，不返回错误，很难排查。
    /// </summary>
    private void ApplyWindowIcon(AppWindow appWindow, string? iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath))
        {
            return;
        }

        try
        {
            var full = Path.IsPathRooted(iconPath) ? iconPath : Path.GetFullPath(iconPath);
            if (!File.Exists(full))
            {
                _warn($"窗口图标不存在: {full}");
                return;
            }

            appWindow.SetIcon(full);
            HostLog.Write($"[window] icon \"{full}\"");
        }
        catch (Exception ex)
        {
            _warn($"窗口图标设置失败: {ex.Message}");
        }
    }

    private void ApplyTheme(string? theme)
    {
        if (string.IsNullOrEmpty(theme) || _window.Content is not FrameworkElement root)
        {
            return;
        }

        root.RequestedTheme = theme.ToLowerInvariant() switch
        {
            "dark" => ElementTheme.Dark,
            "light" => ElementTheme.Light,
            _ => ElementTheme.Default,
        };
    }

    private void ApplyBackdrop(string requested)
    {
        // 先把请求解析成这台机器真能做的效果，Win10 上 Mica 会被降级成 Acrylic
        var kind = Capabilities.ResolveBackdrop(requested);

        try
        {
            _window.SystemBackdrop = kind switch
            {
                "mica" => new MicaBackdrop(),
                "acrylic" => new DesktopAcrylicBackdrop(),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            _warn($"backdrop \"{kind}\" 应用失败: {ex.Message}");
        }
    }

    private void CenterOnScreen(AppWindow appWindow)
    {
        var display = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        var size = appWindow.Size;

        appWindow.Move(new PointInt32(
            work.X + (work.Width - size.Width) / 2,
            work.Y + (work.Height - size.Height) / 2));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// 协议里的尺寸是逻辑像素（DIP），而 AppWindow 用的是物理像素，
    /// 中间差一个显示器缩放系数。125% 缩放下不换算窗口会小一圈。
    /// </summary>
    private double GetDpiScale()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            var dpi = GetDpiForWindow(hwnd);
            return dpi == 0 ? 1.0 : dpi / 96.0;
        }
        catch
        {
            return 1.0;
        }
    }
}
