using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Playback;
using PyWinUI.Host.Protocol;
using PyWinUI.Host.Rendering;
using PyWinUI.Host.Transport;

namespace PyWinUI.Host;

/// <summary>
/// 会话协调者：串起传输层、协议解析和渲染器。
///
/// 线程模型是这个类最需要注意的地方：
///
/// - 读 stdin 会阻塞，绝不能占 UI 线程，所以放在后台线程
/// - 所有 XAML 操作必须在 UI 线程，所以消息解析完要 DispatcherQueue 排队
/// - 写 stdout 是线程安全的（StdioChannel 内部加锁），UI 线程直接写
///
/// 这套分工和 WinUI 自身的设计一致，也是它不会像进程内托管那样死锁的原因。
/// </summary>
internal sealed class Session
{
    private readonly Window _window;
    private readonly DispatcherQueue _dispatcher;
    private readonly StdioChannel _channel;
    private readonly FormRenderer _renderer;
    private readonly QueryExecutor _queries;

    private long _rev;
    private bool _handshaken;
    private bool _closed;

    public Session(Window window, DispatcherQueue dispatcher)
    {
        _window = window;
        _dispatcher = dispatcher;
        _channel = new StdioChannel();
        _queries = new QueryExecutor(window);
        _renderer = new FormRenderer(
            window,
            Emit,
            message => HostLog.Write("[warn] " + message),
            (code, message, nodeId) => Send(Outbound.Error(code, message, nodeId)));

        _window.Closed += OnWindowClosed;
    }

    public void Start()
    {
        _channel.LineReceived += OnLineReceived;
        _channel.Closed += OnInputClosed;
        _channel.Start();
        HostLog.Write("session started, waiting for hello");
    }

    // ==================================================================
    // 入站
    // ==================================================================

    private void OnLineReceived(string line)
    {
        if (_closed)
        {
            return;
        }

        HostLog.Write("<< " + Truncate(line));

        InboundMessage message;
        try
        {
            message = Inbound.Parse(line);
        }
        catch (ProtocolException ex)
        {
            HostLog.Write($"[protocol] {ex.Code}: {ex.Message}");
            Send(Outbound.Error(ex.Code, ex.Message, ex.NodeId));

            if (ex.IsFatal)
            {
                _dispatcher.TryEnqueue(() => Shutdown(1));
            }

            return;
        }

        // 解析在后台线程完成，渲染必须回 UI 线程
        _dispatcher.TryEnqueue(() => Handle(message));
    }

    private void Handle(InboundMessage message)
    {
        if (_closed)
        {
            return;
        }

        if (!_handshaken)
        {
            HandleHandshake(message);
            return;
        }

        switch (message)
        {
            case RenderMessage render:
                _rev = render.Rev;
                // 窗口能力（最大化/最小化）由 ApplyWindowSpec 在成功落地窗口属性时一并交还，
                // 渲染失败时窗口保持加载态的小尺寸——那里能最大化毫无意义。
                _renderer.Render(render);
                Send(Outbound.Ack(render.Rev));
                break;

            case PatchMessage patch:
                _rev = patch.Rev;
                _renderer.Apply(patch);
                Send(Outbound.Ack(patch.Rev));
                break;

            case InvokeMessage invoke:
                HandleInvoke(invoke);
                break;

            case QueryMessage query:
                HandleQuery(query);
                break;

            case DialogMessage dialog:
                ShowDialog(dialog);
                Send(Outbound.Ack(_rev));
                break;

            case CloseMessage close:
                Shutdown(close.Code);
                break;

            case HelloMessage:
                Send(Outbound.Error("bad-protocol", "重复的 hello 消息", null));
                break;
        }
    }

    private void HandleHandshake(InboundMessage message)
    {
        if (message is not HelloMessage hello)
        {
            HostLog.Write("first message is not hello, aborting");
            Send(Outbound.Error("bad-protocol", "首条消息必须是 hello", null));
            Shutdown(1);
            return;
        }

        if (hello.Protocol != Wire.Version)
        {
            HostLog.Write($"protocol mismatch: client={hello.Protocol} host={Wire.Version}");
            Send(Outbound.Error(
                "version-mismatch",
                $"协议版本不匹配：客户端 {hello.Protocol}，宿主 {Wire.Version}",
                null));
            Shutdown(1);
            return;
        }

        _handshaken = true;
        HostLog.Write($"handshake ok (client={hello.Client} pid={hello.Pid})");

        Send(Outbound.Ready(Capabilities.DescribeOs(), Capabilities.Detect()));
    }

