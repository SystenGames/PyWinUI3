using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace PyWinUI.Host.Rendering;

/// <summary>
/// 扩展属性分发：新控件的属性 + 首版漏掉的通用属性。
///
/// 调用约定：PropertyApplier 在每条属性上先问这里，返回 true 表示已消费，
/// 返回 false 表示「这个 key / 控件组合不归我管」，交回原有 switch。
/// 这样基础控件的既有行为一行都不用改。
///
/// 已识别但赋值失败的属性也返回 true（并打 warn），否则会掉回主 switch
/// 触发一条误导性的「未知属性」告警。
/// </summary>
internal static class ExtendedProperties
{
    // ------------------------------------------------------------------
    // 图片解码提示
    // ------------------------------------------------------------------

    private sealed class DecodeHints
    {
        public int Width;
        public int Height;
    }

    /// <summary>
    /// DecodePixelWidth/Height 必须在设置 UriSource 之前落到 BitmapImage 上，
    /// 但 props 是字典，用户写 kwargs 的顺序不保证。所以先整包收集一遍，
    /// ApplyImageSource 创建 BitmapImage 时再来取。
    /// ConditionalWeakTable 让提示跟着控件走，节点销毁后自动回收。
    /// </summary>
    private static readonly ConditionalWeakTable<FrameworkElement, DecodeHints> ImageHints = new();

    public static void Prepare(FrameworkElement element, JsonObject? props)
    {
        if (props is null)
        {
            return;
        }

        if (props["decodePixelWidth"] is null && props["decodePixelHeight"] is null)
        {
            return;
        }

        var hints = ImageHints.GetValue(element, _ => new DecodeHints());
        hints.Width = (int)(ValueParsers.AsDouble(props["decodePixelWidth"]) ?? 0);
        hints.Height = (int)(ValueParsers.AsDouble(props["decodePixelHeight"]) ?? 0);
    }

    public static void ApplyDecodeHints(Image image, BitmapImage bitmap)
    {
        if (!ImageHints.TryGetValue(image, out var hints))
        {
            return;
        }

        if (hints.Width > 0)
        {
            bitmap.DecodePixelWidth = hints.Width;
        }

        if (hints.Height > 0)
        {
            bitmap.DecodePixelHeight = hints.Height;
        }
    }

    // ------------------------------------------------------------------
    // 分发
    // ------------------------------------------------------------------

    public static bool TryApply(
        FrameworkElement element,
        string key,
        JsonNode? value,
        Action<string> warn)
    {
        try
        {
            return Apply(element, key, value, warn);
        }
        catch (Exception ex)
        {
            warn($"扩展属性 \"{key}\" 应用失败: {ex.Message}");
            return true;
        }
    }

