using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace PyWinUI.Host.Interop;

/// <summary>
/// 扩展事件分发：首版只有 click / change / selectionChanged / 媒体 / 指针 / 导航
/// 这十几类事件，键盘、焦点、加载、拖放、以及新控件自己的事件全是空白。
///
/// 和 ExtendedProperties 同样的约定：返回 true 表示已经订阅，
/// 返回 false 表示不认识，交回 EventBridge 打「未知事件」告警。
/// </summary>
internal static class ExtendedEvents
{
    /// <summary>通用事件 + 扩展控件专属事件。</summary>
    public static bool TryBind(
        FrameworkElement element,
        string eventName,
        Action<string, JsonObject?> raise)
    {
        var name = eventName;
        void Post(JsonObject? data = null) => raise(name, data ?? new JsonObject());

        switch (eventName)
        {
            // ---------------- 键盘 ----------------
            case "keyDown":
                element.KeyDown += (_, e) => Post(KeyPayload(e));
                return true;
            case "keyUp":
                element.KeyUp += (_, e) => Post(KeyPayload(e));
                return true;

            // ---------------- 焦点（focus / blur 的别名，语义更贴近 WinUI） ----------------
            case "gotFocus":
                element.GotFocus += (_, _) => Post();
                return true;
            case "lostFocus":
                element.LostFocus += (_, _) => Post();
                return true;

            // ---------------- 生命周期 ----------------
            case "loaded":
                element.Loaded += (_, _) => Post(new JsonObject
                {
                    ["width"] = element.ActualWidth,
                    ["height"] = element.ActualHeight,
                });
                return true;
            case "unloaded":
                element.Unloaded += (_, _) => Post();
                return true;
            case "sizeChanged":
                element.SizeChanged += (_, e) => Post(new JsonObject
                {
                    ["width"] = e.NewSize.Width,
                    ["height"] = e.NewSize.Height,
                });
                return true;

            // ---------------- 输入过程 ----------------
            case "textChanging":
                if (element is TextBox changing)
                {
                    changing.TextChanging += (_, _) => Post(new JsonObject { ["text"] = changing.Text });
                    return true;
                }

                return false;

            // ---------------- 指针 / 点击 ----------------
            case "pointerMoved":
                element.PointerMoved += (_, e) => Post(PositionPayload(element, e));
                return true;
            case "pointerReleased":
                element.PointerReleased += (_, e) => Post(PositionPayload(element, e));
                return true;
            case "pointerWheelChanged":
                element.PointerWheelChanged += (_, e) =>
                {
                    var point = e.GetCurrentPoint(element);
                    Post(new JsonObject
                    {
                        ["x"] = point.Position.X,
                        ["y"] = point.Position.Y,
                        ["delta"] = point.Properties.MouseWheelDelta,
                    });
                };
                return true;
            case "tapped":
                element.Tapped += (_, e) => Post(TappedPayload(element, e));
                return true;
            case "doubleTapped":
                element.DoubleTapped += (_, e) => Post(TappedPayload(element, e));
                return true;
            case "rightTapped":
                element.RightTapped += (_, e) => Post(TappedPayload(element, e));
                return true;
            case "contextRequested":
                element.ContextRequested += (_, _) => Post();
                return true;

            // ---------------- 拖放 ----------------
            case "dragEnter":
            case "dragOver":
            case "dragLeave":
            case "drop":
                return BindDragDrop(element, eventName, Post);

            // ---------------- 扩展控件专属 ----------------
            case "valueChanged":
                switch (element)
                {
                    case NumberBox number:
                        number.ValueChanged += (_, _) => Post(new JsonObject { ["value"] = number.Value });
                        return true;
                    case RatingControl rating:
                        rating.ValueChanged += (_, _) => Post(new JsonObject { ["value"] = rating.Value });
                        return true;
                    case Slider slider:
                        slider.ValueChanged += (_, e) => Post(new JsonObject { ["value"] = e.NewValue });
                        return true;
                    default:
                        return false;
                }

            case "colorChanged":
                if (element is ColorPicker picker)
                {
                    picker.ColorChanged += (_, _) => Post(new JsonObject { ["color"] = ColorText(picker.Color) });
                    return true;
                }

                return false;

            case "dateChanged":
                switch (element)
                {
                    case DatePicker date:
                        date.SelectedDateChanged += (_, _) => Post(new JsonObject
                        {
                            ["date"] = date.SelectedDate?.ToString("yyyy-MM-dd") ?? "",
                        });
                        return true;
                    case CalendarDatePicker calendar:
                        calendar.DateChanged += (_, _) => Post(new JsonObject
                        {
                            ["date"] = calendar.Date?.ToString("yyyy-MM-dd") ?? "",
                        });
                        return true;
                    default:
                        return false;
                }

            case "timeChanged":
                if (element is TimePicker time)
                {
                    time.SelectedTimeChanged += (_, _) => Post(new JsonObject
                    {
                        ["time"] = time.SelectedTime?.ToString(@"hh\:mm") ?? "",
                    });
                    return true;
                }

                return false;

            case "selectedDatesChanged":
                if (element is CalendarView calendarView)
                {
                    calendarView.SelectedDatesChanged += (_, _) => Post(new JsonObject
                    {
                        ["date"] = calendarView.SelectedDates.Count > 0
                            ? calendarView.SelectedDates[0].ToString("yyyy-MM-dd")
                            : "",
                    });
                    return true;
                }

                return false;

            case "querySubmitted":
                if (element is AutoSuggestBox suggest)
                {
                    suggest.QuerySubmitted += (_, e) => Post(new JsonObject
                    {
                        ["text"] = e.QueryText,
                        ["value"] = e.ChosenSuggestion?.ToString() ?? "",
                    });
                    return true;
                }

                return false;

            case "suggestionChosen":
                if (element is AutoSuggestBox suggestChosen)
                {
                    suggestChosen.SuggestionChosen += (_, e) => Post(new JsonObject
                    {
                        ["value"] = e.SelectedItem?.ToString() ?? "",
                    });
                    return true;
                }

                return false;

            case "itemClick":
                switch (element)
                {
                    case GridView grid:
                        grid.ItemClick += (_, e) => Post(new JsonObject { ["value"] = ItemText(e.ClickedItem) });
                        return true;
                    case ListViewBase list:
                        list.ItemClick += (_, e) => Post(new JsonObject { ["value"] = ItemText(e.ClickedItem) });
                        return true;
                    case BreadcrumbBar breadcrumb:
                        breadcrumb.ItemClicked += (_, e) => Post(new JsonObject
                        {
                            ["index"] = e.Index,
                            ["value"] = ItemText(e.Item),
                        });
                        return true;
                    case TreeView tree:
                        tree.ItemInvoked += (_, e) => Post(new JsonObject
                        {
                            ["value"] = (e.InvokedItem as TreeViewNode)?.Content?.ToString() ?? "",
                        });
                        return true;
                    default:
                        return false;
                }

            case "expanding":
                if (element is Expander expanding)
                {
                    expanding.Expanding += (_, _) => Post(new JsonObject { ["expanded"] = true });
                    return true;
                }

                return false;

            case "collapsed":
                if (element is Expander collapsed)
                {
                    collapsed.Collapsed += (_, _) => Post(new JsonObject { ["expanded"] = false });
                    return true;
                }

                return false;

            case "closeButtonClick":
                if (element is InfoBar info)
                {
                    info.CloseButtonClick += (_, _) => Post();
                    return true;
                }

                return false;

            case "tabCloseRequested":
                if (element is TabView tabs)
                {
                    tabs.TabCloseRequested += (_, e) => Post(new JsonObject
                    {
                        ["value"] = (e.Tab as TabViewItem)?.Header?.ToString() ?? "",
                    });
                    return true;
                }

                return false;

            case "paneOpening":
                if (element is NavigationView paneOpening)
                {
                    paneOpening.PaneOpening += (_, _) => Post();
                    return true;
                }

                return false;

            case "paneClosed":
                if (element is NavigationView paneClosed)
                {
                    paneClosed.PaneClosed += (_, _) => Post();
                    return true;
                }

                return false;

            case "displayModeChanged":
                if (element is NavigationView displayMode)
                {
                    displayMode.DisplayModeChanged += (_, _) => Post(new JsonObject
                    {
                        ["mode"] = displayMode.DisplayMode.ToString(),
                    });
                    return true;
                }

                return false;

            case "refreshRequested":
                if (element is RefreshContainer refresh)
                {
                    refresh.RefreshRequested += (_, _) => Post();
                    return true;
                }

                return false;

            case "modeChanged":
                if (element is TwoPaneView twoPane)
                {
                    twoPane.ModeChanged += (_, _) => Post(new JsonObject
                    {
                        ["mode"] = twoPane.Mode.ToString(),
                    });
                    return true;
                }

                return false;

            default:
                return false;
        }
    }

