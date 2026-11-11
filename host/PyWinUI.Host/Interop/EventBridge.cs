using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using PyWinUI.Host.Protocol;
using Windows.Media.Playback;
using Windows.System;

namespace PyWinUI.Host.Interop;

/// <summary>
/// 把 WinUI 控件事件翻译成协议事件回传给 Python。
///
/// 两个设计要点：
///
/// 1. **事件不带 handler id**。协议里 <c>event</c> 消息只有 nodeId 和事件名，
///    handler 的映射由 Python 侧维护。这样 Python 改绑回调不需要通知宿主，
///    少一次往返，也避免了「宿主里存着一份可能过期的映射」这种状态同步问题。
///
/// 2. **回声抑制**。程序设置 <c>entry.text = "x"</c> 时，宿主给 TextBox 赋值
///    同样会触发 TextChanged，Python 侧无法区分「用户敲的」和「自己设的」。
///    做法是赋值期间给元素打标记，期间产生的事件带 <c>echo: true</c>。
/// </summary>
internal sealed class EventBridge
{
    private readonly Action<string, string, JsonObject?, bool> _emit;
    private readonly Action<string> _warn;
    private readonly HashSet<FrameworkElement> _suppressed = new();

    /// <summary>已经挂过处理器的 (节点, 事件) 组合，避免重复订阅。</summary>
    private readonly HashSet<(string NodeId, string EventName)> _bound = new();

    /// <summary>窗口级事件单独记一份：它们挂在 Window 上，不挂在根元素上。</summary>
    private readonly HashSet<(string NodeId, string EventName)> _boundWindow = new();

    /// <param name="emit">参数依次为 nodeId、事件名、数据、是否回声。</param>
    public EventBridge(Action<string, string, JsonObject?, bool> emit, Action<string> warn)
    {
        _emit = emit;
        _warn = warn;
    }

    // ------------------------------------------------------------------
    // 回声抑制
    // ------------------------------------------------------------------

    public void BeginSuppress(FrameworkElement element) => _suppressed.Add(element);

    public void EndSuppress(FrameworkElement element) => _suppressed.Remove(element);

    // ------------------------------------------------------------------
    // 绑定
    // ------------------------------------------------------------------