    private static bool Apply(
        FrameworkElement e,
        string key,
        JsonNode? v,
        Action<string> warn)
    {
        switch (key)
        {
            // ================= 通用 FrameworkElement =================
            case "name":
                e.Name = ValueParsers.AsString(v) ?? "";
                return true;
            case "tag":
                e.Tag = ValueParsers.AsString(v);
                return true;
            case "tabIndex":
                if (e is Control tabControl && ValueParsers.AsInt(v) is { } tabIndex)
                    tabControl.TabIndex = tabIndex;
                return true;
            case "isTabStop":
                if (e is Control stopControl && ValueParsers.AsBool(v) is { } isTabStop)
                    stopControl.IsTabStop = isTabStop;
                return true;
            case "allowDrop":
                if (ValueParsers.AsBool(v) is { } allowDrop)
                    e.AllowDrop = allowDrop;
                return true;
            case "isHitTestVisible":
                if (ValueParsers.AsBool(v) is { } hitTest)
                    e.IsHitTestVisible = hitTest;
                return true;
            case "flowDirection":
                e.FlowDirection = ValueParsers.AsString(v) == "rtl"
                    ? FlowDirection.RightToLeft
                    : FlowDirection.LeftToRight;
                return true;

            // ================= 输入增强 =================
            case "isSpellCheckEnabled":
                if (e is TextBox spell && ValueParsers.AsBool(v) is { } spellCheck)
                    spell.IsSpellCheckEnabled = spellCheck;
                return true;
            case "acceptsReturn":
                if (e is TextBox ret && ValueParsers.AsBool(v) is { } acceptsReturn)
                    ret.AcceptsReturn = acceptsReturn;
                return true;
            case "selectionStart":
                if (e is TextBox selStart && ValueParsers.AsInt(v) is { } start)
                    selStart.SelectionStart = Math.Clamp(start, 0, selStart.Text.Length);
                return true;
            case "selectionLength":
                if (e is TextBox selLen && ValueParsers.AsInt(v) is { } length)
                    selLen.SelectionLength = Math.Clamp(length, 0, Math.Max(0, selLen.Text.Length - selLen.SelectionStart));
                return true;
            case "isReadOnly":
            case "readOnly":
                if (e is TextBox readOnly && ValueParsers.AsBool(v) is { } ro)
                    readOnly.IsReadOnly = ro;
                else if (e is RichEditBox richRo && ValueParsers.AsBool(v) is { } richReadOnly)
                    richRo.IsReadOnly = richReadOnly;
                return true;
            case "decodePixelWidth":
            case "decodePixelHeight":
                // 已在 Prepare() 里收集， ApplyImageSource 会取用；这里只需认下名字
                if (e is not Image)
                    warn($"{key} 只适用于 Image");
                return true;
            case "isEditable":
                if (e is ComboBox editable && ValueParsers.AsBool(v) is { } isEditable)
                    editable.IsEditable = isEditable;
                return true;
            case "isTextSearchEnabled":
                if (e is ComboBox searchable && ValueParsers.AsBool(v) is { } search)
                    searchable.IsTextSearchEnabled = search;
                return true;
            case "selectionMode":
                if (e is ListViewBase listBase && ValueParsers.AsString(v) is { } mode)
                {
                    listBase.SelectionMode = mode.ToLowerInvariant() switch
                    {
                        "none" => ListViewSelectionMode.None,
                        "multiple" => ListViewSelectionMode.Multiple,
                        "extended" => ListViewSelectionMode.Extended,
                        _ => ListViewSelectionMode.Single,
                    };
                }
                else if (e is CalendarView calendar && ValueParsers.AsString(v) is { } calMode)
                {
                    calendar.SelectionMode = calMode.ToLowerInvariant() switch
                    {
                        "multiple" => CalendarViewSelectionMode.Multiple,
                        "none" => CalendarViewSelectionMode.None,
                        _ => CalendarViewSelectionMode.Single,
                    };
                }

                return true;
            case "isFullWindow":
                if (e is MediaPlayerElement fullMedia && ValueParsers.AsBool(v) is { } full)
                    fullMedia.IsFullWindow = full;
                return true;
            case "verticalScrollBarVisibility":
                if (e is ScrollViewer vertical && ValueParsers.AsString(v) is { } verticalMode)
                    vertical.VerticalScrollBarVisibility = ParseScrollBarVisibility(verticalMode);
                return true;
            case "horizontalScrollBarVisibility":
                if (e is ScrollViewer horizontal && ValueParsers.AsString(v) is { } horizontalMode)
                    horizontal.HorizontalScrollBarVisibility = ParseScrollBarVisibility(horizontalMode);
                return true;

            // ================= 扩展控件：与旧 switch 同名的 key =================
            // 只在这些扩展控件上接管；其他控件返回 false，原有逻辑照常生效。
            case "text":
                return ApplyExtendedText(e, v, warn);
            case "value":
                return ApplyExtendedValue(e, v, warn);
            case "items":
                return v is JsonArray array && ApplyExtendedItems(e, array, warn);
            case "selectedIndex":
                return ApplyExtendedSelectedIndex(e, v);
            case "min":
                if (e is NumberBox minBox && ValueParsers.AsDouble(v) is { } min)
                {
                    minBox.Minimum = min;
                    return true;
                }
                return false;
            case "max":
                if (e is NumberBox maxBox && ValueParsers.AsDouble(v) is { } max)
                {
                    maxBox.Maximum = max;
                    return true;
                }
                return false;
            case "stepFrequency":
                if (e is NumberBox stepBox && ValueParsers.AsDouble(v) is { } step)
                {
                    stepBox.SmallChange = step;
                    return true;
                }
                return false;
            case "header":
                return ApplyExtendedHeader(e, v);
            case "placeholder":
                return ApplyExtendedPlaceholder(e, v);
            case "stretch":
                if (e is Viewbox viewbox && ValueParsers.AsStretch(v) is { } stretch)
                {
                    viewbox.Stretch = stretch;
                    return true;
                }
                return false;
            case "color":
                if (e is ColorPicker picker && ValueParsers.AsColor(v) is { } color)
                {
                    picker.Color = color;
                    return true;
                }
                return false;
            case "checked":
                if (e is ToggleSplitButton splitToggle && ValueParsers.AsBool(v) is { } isChecked)
                {
                    splitToggle.IsChecked = isChecked;
                    return true;
                }
                return false;

            // ================= 扩展控件：新增 key =================
            case "date":
                return ApplyDate(e, v, warn);
            case "time":
                return ApplyTime(e, v, warn);
            case "severity":
                if (e is InfoBar infoSeverity && ValueParsers.AsString(v) is { } severity)
                {
                    infoSeverity.Severity = severity.ToLowerInvariant() switch
                    {
                        "success" => InfoBarSeverity.Success,
                        "warning" or "warn" => InfoBarSeverity.Warning,
                        "error" => InfoBarSeverity.Error,
                        _ => InfoBarSeverity.Informational,
                    };
                }
                return true;
            case "message":
                if (e is InfoBar infoMessage)
                    infoMessage.Message = ValueParsers.AsString(v) ?? "";
                return true;
            case "title":
                if (e is InfoBar infoTitle)
                    infoTitle.Title = ValueParsers.AsString(v) ?? "";
                return true;
            case "isOpen":
                if (e is InfoBar infoOpen && ValueParsers.AsBool(v) is { } isOpen)
                    infoOpen.IsOpen = isOpen;
                return true;
            case "isClosable":
                if (e is InfoBar infoClosable && ValueParsers.AsBool(v) is { } isClosable)
                    infoClosable.IsClosable = isClosable;
                return true;
            case "isExpanded":
                if (e is Expander expander && ValueParsers.AsBool(v) is { } isExpanded)
                    expander.IsExpanded = isExpanded;
                return true;
            case "expandDirection":
                if (e is Expander direction && ValueParsers.AsString(v) == "up")
                    direction.ExpandDirection = ExpandDirection.Up;
                return true;
            case "displayName":
                if (e is PersonPicture person)
                    person.DisplayName = ValueParsers.AsString(v) ?? "";
                return true;
            case "initials":
                if (e is PersonPicture initials)
                    initials.Initials = ValueParsers.AsString(v) ?? "";
                return true;
            case "paneDisplayMode":
                if (e is NavigationView nav && ValueParsers.AsString(v) is { } paneMode)
                {
                    nav.PaneDisplayMode = paneMode.ToLowerInvariant() switch
                    {
                        "top" => NavigationViewPaneDisplayMode.Top,
                        "compact" or "leftcompact" => NavigationViewPaneDisplayMode.LeftCompact,
                        "minimal" or "leftminimal" => NavigationViewPaneDisplayMode.LeftMinimal,
                        "auto" => NavigationViewPaneDisplayMode.Auto,
                        _ => NavigationViewPaneDisplayMode.Left,
                    };
                }
                return true;
            case "isPaneOpen":
                if (e is NavigationView pane && ValueParsers.AsBool(v) is { } paneOpen)
                    pane.IsPaneOpen = paneOpen;
                return true;
            case "openPaneLength":
                if (e is NavigationView openPane && ValueParsers.AsDouble(v) is { } openLength)
                    openPane.OpenPaneLength = openLength;
                return true;
            case "compactPaneLength":
                if (e is NavigationView compactPane && ValueParsers.AsDouble(v) is { } compactLength)
                    compactPane.CompactPaneLength = compactLength;
                return true;
            case "isBackEnabled":
                if (e is NavigationView backNav && ValueParsers.AsBool(v) is { } backEnabled)
                    backNav.IsBackEnabled = backEnabled;
                return true;
            case "isSettingsVisible":
                if (e is NavigationView settings && ValueParsers.AsBool(v) is { } settingsVisible)
                    settings.IsSettingsVisible = settingsVisible;
                return true;
            case "spin":
                if (e is NumberBox spinBox && ValueParsers.AsString(v) is { } spin)
                {
                    spinBox.SpinButtonPlacementMode = spin.ToLowerInvariant() switch
                    {
                        "hidden" or "none" => NumberBoxSpinButtonPlacementMode.Hidden,
                        "inline" => NumberBoxSpinButtonPlacementMode.Inline,
                        _ => NumberBoxSpinButtonPlacementMode.Compact,
                    };
                }
                return true;
            case "isClearEnabled":
                if (e is RatingControl clearRating && ValueParsers.AsBool(v) is { } clear)
                    clearRating.IsClearEnabled = clear;
                return true;
            case "maxRating":
                if (e is RatingControl maxRating && ValueParsers.AsInt(v) is { } ratingMax)
                    maxRating.MaxRating = ratingMax;
                return true;
            case "queryText":
                if (e is AutoSuggestBox query)
                    query.Text = ValueParsers.AsString(v) ?? "";
                return true;
            case "content":
                if (e is DropDownButton drop)
                    drop.Content = ValueParsers.AsString(v) ?? "";
                else if (e is ToggleSplitButton toggleSplit)
                    toggleSplit.Content = ValueParsers.AsString(v) ?? "";
                return true;
            case "maxColumns":
                if (e is RadioButtons radios && ValueParsers.AsInt(v) is { } columns)
                    radios.MaxColumns = columns;
                return true;
            case "isAddTabButtonVisible":
                if (e is TabView tabs && ValueParsers.AsBool(v) is { } addVisible)
                    tabs.IsAddTabButtonVisible = addVisible;
                return true;
            case "panePriority":
                if (e is TwoPaneView twoPane && ValueParsers.AsString(v) is { } priority)
                    twoPane.PanePriority = priority.ToLowerInvariant() == "pane2"
                        ? TwoPaneViewPriority.Pane2
                        : TwoPaneViewPriority.Pane1;
                return true;
            case "isMoreButtonVisible":
                if (e is ColorPicker more && ValueParsers.AsBool(v) is { } moreVisible)
                    more.IsMoreButtonVisible = moreVisible;
                return true;

            default:
                return false;
        }
    }