    /// <summary>扩展控件的 change 事件；原有控件继续走 EventBridge.BindChange。</summary>
    public static bool TryBindChange(FrameworkElement element, Action<string, JsonObject?> raise)
    {
        void Change(JsonObject data) => raise("change", data);

        switch (element)
        {
            case NumberBox number:
                number.ValueChanged += (_, _) => Change(new JsonObject { ["value"] = number.Value });
                return true;
            case RatingControl rating:
                rating.ValueChanged += (_, _) => Change(new JsonObject { ["value"] = rating.Value });
                return true;
            case ColorPicker picker:
                picker.ColorChanged += (_, _) => Change(new JsonObject { ["color"] = ColorText(picker.Color) });
                return true;
            case AutoSuggestBox suggest:
                suggest.TextChanged += (_, _) => Change(new JsonObject { ["text"] = suggest.Text });
                return true;
            case DatePicker date:
                date.SelectedDateChanged += (_, _) => Change(new JsonObject
                {
                    ["date"] = date.SelectedDate?.ToString("yyyy-MM-dd") ?? "",
                });
                return true;
            case TimePicker time:
                time.SelectedTimeChanged += (_, _) => Change(new JsonObject
                {
                    ["time"] = time.SelectedTime?.ToString(@"hh\:mm") ?? "",
                });
                return true;
            case CalendarDatePicker calendar:
                calendar.DateChanged += (_, _) => Change(new JsonObject
                {
                    ["date"] = calendar.Date?.ToString("yyyy-MM-dd") ?? "",
                });
                return true;
            case CalendarView calendarView:
                calendarView.SelectedDatesChanged += (_, _) => Change(new JsonObject
                {
                    ["date"] = calendarView.SelectedDates.Count > 0
                        ? calendarView.SelectedDates[0].ToString("yyyy-MM-dd")
                        : "",
                });
                return true;
            case Expander expander:
                expander.Expanding += (_, _) => Change(new JsonObject { ["expanded"] = true });
                expander.Collapsed += (_, _) => Change(new JsonObject { ["expanded"] = false });
                return true;
            case InfoBar info:
                info.CloseButtonClick += (_, _) => Change(new JsonObject { ["isOpen"] = false });
                return true;
            case Selector selector when selector is not ComboBox and not ListView:
                selector.SelectionChanged += (_, _) => Change(new JsonObject
                {
                    ["index"] = selector.SelectedIndex,
                    ["value"] = ItemText(selector.SelectedItem),
                });
                return true;
            case Pivot pivot:
                pivot.SelectionChanged += (_, _) => Change(new JsonObject
                {
                    ["index"] = pivot.SelectedIndex,
                    ["value"] = (pivot.SelectedItem as PivotItem)?.Header?.ToString() ?? "",
                });
                return true;
            case RadioButtons radios:
                radios.SelectionChanged += (_, _) => Change(new JsonObject
                {
                    ["index"] = radios.SelectedIndex,
                    ["value"] = ItemText(radios.SelectedItem),
                });
                return true;
            case NavigationView nav:
                nav.SelectionChanged += (_, _) => Change(new JsonObject
                {
                    ["value"] = (nav.SelectedItem as NavigationViewItem)?.Content?.ToString() ?? "",
                });
                return true;
            case TreeView tree:
                tree.ItemInvoked += (_, e) => Change(new JsonObject
                {
                    ["value"] = (e.InvokedItem as TreeViewNode)?.Content?.ToString() ?? "",
                });
                return true;
            default:
                return false;
        }
    }