    private void HandleInvoke(InvokeMessage invoke)
    {
        // 窗口级命令不依赖任何节点，先处理，避免根节点还没建好时被挡住
        if (TryWindowInvoke(invoke.Method))
        {
            return;
        }

        if (!_renderer.TryGetElement(invoke.Id, out var element))
        {
            Send(Outbound.Error("unknown-node", $"invoke 的目标不存在: {invoke.Id}", invoke.Id));
            return;
        }

        try
        {
            switch (invoke.Method)
            {
                case "focus":
                    if (element is Control control)
                    {
                        control.Focus(FocusState.Programmatic);
                    }
                    else
                    {
                        element.Focus(FocusState.Programmatic);
                    }

                    break;

                case "select_all":
                    switch (element)
                    {
                        case TextBox box:
                            box.SelectAll();
                            break;
                        case PasswordBox passwordBox:
                            passwordBox.SelectAll();
                            break;
                        case RichEditBox richBox:
                            richBox.Document.Selection.Expand(Microsoft.UI.Text.TextRangeUnit.Story);
                            break;
                    }

                    break;

                case "scroll_to":
                    var index = ValueParsers.AsInt(invoke.Args?["index"]) ?? 0;
                    // ListViewBase 同时覆盖 ListView 和 GridView
                    if (element is ListViewBase list && list.Items.Count > index && index >= 0)
                    {
                        list.ScrollIntoView(list.Items[index]);
                    }

                    break;

                case "play":
                    if (element is MediaPlayerElement playMedia)
                    {
                        playMedia.MediaPlayer?.Play();
                    }

                    break;

                case "pause":
                    if (element is MediaPlayerElement pauseMedia)
                    {
                        pauseMedia.MediaPlayer?.Pause();
                    }

                    break;

                case "stop":
                    if (element is MediaPlayerElement stopMedia
                        && stopMedia.MediaPlayer is { } stopPlayer)
                    {
                        // MediaPlayer 没有 Stop()，只能暂停后把播放位置归零
                        stopPlayer.Pause();
                        stopPlayer.PlaybackSession.Position = TimeSpan.Zero;
                    }

                    break;

                case "seek":
                    if (element is MediaPlayerElement seekMedia
                        && seekMedia.MediaPlayer is { } seekPlayer)
                    {
                        var seconds = ValueParsers.AsDouble(invoke.Args?["seconds"]) ?? 0;
                        seekPlayer.PlaybackSession.Position =
                            TimeSpan.FromSeconds(Math.Max(0, seconds));
                    }

                    break;

                // WebView2 的导航动作。降级占位是 Border，匹配不上就静默跳过：
                // 运行时都没装，刷新/后退本来也没有意义。
                case "navigate":
                    if (element is WebView2 navigateWeb
                        && ValueParsers.AsString(invoke.Args?["url"]) is { } url
                        && Uri.TryCreate(url, UriKind.Absolute, out var target))
                    {
                        navigateWeb.Source = target;
                    }

                    break;

                case "navigate_to_string":
                    if (element is WebView2 htmlWeb
                        && ValueParsers.AsString(invoke.Args?["html"]) is { } html)
                    {
                        // 冷启动时 CoreWebView2 可能还没好，直接调会抛异常，见下方 helper
                        _ = NavigateToStringAsync(htmlWeb, html, invoke.Id);
                    }

                    break;

                case "reload":
                    if (element is WebView2 reloadWeb)
                    {
                        reloadWeb.Reload();
                    }

                    break;

                case "go_back":
                    if (element is WebView2 backWeb && backWeb.CanGoBack)
                    {
                        backWeb.GoBack();
                    }

                    break;

                case "go_forward":
                    if (element is WebView2 forwardWeb && forwardWeb.CanGoForward)
                    {
                        forwardWeb.GoForward();
                    }

                    break;

                default:
                    if (!ExtendedInvoke.TryInvoke(
                            element, invoke.Method, invoke.Args, invoke.Id,
                            message => HostLog.Write("[warn] " + message), Emit))
                    {
                        Send(Outbound.Error(
                            "bad-protocol",
                            $"未知的 invoke 方法: {invoke.Method}",
                            invoke.Id));
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Send(Outbound.Error("internal", $"invoke {invoke.Method} 失败: {ex.Message}", invoke.Id));
        }
    }

    /// <summary>
    /// 最小化 / 最大化 / 还原 / 激活。这些是 OverlappedPresenter 的能力，
    /// 全屏或紧凑型 presenter 下静默跳过，不让整个窗口报错。
    /// </summary>
    private bool TryWindowInvoke(string method)
    {
        if (method is not ("minimize" or "maximize" or "restore" or "activate"))
        {
            return false;
        }

        try
        {
            if (method == "activate")
            {
                _window.Activate();
                return true;
            }

            if (_window.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                switch (method)
                {
                    case "minimize": presenter.Minimize(); break;
                    case "maximize": presenter.Maximize(); break;
                    case "restore": presenter.Restore(); break;
                }
            }
        }
        catch (Exception ex)
        {
            Send(Outbound.Error("internal", $"窗口命令 {method} 失败: {ex.Message}", null));
        }

        return true;
    }

    /// <summary>
    /// 读一个运行时值。query 走的是和 render/patch 相同的 UI 线程调度，
    /// 所以读取的是「已经落地的最新状态」，不会读到半应用完的补丁。
    /// 节点不存在时仍然允许窗口级键（例如 windowState），其余回 ok=false。
    /// </summary>
    private void HandleQuery(QueryMessage query)
    {
        _renderer.TryGetElement(query.Node, out var element);
        try
        {
            var value = _queries.Read(element, query.Key, query.Args);
            Send(Outbound.Result(query.Id, true, value));
        }
        catch (Exception ex)
        {
            Send(Outbound.Result(query.Id, false, null, ex.Message));
        }
    }

    /// <summary>
    /// NavigateToString 要求 CoreWebView2 已经就绪。控件刚建好、Runtime 冷启动时
    /// Core 还在异步初始化，此时直接调会抛「valid CoreWebView2 is not present」。
    /// HandleInvoke 本身跑在 UI 线程，await 会把控制权交回消息泵，不会死锁。
    /// </summary>
    private async Task NavigateToStringAsync(WebView2 web, string html, string? nodeId)
    {
        try
        {
            if (web.CoreWebView2 is null)
            {
                await web.EnsureCoreWebView2Async();
            }

            web.NavigateToString(html);
        }
        catch (Exception ex)
        {
            HostLog.Write("[webview2] NavigateToString failed: " + ex.Message);
            Send(Outbound.Error("internal", $"navigate_to_string 失败: {ex.Message}", nodeId));
        }
    }

    // ==================================================================
    // 窗口装饰
    // ==================================================================

    // 加载态 -> 常规态的窗口装饰切换已并入 FormRenderer.ApplyWindowSpec：
    // 那里能同时看到协议给的 resizable / minimizable / maximizable，
    // 分两处设置会互相覆盖。

    // ==================================================================
    // 对话框
    // ==================================================================

    private bool _dialogOpen;

    /// <summary>
    /// 显示模态对话框。
    ///
    /// WinUI 限制同一时刻只能有一个 ContentDialog，所以这里用标志位挡住并发请求——
    /// 不加这个判断，连续触发会抛「Only a single ContentDialog can be open at any time」。
    /// </summary>
    private async void ShowDialog(DialogMessage message)
    {
        if (_dialogOpen)
        {
            Send(Outbound.Error("busy", "已有对话框未关闭，本次请求被丢弃", null));
            return;
        }

        // 窗口 Content 刚切换完的那一帧 XamlRoot 可能还是 null，
        // 此时直接放弃会让「启动后立刻弹提示」这种常见用法莫名失效。
        // 等几帧再取，最多 500ms；ContentDialog 本来就是异步的，等得起。
        var xamlRoot = _window.Content?.XamlRoot;
        for (var attempt = 0; attempt < 10 && xamlRoot is null; attempt++)
        {
            await Task.Delay(50);
            xamlRoot = _window.Content?.XamlRoot;
        }

        if (xamlRoot is null)
        {
            Send(Outbound.Error("internal", "窗口还没有内容，无法显示对话框", null));
            return;
        }

        _dialogOpen = true;
        try
        {
            // kind 以前解析了却没用：info / warn / error 三个级别长得一模一样。
            // 这里用图标、颜色和按钮文字把级别显式区分开。
            var kind = (message.Kind ?? "info").ToLowerInvariant();
            var (glyph, accent, buttonText) = kind switch
            {
                "warn" or "warning" => ("\uE7BA", Microsoft.UI.Colors.Orange, "我知道了"),
                "error" => ("\uEA39", Microsoft.UI.Colors.OrangeRed, "关闭"),
                "success" => ("\uE930", Microsoft.UI.Colors.ForestGreen, "好的"),
                _ => ("\uE946", Microsoft.UI.Colors.DodgerBlue, "知道了"),
            };

            var body = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Margin = new Thickness(0, 8, 0, 0),
            };
            body.Children.Add(new FontIcon
            {
                Glyph = glyph,
                FontSize = 26,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(accent),
                VerticalAlignment = VerticalAlignment.Center,
            });
            body.Children.Add(new TextBlock
            {
                Text = message.Body,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var dialog = new ContentDialog
            {
                Title = string.IsNullOrEmpty(message.Title) ? "提示" : message.Title,
                Content = body,
                CloseButtonText = buttonText,
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot,
            };

            HostLog.Write($"[dialog] show ({kind}): {message.Title} / {message.Body}");
            await dialog.ShowAsync();
            HostLog.Write("[dialog] closed");
        }
        catch (Exception ex)
        {
            HostLog.Write("dialog failed: " + ex.Message);
            Send(Outbound.Error("internal", $"对话框显示失败: {ex.Message}", null));
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    // ==================================================================
    // 出站
    // ==================================================================

    private void Emit(string nodeId, string eventName, JsonObject? data, bool echo)
    {
        if (_closed)
        {
            return;
        }

        Send(Outbound.Event(nodeId, eventName, _rev, data, echo));
    }

    private void Send(string line)
    {
        HostLog.Write(">> " + Truncate(line));
        _channel.Send(line);
    }

    private static string Truncate(string text) =>
        text.Length <= 500 ? text : text[..500] + $"…(+{text.Length - 500})";

    // ==================================================================
    // 关闭
    // ==================================================================

    private void OnInputClosed(string? error)
    {
        if (_closed)
        {
            return;
        }

        HostLog.Write(error is null
            ? "stdin closed (python side exited)"
            : $"stdin closed with error: {error}");

        // Python 那边没了，界面留着也没意义
        _dispatcher.TryEnqueue(() => Shutdown(0));
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_closed)
        {
            return;
        }

        HostLog.Write("window closed by user");
        _closed = true;
        _channel.Send(Outbound.Bye(_exitCode));
        _channel.Dispose();
        Application.Current.Exit();
    }

    /// <summary>退出码。记在字段上，是因为关窗要经过 Closed 事件，那里拿不到局部变量。</summary>
    private int _exitCode;

    private void Shutdown(int code)
    {
        if (_closed)
        {
            return;
        }

        _exitCode = code;
        HostLog.Write($"shutdown (code={code})");

        // 先正常关窗口，让 closing 事件能发出去——Emit 会检查 _closed，
        // 所以不能像以前那样先把 _closed 置位再退出。
        try
        {
            _window.Close();
        }
        catch (Exception ex)
        {
            HostLog.Write("close window failed: " + ex.Message);
        }

        if (_closed)
        {
            // Closed 事件已经把收尾做完（正常用户关窗走的就是这条路）
            return;
        }

        _closed = true;

        try
        {
            _channel.Send(Outbound.Bye(code));
        }
        catch
        {
        }

        _channel.Dispose();
        Application.Current.Exit();
    }
}
