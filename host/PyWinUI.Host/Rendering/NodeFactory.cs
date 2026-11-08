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

            case "Image":
                return new Image();

            case "Separator":
                return CreateSeparator();

            default:
                warn($"节点 {nodeId}: 未知控件类型 \"{type}\"");
                return CreatePlaceholder(type);
        }
    }

    private static bool IsTrue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

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