    public void Bind(FrameworkElement element, string nodeId, IEnumerable<string> eventNames)
    {
        foreach (var eventName in eventNames)
        {
            if (!_bound.Add((nodeId, eventName)))
            {
                // 已经挂过了。WinUI 无法移除匿名委托，重复订阅会导致一次点击发多条事件，
                // 所以这里必须跳过。
                continue;
            }

            try
            {
                BindOne(element, nodeId, eventName);
            }
            catch (Exception ex)
            {
                _warn($"节点 {nodeId}: 事件 \"{eventName}\" 绑定失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 绑定窗口级事件。
    ///
    /// 窗口事件挂在 <see cref="Window"/> 上而不是根元素上，但协议里它们同样以
    /// 根节点的 <c>events</c> 形式传下来，所以这里单独开一条路径。
    /// </summary>
    public void BindWindow(Window window, string nodeId, string eventName)
    {
        if (!_boundWindow.Add((nodeId, eventName)))
        {
            return;
        }

        try
        {
            switch (eventName)
            {
                case "closing":
                    // AppWindow.Closing 在窗口真正消失前触发。
                    // 首版不支持取消：不碰 args.Cancel，事件只用来通知 Python。
                    window.AppWindow.Closing += (_, _) =>
                        _emit(nodeId, "closing", new JsonObject(), false);
                    break;

                case "sizeChanged":
                    window.SizeChanged += (_, e) =>
                        _emit(nodeId, "sizeChanged", new JsonObject
                        {
                            ["width"] = e.Size.Width,
                            ["height"] = e.Size.Height,
                        }, false);
                    break;

                default:
                    _warn($"节点 {nodeId}: 窗口没有事件 \"{eventName}\"");
                    break;
            }
        }
        catch (Exception ex)
        {
            _warn($"节点 {nodeId}: 窗口事件 \"{eventName}\" 绑定失败: {ex.Message}");
        }
    }

    private void BindOne(FrameworkElement element, string nodeId, string eventName)
    {
        void Raise(string name, JsonObject? data) =>
            _emit(nodeId, name, data, _suppressed.Contains(element));

        switch (eventName)
        {
            case "click":
                switch (element)
                {
                    case Button button:
                        button.Click += (_, _) => Raise("click", new JsonObject());
                        break;
                    case HyperlinkButton link:
                        link.Click += (_, _) => Raise("click", new JsonObject());
                        break;
                    case CheckBox check:
                        check.Click += (_, _) => Raise("click", new JsonObject());
                        break;
                    // ToggleButton 家族都有 Click；RadioButton 也走这一条
                    case ToggleButton toggleButton:
                        toggleButton.Click += (_, _) => Raise("click", new JsonObject());
                        break;
                    case RepeatButton repeat:
                        repeat.Click += (_, _) => Raise("click", new JsonObject());
                        break;
                    // SplitButton 只把左半边的点击当 click，右半边是 Flyout
                    case SplitButton split:
                        split.Click += (_, _) => Raise("click", new JsonObject());
                        break;
                    default:
                        _warn($"节点 {nodeId}: {element.GetType().Name} 没有 click 事件");
                        break;
                }

                break;

            case "change":
                BindChange(element, nodeId, Raise);
                break;

            case "submit":
                if (element is TextBox submitBox)
                {
                    submitBox.KeyDown += (_, e) =>
                    {
                        if (e.Key == VirtualKey.Enter)
                        {
                            Raise("submit", new JsonObject { ["text"] = submitBox.Text });
                        }
                    };
                }
                else
                {
                    _warn($"节点 {nodeId}: {element.GetType().Name} 没有 submit 事件");
                }

                break;

            case "focus":
            case "blur":
                if (element is Control focusControl)
                {
                    if (eventName == "focus")
                    {
                        focusControl.GotFocus += (_, _) => Raise("focus", new JsonObject());
                    }
                    else
                    {
                        focusControl.LostFocus += (_, _) => Raise("blur", new JsonObject());
                    }
                }
                else
                {
                    _warn($"节点 {nodeId}: {element.GetType().Name} 没有焦点事件");
                }

                break;

            case "selectionChanged":
                BindSelectionChanged(element, nodeId, Raise);
                break;

            case "mediaOpened":
            case "mediaEnded":
            case "mediaFailed":
                BindMediaEvent(element, nodeId, eventName, Raise);
                break;

            // 指针事件：悬停展开侧栏、点内容区收起这类交互都靠它
            case "pointerEntered":
            case "pointerExited":
            case "pointerPressed":
                BindPointer(element, nodeId, eventName, Raise);
                break;

            // WebView2 导航事件：协议没有「读回」通道，真实地址只能靠事件主动推给 Python。
            // 只在真的订阅了这些事件时才挂，不为读地址白拉起 CoreWebView2。
            case "navigationStarting":
            case "navigationCompleted":
            case "sourceChanged":
                BindWebView2Navigation(element, nodeId, eventName, Raise);
                break;

            // 这两个事件的订阅时机不在绑定阶段：menu 由 contextMenu 的菜单项自行回传，
            // imageFailed 跟着 BitmapImage 的加载走。这里认下名字，免得刷「未知事件」。
            case "menu":
            case "imageFailed":
                break;

            default:
                _warn($"节点 {nodeId}: 未知事件 \"{eventName}\"");
                break;
        }
    }

    private void BindChange(
        FrameworkElement element,
        string nodeId,
        Action<string, JsonObject?> raise)
    {
        switch (element)
        {
            case TextBox box:
                box.TextChanged += (_, _) =>
                    raise("change", new JsonObject { ["text"] = box.Text });
                break;

            case CheckBox check:
                void OnCheckChanged(object sender, RoutedEventArgs e) =>
                    raise("change", new JsonObject { ["checked"] = check.IsChecked ?? false });

                check.Checked += OnCheckChanged;
                check.Unchecked += OnCheckChanged;
                break;

            case RadioButton radio:
                radio.Checked += (_, _) => raise("change", new JsonObject
                {
                    ["checked"] = true,
                    ["value"] = radio.Content as string,
                });
                break;

            case ToggleSwitch toggle:
                toggle.Toggled += (_, _) =>
                    raise("change", new JsonObject { ["checked"] = toggle.IsOn });
                break;

            // CheckBox / RadioButton 已在上面的分支里处理掉了，这里轮到 ToggleButton
            case ToggleButton toggleButton:
                void OnToggle(object sender, RoutedEventArgs e) =>
                    raise("change", new JsonObject { ["checked"] = toggleButton.IsChecked ?? false });

                toggleButton.Checked += OnToggle;
                toggleButton.Unchecked += OnToggle;
                break;

            case Slider slider:
                slider.ValueChanged += (_, e) =>
                    raise("change", new JsonObject { ["value"] = e.NewValue });
                break;

            case ComboBox combo:
                combo.SelectionChanged += (_, _) => raise("change", SelectionPayload(combo));
                break;

            case ListView list:
                list.SelectionChanged += (_, _) => raise("change", SelectionPayload(list));
                break;

            default:
                _warn($"节点 {nodeId}: {element.GetType().Name} 没有 change 事件");
                break;
        }
    }

    private void BindSelectionChanged(
        FrameworkElement element,
        string nodeId,
        Action<string, JsonObject?> raise)
    {
        switch (element)
        {
            case ComboBox combo:
                combo.SelectionChanged += (_, _) => raise("selectionChanged", SelectionPayload(combo));
                break;

            case ListView list:
                list.SelectionChanged += (_, _) => raise("selectionChanged", SelectionPayload(list));
                break;

            default:
                _warn($"节点 {nodeId}: {element.GetType().Name} 没有 selectionChanged 事件");
                break;
        }
    }

    private void BindPointer(
        FrameworkElement element,
        string nodeId,
        string eventName,
        Action<string, JsonObject?> raise)
    {
        switch (eventName)
        {
            case "pointerEntered":
                element.PointerEntered += (_, _) => raise("pointerEntered", new JsonObject());
                break;

            case "pointerExited":
                element.PointerExited += (_, _) => raise("pointerExited", new JsonObject());
                break;

            case "pointerPressed":
                element.PointerPressed += (_, _) => raise("pointerPressed", new JsonObject());
                break;

            default:
                _warn($"节点 {nodeId}: 未知指针事件 \"{eventName}\"");
                break;
        }
    }

    private void BindMediaEvent(
        FrameworkElement element,
        string nodeId,
        string eventName,
        Action<string, JsonObject?> raise)
    {
        if (element is not MediaPlayerElement media)
        {
            _warn($"节点 {nodeId}: {element.GetType().Name} 没有 {eventName} 事件");
            return;
        }

        // 没设 src 时控件还没有 MediaPlayer，补一个，否则事件永远绑不上
        var player = media.MediaPlayer;
        if (player is null)
        {
            player = new MediaPlayer();
            media.SetMediaPlayer(player);
        }

        switch (eventName)
        {
            case "mediaOpened":
                player.MediaOpened += (_, _) => raise("mediaOpened", new JsonObject
                {
                    ["duration"] = player.PlaybackSession?.NaturalDuration.TotalSeconds ?? 0,
                });
                break;

            case "mediaEnded":
                player.MediaEnded += (_, _) => raise("mediaEnded", new JsonObject());
                break;

            case "mediaFailed":
                player.MediaFailed += (_, e) => raise("mediaFailed", new JsonObject
                {
                    ["error"] = e.ErrorMessage ?? "unknown",
                });
                break;
        }
    }

    /// <summary>
    /// WebView2 的导航事件。协议里 invoke 没有返回值，宿主没法被「问」当前网址，
    /// 所以由这里主动把浏览器地址推回 Python，Python 侧记进 WebView2.current_url。
    ///
    /// sourceChanged 挂在 CoreWebView2 而不是 XAML 控件上：它连 history.pushState /
    /// location.hash 这类没走 NavigationStarting 的地址变化也能覆盖。CoreWebView2 是
    /// 异步初始化的，早了拿不到，所以要么直接挂、要么等 CoreWebView2Initialized。
    /// </summary>
    private static void BindWebView2Navigation(
        FrameworkElement element,
        string nodeId,
        string eventName,
        Action<string, JsonObject?> raise)
    {
        // 缺运行时时这个位置是灰色降级 Border，静默跳过：没有浏览器就没有地址可报。
        if (element is not WebView2 web)
        {
            return;
        }

        // XAML 控件的事件在 UI 线程，但 CoreWebView2 的回调不保证。
        // 统一调度回 UI 线程再 emit，避免跨线程碰控件和 _suppressed 集合。
        void Post(JsonObject data)
        {
            var queue = web.DispatcherQueue;
            if (queue is not null && !queue.HasThreadAccess)
            {
                queue.TryEnqueue(() => raise(eventName, data));
            }
            else
            {
                raise(eventName, data);
            }
        }

        switch (eventName)
        {
            case "navigationStarting":
                web.NavigationStarting += (_, e) => Post(new JsonObject
                {
                    ["url"] = e.Uri ?? "",
                });
                break;

            case "navigationCompleted":
                // 这个事件参数不带 Uri（NavigationStarting 才带），
                // 地址从 CoreWebView2 取，回落再用 XAML 控件的 Source。
                web.NavigationCompleted += (_, e) => Post(new JsonObject
                {
                    ["url"] = web.CoreWebView2?.Source ?? web.Source?.ToString() ?? "",
                    ["isSuccess"] = e.IsSuccess,
                    ["httpStatusCode"] = e.HttpStatusCode,
                });
                break;

            case "sourceChanged":
                void AttachCore()
                {
                    var core = web.CoreWebView2;
                    if (core is null)
                    {
                        return;
                    }

                    // 补一次当前地址：初始化完成前发生的导航不会再有任何事件。
                    // Core 刚起来时常是 about:blank，那不是用户要的网址，别覆盖掉。
                    var initial = core.Source ?? "";
                    if (!string.IsNullOrEmpty(initial) && initial != "about:blank")
                    {
                        Post(new JsonObject { ["url"] = initial });
                    }
                    core.SourceChanged += (_, _) =>
                        Post(new JsonObject { ["url"] = core.Source ?? "" });
                }

                if (web.CoreWebView2 is not null)
                {
                    AttachCore();
                }
                else
                {
                    web.CoreWebView2Initialized += (_, _) => AttachCore();
                }

                break;
        }
    }

    private static JsonObject SelectionPayload(object source)
    {
        var index = source switch
        {
            ComboBox combo => combo.SelectedIndex,
            ListView list => list.SelectedIndex,
            _ => -1,
        };

        var value = source switch
        {
            ComboBox combo => ExtractItemValue(combo.SelectedItem),
            ListView list => ExtractItemValue(list.SelectedItem),
            _ => null,
        };

        var payload = new JsonObject { ["index"] = index };
        if (value is not null)
        {
            payload["value"] = value;
        }

        return payload;
    }

    /// <summary>
    /// 从选中项里取出可回传的字符串。
    /// 带图标的列表项是 StackPanel，真正的文字存在它的 Tag 上。
    /// </summary>
    private static string? ExtractItemValue(object? item) => item switch
    {
        null => null,
        string text => text,
        FrameworkElement element => element.Tag as string,
        _ => item.ToString(),
    };
}
