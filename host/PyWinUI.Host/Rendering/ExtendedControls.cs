using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PyWinUI.Host.Rendering;

/// <summary>
/// 扩展控件工厂：补齐 WinUI 3 常用但首版没接进来的控件类型。
///
/// 返回 null 表示这个类型不归本文件管，由 NodeFactory 继续走原逻辑。
/// 之所以单独拆文件，是因为这些控件的「创建 + 属性 + 事件」是一整组新增能力，
/// 混进原 switch 会让原本稳定的基础控件代码变得难读。
///
/// 容器型控件（Viewbox / BorderBox / Expander / TwoPaneView / RefreshContainer）
/// 一律预置一个内层 StackPanel：FormRenderer.ChildHostOf 只认 Panel，
/// 有这层壳子节点才能像 Column / Row 一样直接挂进去。
/// </summary>
internal static class ExtendedControls
{
    public static FrameworkElement? Create(string type, JsonObject? props)
    {
        switch (type)
        {
            // ---------------- 表单输入 ----------------
            case "NumberBox":
                // 不设 HorizontalAlignment.Stretch：在 StackPanel 的无限宽测量里
                // NumberBox 会被量成 0 宽，反而什么都不显示。
                return new NumberBox
                {
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                    MinWidth = 160,
                };

            case "PasswordBox":
                return new PasswordBox();

            case "RichTextBlock":
                return new RichTextBlock { TextWrapping = TextWrapping.Wrap };

            case "RichEditBox":
                return new RichEditBox();

            case "AutoSuggestBox":
                return new AutoSuggestBox();

            case "DatePicker":
                return new DatePicker();

            case "TimePicker":
                return new TimePicker();

            case "CalendarDatePicker":
                return new CalendarDatePicker();

            case "CalendarView":
                return new CalendarView { SelectionMode = CalendarViewSelectionMode.Single };

            case "RatingControl":
                return new RatingControl();

            case "ColorPicker":
                return new ColorPicker { IsMoreButtonVisible = false };

            case "DropDownButton":
                return new DropDownButton { Content = "选择" };

            case "ToggleSplitButton":
                return new ToggleSplitButton { Content = "选项" };

            case "PersonPicture":
                return new PersonPicture { Width = 64, Height = 64 };

            // ---------------- 数据 / 导航 ----------------
            case "GridView":
                return new GridView { IsItemClickEnabled = true, SelectionMode = ListViewSelectionMode.Single };

            case "FlipView":
                return new FlipView();

            case "Pivot":
                return new Pivot();

            case "TabView":
                return new TabView { IsAddTabButtonVisible = false };

            case "TreeView":
                return new TreeView();

            case "NavigationView":
                return new NavigationView
                {
                    IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
                    IsSettingsVisible = false,
                    PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
                    Content = new StackPanel { Orientation = Orientation.Vertical },
                };

            case "MenuBar":
                return new MenuBar();

            case "CommandBar":
                return new CommandBar();

            case "BreadcrumbBar":
                return new BreadcrumbBar();

            case "ItemsView":
                return new ItemsView();

            case "RadioButtons":
                return new RadioButtons();

            case "SelectorBar":
                return new SelectorBar();

            // ---------------- 信息 / 容器 ----------------
            case "InfoBar":
                return new InfoBar { IsOpen = true, IsClosable = false };

            case "Expander":
                return new Expander
                {
                    Content = new StackPanel { Orientation = Orientation.Vertical },
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };

            case "BorderBox":
                return new Border
                {
                    Child = new StackPanel { Orientation = Orientation.Vertical },
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };

            case "Viewbox":
                return new Viewbox
                {
                    Child = new StackPanel { Orientation = Orientation.Vertical },
                    Stretch = Stretch.Uniform,
                };

            case "RelativePanel":
                return new RelativePanel();

            case "TwoPaneView":
                return new TwoPaneView { Pane1 = new StackPanel { Orientation = Orientation.Vertical } };

            case "RefreshContainer":
                return new RefreshContainer { Content = new StackPanel { Orientation = Orientation.Vertical } };

            default:
                return null;
        }
    }

    /// <summary>这些类型在协议里允许带 children。</summary>
    public static bool IsContainerType(string type) => type is
        "BorderBox" or "Viewbox" or "RelativePanel" or "Expander" or
        "TwoPaneView" or "RefreshContainer" or "NavigationView";
}