    // ------------------------------------------------------------------
    // 与主 switch 同名的 key：只接管扩展控件
    // ------------------------------------------------------------------

    private static bool ApplyExtendedText(FrameworkElement e, JsonNode? v, Action<string> warn)
    {
        var text = ValueParsers.AsString(v) ?? "";
        switch (e)
        {
            case RichTextBlock rich:
                rich.Blocks.Clear();
                var paragraph = new Microsoft.UI.Xaml.Documents.Paragraph();
                paragraph.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = text });
                rich.Blocks.Add(paragraph);
                return true;

            case RichEditBox edit:
                edit.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, text);
                return true;

            case AutoSuggestBox suggest:
                suggest.Text = text;
                return true;

            case InfoBar info:
                info.Message = text;
                return true;

            case DropDownButton drop:
                drop.Content = text;
                return true;

            case ToggleSplitButton toggleSplit:
                toggleSplit.Content = text;
                return true;

            case NumberBox number:
                // 允许用 text 传数字字符串，方便从 Entry 抄值
                if (double.TryParse(text, out var parsed))
                    number.Value = parsed;
                else
                    warn($"NumberBox text 不是数字: {text}");
                return true;

            default:
                return false;
        }
    }

    private static bool ApplyExtendedValue(FrameworkElement e, JsonNode? v, Action<string> warn)
    {
        if (ValueParsers.AsDouble(v) is not { } number)
        {
            return false;
        }

        switch (e)
        {
            case NumberBox box:
                box.Value = number;
                return true;
            case RatingControl rating:
                rating.Value = number;
                return true;
            default:
                return false;
        }
    }

    private static bool ApplyExtendedHeader(FrameworkElement e, JsonNode? v)
    {
        var header = ValueParsers.AsString(v) ?? "";
        switch (e)
        {
            case Expander expander: expander.Header = header; return true;
            case NumberBox number: number.Header = header; return true;
            case DatePicker date: date.Header = header; return true;
            case TimePicker time: time.Header = header; return true;
            case CalendarDatePicker calendar: calendar.Header = header; return true;
            case ComboBox combo: combo.Header = header; return true;
            case TextBox textBox: textBox.Header = header; return true;
            case PasswordBox password: password.Header = header; return true;
            case AutoSuggestBox suggest: suggest.Header = header; return true;
            default: return false;
        }
    }

    private static bool ApplyExtendedPlaceholder(FrameworkElement e, JsonNode? v)
    {
        var text = ValueParsers.AsString(v) ?? "";
        switch (e)
        {
            case AutoSuggestBox suggest: suggest.PlaceholderText = text; return true;
            case RichEditBox rich: rich.PlaceholderText = text; return true;
            case NumberBox number: number.PlaceholderText = text; return true;
            default: return false;
        }
    }

    private static bool ApplyExtendedSelectedIndex(FrameworkElement e, JsonNode? v)
    {
        if (ValueParsers.AsInt(v) is not { } index)
        {
            return false;
        }

        switch (e)
        {
            case Pivot pivot:
                pivot.SelectedIndex = index;
                return true;
            case RadioButtons radios:
                radios.SelectedIndex = index;
                return true;
            case SelectorBar bar:
                if (index >= 0 && index < bar.Items.Count)
                    bar.SelectedItem = bar.Items[index];
                return true;
            case NavigationView nav:
                if (index >= 0 && index < nav.MenuItems.Count)
                    nav.SelectedItem = nav.MenuItems[index];
                return true;
            case TabView tabs:
                if (index >= 0 && index < tabs.TabItems.Count)
                    tabs.SelectedItem = tabs.TabItems[index];
                return true;
            // GridView / FlipView 都是 Selector，但原有 ComboBox / ListView
            // 的 selectedIndex 仍归主 switch，别在这里截走。
            case Selector selector when selector is not ComboBox and not ListView:
                selector.SelectedIndex = index;
                return true;
            default:
                return false;
        }
    }

    // ------------------------------------------------------------------
    // items：不同控件的条目类型完全不同，集中在这里构造
    // ------------------------------------------------------------------

    private static bool ApplyExtendedItems(FrameworkElement e, JsonArray array, Action<string> warn)
    {
        var labels = new List<string>(array.Count);
        foreach (var node in array)
        {
            labels.Add(node is JsonObject obj
                ? ValueParsers.AsString(obj["label"]) ?? ValueParsers.AsString(node) ?? ""
                : ValueParsers.AsString(node) ?? "");
        }

        switch (e)
        {
            case GridView grid:
                grid.ItemsSource = labels;
                return true;
            case FlipView flip:
                flip.ItemsSource = labels;
                return true;
            // ListView / ComboBox 的 items 由原 switch 处理（支持带图标的富项），
            // 这里刻意不接管。
            case AutoSuggestBox suggest:
                suggest.ItemsSource = labels;
                return true;
            case BreadcrumbBar breadcrumb:
                breadcrumb.ItemsSource = labels;
                return true;
            case ItemsView itemsView:
                itemsView.ItemsSource = labels;
                return true;

            case Pivot pivot:
                pivot.Items.Clear();
                foreach (var label in labels)
                    pivot.Items.Add(new PivotItem { Header = label, Content = new TextBlock { Text = label } });
                return true;

            case TabView tabs:
                tabs.TabItems.Clear();
                foreach (var label in labels)
                    tabs.TabItems.Add(new TabViewItem { Header = label, Tag = label });
                return true;

            case NavigationView nav:
                nav.MenuItems.Clear();
                foreach (var label in labels)
                    nav.MenuItems.Add(new NavigationViewItem { Content = label, Tag = label });
                return true;

            case MenuBar menuBar:
                menuBar.Items.Clear();
                foreach (var label in labels)
                {
                    var item = new MenuBarItem { Title = label, Tag = label };
                    item.Items.Add(new MenuFlyoutItem { Text = label, Tag = label });
                    menuBar.Items.Add(item);
                }
                return true;

            case CommandBar commandBar:
                commandBar.PrimaryCommands.Clear();
                foreach (var label in labels)
                    commandBar.PrimaryCommands.Add(new AppBarButton { Label = label, Tag = label });
                return true;

            case RadioButtons radios:
                radios.Items.Clear();
                foreach (var label in labels)
                    radios.Items.Add(label);
                return true;

            case SelectorBar selectorBar:
                selectorBar.Items.Clear();
                foreach (var label in labels)
                    selectorBar.Items.Add(new SelectorBarItem { Text = label, Tag = label });
                return true;

            case TreeView tree:
                tree.RootNodes.Clear();
                BuildTreeNodes(tree.RootNodes, array);
                return true;

            default:
                return false;
        }
    }

    private static void BuildTreeNodes(IList<TreeViewNode> target, JsonArray array)
    {
        foreach (var node in array)
        {
            string label;
            JsonArray? children = null;
            if (node is JsonObject obj)
            {
                label = ValueParsers.AsString(obj["label"]) ?? "";
                children = obj["children"] as JsonArray;
            }
            else
            {
                label = ValueParsers.AsString(node) ?? "";
            }

            var treeNode = new TreeViewNode { Content = label, IsExpanded = true };
            if (children is not null)
            {
                BuildTreeNodes(treeNode.Children, children);
            }

            target.Add(treeNode);
        }
    }

    // ------------------------------------------------------------------
    // 日期 / 时间 / 滚动条
    // ------------------------------------------------------------------

    private static bool ApplyDate(FrameworkElement e, JsonNode? v, Action<string> warn)
    {
        var raw = ValueParsers.AsString(v);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(raw, out var date))
        {
            warn($"date 不是可解析的日期: {raw}");
            return true;
        }

        switch (e)
        {
            case DatePicker picker:
                picker.SelectedDate = date;
                break;
            case CalendarDatePicker calendar:
                calendar.Date = date;
                break;
            case CalendarView view:
                view.SelectedDates.Clear();
                view.SelectedDates.Add(date);
                view.SetDisplayDate(date);
                break;
        }

        return true;
    }

    private static bool ApplyTime(FrameworkElement e, JsonNode? v, Action<string> warn)
    {
        var raw = ValueParsers.AsString(v);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!TimeSpan.TryParse(raw, out var time))
        {
            warn($"time 不是可解析的时间: {raw}");
            return true;
        }

        if (e is TimePicker picker)
        {
            picker.SelectedTime = time;
        }

        return true;
    }

    private static ScrollBarVisibility ParseScrollBarVisibility(string mode) =>
        mode.ToLowerInvariant() switch
        {
            "visible" => ScrollBarVisibility.Visible,
            "hidden" => ScrollBarVisibility.Hidden,
            "disabled" => ScrollBarVisibility.Disabled,
            _ => ScrollBarVisibility.Auto,
        };
}