    /// <summary>扩展控件的选择变化事件。</summary>
    public static bool TryBindSelectionChanged(FrameworkElement element, Action<string, JsonObject?> raise)
    {
        void Changed(int index, object? item) => raise("selectionChanged", new JsonObject
        {
            ["index"] = index,
            ["value"] = ItemText(item),
        });

        switch (element)
        {
            case Selector selector when selector is not ComboBox and not ListView:
                selector.SelectionChanged += (_, _) => Changed(selector.SelectedIndex, selector.SelectedItem);
                return true;
            case Pivot pivot:
                pivot.SelectionChanged += (_, _) =>
                    Changed(pivot.SelectedIndex, (pivot.SelectedItem as PivotItem)?.Header);
                return true;
            case NavigationView nav:
                nav.SelectionChanged += (_, _) =>
                    Changed(-1, (nav.SelectedItem as NavigationViewItem)?.Content);
                return true;
            case RadioButtons radios:
                radios.SelectionChanged += (_, _) => Changed(radios.SelectedIndex, radios.SelectedItem);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// MenuBar / CommandBar 的条目是在 items 属性里动态造出来的，不是协议节点，
    /// 所以它们的点击只能挂到父控件上，用 index + value 区分点的是哪一项。
    /// </summary>
    public static bool TryBindClick(FrameworkElement element, Action<string, JsonObject?> raise)
    {
        switch (element)
        {
            case MenuBar menuBar:
                for (var i = 0; i < menuBar.Items.Count; i++)
                {
                    var index = i;
                    var item = menuBar.Items[i];
                    foreach (var flyoutItem in item.Items)
                    {
                        if (flyoutItem is MenuFlyoutItem clickable)
                        {
                            clickable.Click += (_, _) => raise("click", new JsonObject
                            {
                                ["index"] = index,
                                ["value"] = item.Title,
                            });
                        }
                    }
                }

                return true;

            case CommandBar commandBar:
                for (var i = 0; i < commandBar.PrimaryCommands.Count; i++)
                {
                    var index = i;
                    if (commandBar.PrimaryCommands[i] is AppBarButton button)
                    {
                        button.Click += (_, _) => raise("click", new JsonObject
                        {
                            ["index"] = index,
                            ["value"] = button.Label,
                        });
                    }
                }

                return true;

            case ToggleSplitButton toggleSplit:
                toggleSplit.Click += (_, _) => raise("click", new JsonObject());
                return true;

            case RatingControl rating:
                rating.ValueChanged += (_, _) => raise("click", new JsonObject { ["value"] = rating.Value });
                return true;

            default:
                return false;
        }
    }

    // ------------------------------------------------------------------
    // 负载辅助
    // ------------------------------------------------------------------

    private static JsonObject KeyPayload(KeyRoutedEventArgs e)
    {
        static bool Down(VirtualKey key) =>
            (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
             & CoreVirtualKeyStates.Down) != 0;

        return new JsonObject
        {
            ["key"] = e.Key.ToString(),
            ["keyCode"] = (int)e.Key,
            ["ctrl"] = Down(VirtualKey.Control),
            ["shift"] = Down(VirtualKey.Shift),
            ["alt"] = Down(VirtualKey.Menu),
            ["windows"] = Down(VirtualKey.LeftWindows),
        };
    }

    private static JsonObject PositionPayload(FrameworkElement element, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(element);
        return new JsonObject
        {
            ["x"] = point.Position.X,
            ["y"] = point.Position.Y,
        };
    }

    private static JsonObject TappedPayload(FrameworkElement element, RoutedEventArgs e) =>
        e is RightTappedRoutedEventArgs right
            ? new JsonObject { ["x"] = right.GetPosition(element).X, ["y"] = right.GetPosition(element).Y }
            : e is TappedRoutedEventArgs tapped
                ? new JsonObject { ["x"] = tapped.GetPosition(element).X, ["y"] = tapped.GetPosition(element).Y }
                : e is DoubleTappedRoutedEventArgs doubled
                    ? new JsonObject { ["x"] = doubled.GetPosition(element).X, ["y"] = doubled.GetPosition(element).Y }
                    : new JsonObject();

    private static bool BindDragDrop(FrameworkElement element, string eventName, Action<JsonObject?> post)
    {
        switch (eventName)
        {
            case "dragEnter":
                element.DragEnter += (_, _) => post(new JsonObject { ["accepted"] = element.AllowDrop });
                return true;
            case "dragOver":
                element.DragOver += (_, _) => post(new JsonObject { ["accepted"] = element.AllowDrop });
                return true;
            case "dragLeave":
                element.DragLeave += (_, _) => post(null);
                return true;
            case "drop":
                element.Drop += (_, _) => post(new JsonObject { ["accepted"] = true });
                return true;
            default:
                return false;
        }
    }

    private static string? ItemText(object? item) => item switch
    {
        null => null,
        string text => text,
        ContentControl content => content.Content as string ?? content.Tag as string,
        FrameworkElement fe => fe.Tag as string,
        _ => item.ToString(),
    };

    private static string ColorText(Windows.UI.Color color) =>
        $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
}
