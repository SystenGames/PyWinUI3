using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace PyWinUI.Host.Rendering;

/// <summary>
/// 控件类型名 → WinUI 控件的映射。
///
/// 需要看属性才能决定类型的少数情况（例如带 <c>password</c> 的 Entry
/// 要造 PasswordBox 而不是 TextBox），所以这里接受 props。
///
/// 未知类型不抛异常：渲染成醒目的占位块并回一条 error，
/// 这样用户能立刻看出是哪一块写错了，而不是整个窗口白屏。
/// </summary>
internal static class NodeFactory
{
    public static FrameworkElement Create(
        string type,
        string nodeId,
        JsonObject? props,
        Action<string> warn)
    {
        switch (type)
        {
            case "Column":
                return new StackPanel { Orientation = Orientation.Vertical };

            case "Row":
                return new StackPanel { Orientation = Orientation.Horizontal };

            case "Grid":
                return new Grid();

            case "Scroll":
                return CreateScroll();

            case "Label":
                return new TextBlock();

            case "Button":
                return new Button();

            case "HyperlinkButton":
                return new HyperlinkButton();

            // 下面三种都是 ButtonBase 家族：ToggleButton 保持按下状态，
            // SplitButton 左右两段（左执行、右弹菜单），RepeatButton 按住连发
            case "ToggleButton":
                return new ToggleButton();

            case "SplitButton":
                return new SplitButton();

            case "RepeatButton":
                return new RepeatButton();

            case "Entry":
                // PasswordBox 不是 TextBox 的子类，必须在这里分叉
                return IsTrue(props?["password"]) ? new PasswordBox() : new TextBox();

            case "Checkbox":
                return new CheckBox();

            case "Radio":
                return new RadioButton();

            case "Switch":
                return new ToggleSwitch();

            case "Slider":
                return new Slider();

            case "ProgressBar":
                return new ProgressBar();

            case "ProgressRing":
                return new ProgressRing
                {
                    IsActive = true,
                    Width = 40,
                    Height = 40,
                };

            case "Media":
                // MediaPlayerElement 自带传输控件（播放/暂停/进度条），
                // 先用系统那套，需要自定义再关掉自己画
                return new MediaPlayerElement
                {
                    AreTransportControlsEnabled = true,
                    AutoPlay = false,
                };

            case "ComboBox":
                return new ComboBox();

            case "ListBox":
                // 用 ListView 而不是 ListBox：前者是 WinUI 的现代列表控件，
                // 自带虚拟化和 Fluent 样式
                return new ListView();

            case "WebView2":
                return CreateWebView2(nodeId, warn);

            case "Image":
                return new Image();

            case "Separator":
                return CreateSeparator();

            default:
                // 扩展控件单独拆在 ExtendedControls，避免这里变成上百行的巨型 switch
                if (ExtendedControls.Create(type, props) is { } extended)
                {
                    return extended;
                }

                warn($"节点 {nodeId}: 未知控件类型 \"{type}\"");
                return CreatePlaceholder(type);
        }
    }

    private static bool IsTrue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    /// <summary>
    /// WebView2：运行时缺失时换成灰色降级占位，绝不把控件交去初始化。
    ///
    /// 探测结果在 <see cref="WebView2Runtime"/> 里缓存，多个 WebView2 节点只查一次。
    /// </summary>
    private static FrameworkElement CreateWebView2(string nodeId, Action<string> warn)
    {
        if (!WebView2Runtime.IsAvailable)
        {
            warn($"节点 {nodeId}: 未找到 WebView2 Runtime，已显示降级占位");
            return CreateWebView2Fallback();
        }

        return new WebView2
        {
            // 不给默认 Source：没指定就保持空白，由 Python 侧随后 set / navigate 决定
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
    }

    /// <summary>
    /// 缺运行时的灰色矩形：中间是居中的「加载失败」小页面，下方是具体原因。
    ///
    /// 外层刻意用 Border 而不是 Panel：FormRenderer 的 ChildHostOf 只认
    /// Panel / ScrollViewer，这样即使协议给这个节点带了 children，也不会把
    /// 子节点插进降级页把提示冲掉，而是老老实实回一条「不是容器」的协议错误。
    /// </summary>
    private static FrameworkElement CreateWebView2Fallback()
    {
        var white = new SolidColorBrush(Colors.White);
        var softWhite = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));

        var icon = new FontIcon
        {
            Glyph = "\uE783",
            FontSize = 40,
            Foreground = white,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var title = new TextBlock
        {
            Text = "加载失败",
            FontSize = 16,
            Foreground = white,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var detail = new TextBlock
        {
            Text = "该控件无法正常加载：Webview2 Runtime未找到。",
            FontSize = 12,
            Foreground = softWhite,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 320,
        };

        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 10,
        };
        stack.Children.Add(icon);
        stack.Children.Add(title);
        stack.Children.Add(detail);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(255, 0x76, 0x76, 0x76)),
            Child = stack,
            MinWidth = 260,
            MinHeight = 160,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Tag = "webview2-fallback",
        };
    }

    /// <summary>分隔线：1 像素高的横条，随容器拉伸。</summary>
    private static FrameworkElement CreateSeparator() => new Border
    {
        Height = 1,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Background = new SolidColorBrush(Color.FromArgb(48, 128, 128, 128)),
    };

    /// <summary>
    /// 可滚动区域：ScrollViewer 里再套一层 StackPanel。
    ///
    /// ScrollViewer 的 Content 只能放一个元素，而我们的节点模型是「一个节点多个子节点」，
    /// 所以里面这层 StackPanel 是必要的：它才是 FormRenderer 眼里的子节点宿主。
    /// </summary>
    private static FrameworkElement CreateScroll() => new ScrollViewer
    {
        Content = new StackPanel { Orientation = Orientation.Vertical },
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalScrollMode = ScrollMode.Disabled,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Top,
    };

    private static FrameworkElement CreatePlaceholder(string type) => new TextBlock
    {
        Text = $"[未知控件: {type}]",
        Foreground = new SolidColorBrush(Colors.OrangeRed),
        FontStyle = Windows.UI.Text.FontStyle.Italic,
    };
}
