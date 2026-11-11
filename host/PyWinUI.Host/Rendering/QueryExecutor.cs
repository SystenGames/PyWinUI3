using System.Text.Json.Nodes;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;

namespace PyWinUI.Host.Rendering;

/// <summary>
/// 把「读一个运行时值」的请求翻译成 WinUI 属性读取。
///
/// 协议原先只有 invoke（单向命令）和 event（异步通知），没有任何读回通道，
/// 于是 ActualWidth、窗口是否最大化、文本框选区、媒体播放位置这类
/// 「随时问一句」的能力全都做不到。query/result 就是为了补这半边。
///
/// 读取全部在 UI 线程同步执行（Session 的 Handle 已经调度过），
/// 所以这里不能 await，也不能做耗时的异步操作。
/// </summary>
internal sealed class QueryExecutor
{
    private readonly Window _window;
    private bool _active = true;

    public QueryExecutor(Window window)
    {
        _window = window;
        _window.Activated += (_, e) =>
            _active = e.WindowActivationState != WindowActivationState.Deactivated;
    }

    /// <summary>
    /// 读一个值。element 为 null 表示节点不存在，此时只有窗口级键还能读。
    /// 抛出的异常由 Session 统一转成 ok=false 的 result。
    /// </summary>
    public JsonNode? Read(FrameworkElement? element, string key, JsonObject? args)
    {
        // 窗口级键与具体控件无关，放最前面：根节点可能还没建好
        switch (key)
        {
            case "windowState":
                return PresenterState();
            case "isMaximized":
                return PresenterState() == "maximized";
            case "isMinimized":
                return PresenterState() == "minimized";
            case "isActive":
                return _active;
            case "windowBounds":
                return new JsonObject
                {
                    ["x"] = _window.AppWindow.Position.X,
                    ["y"] = _window.AppWindow.Position.Y,
                    ["width"] = _window.AppWindow.Size.Width,
                    ["height"] = _window.AppWindow.Size.Height,
                };
            case "dpiScale":
                return _window.Content?.XamlRoot?.RasterizationScale ?? 1.0;
            case "windowTitle":
                return _window.Title;
        }

        if (element is null)
        {
            throw new InvalidOperationException($"节点不存在，无法读取 \"{key}\"");
        }

        // 几何值必须先把挂起的布局跑完。控件多的时候首帧布局要好几秒，
        // 不 UpdateLayout 就会在「已经 Loaded 但还没 Measure」的窗口期读到 0。
        if (key is "actualWidth" or "actualHeight" or "bounds")
        {
            try
            {
                element.UpdateLayout();
            }
            catch (Exception ex)
            {
                HostLog.Write("[query] UpdateLayout 失败: " + ex.Message);
            }
        }

        switch (key)
        {
            // ---- 所有控件通用的几何 / 状态 ----
            case "actualWidth": return element.ActualWidth;
            case "actualHeight": return element.ActualHeight;
            case "bounds":
                return new JsonObject
                {
                    ["width"] = element.ActualWidth,
                    ["height"] = element.ActualHeight,
                    ["x"] = 0,
                    ["y"] = 0,
                };
            case "isLoaded": return element.IsLoaded;
            case "isVisible": return element.Visibility == Visibility.Visible;
            case "opacity": return element.Opacity;
            case "name": return element.Name;
            case "tag": return element.Tag as string;
            case "controlType": return element.GetType().Name;
            case "isEnabled": return element is Control c ? c.IsEnabled : true;
            case "isFocused":
                return element is Control focused
                    && focused.FocusState != FocusState.Unfocused;
            case "width": return element.Width;
            case "height": return element.Height;
            case "dpiScale":
                return element.XamlRoot?.RasterizationScale ?? 1.0;

            // ---- 文本 / 输入 ----
            case "text": return ReadText(element);
            case "password": return (element as PasswordBox)?.Password;
            case "selectionStart":
                return element is TextBox selStart ? selStart.SelectionStart : -1;
            case "selectionLength":
                return element is TextBox selLen ? selLen.SelectionLength : -1;
            case "selectedText":
                return element is TextBox selText ? selText.SelectedText : "";
            case "isSpellCheckEnabled":
                return element is TextBox spell ? spell.IsSpellCheckEnabled : false;
            case "acceptsReturn":
                return element is TextBox ret ? ret.AcceptsReturn : false;
            case "isReadOnly":
                return element is TextBox ro ? ro.IsReadOnly : false;
            case "maxLength":
                return element is TextBox maxLen ? maxLen.MaxLength : 0;

            // ---- 数值 / 状态类 ----
            case "value": return ReadValue(element);
            case "checked": return ReadChecked(element);
            case "selectedIndex": return ReadSelectedIndex(element);
            case "selectedItem": return ReadSelectedItem(element);
            case "selectedItems": return ReadSelectedItems(element);
            case "selectedCount": return ReadSelectedItems(element).Count;
            case "itemCount": return ReadItemCount(element);

            // ---- 日期 / 颜色 ----
            case "date": return ReadDate(element);
            case "time": return ReadTime(element);
            case "color": return ReadColor(element);

            // ---- 展开 / 提示类 ----
            case "isOpen":
                return (element as InfoBar)?.IsOpen ?? false;
            case "isExpanded":
                return (element as Expander)?.IsExpanded ?? false;
            case "severity":
                return (element as InfoBar)?.Severity.ToString();
            case "isPaneOpen":
                return (element as NavigationView)?.IsPaneOpen ?? false;

            // ---- WebView2 ----
            case "currentUrl":
                return element is WebView2 web
                    ? web.CoreWebView2?.Source ?? web.Source?.ToString() ?? ""
                    : null;
            case "canGoBack":
                return element is WebView2 backWeb && backWeb.CanGoBack;
            case "canGoForward":
                return element is WebView2 forwardWeb && forwardWeb.CanGoForward;
            case "webTitle":
                return element is WebView2 titled ? titled.CoreWebView2?.DocumentTitle : null;

            // ---- 媒体 ----
            case "position":
                return element is MediaPlayerElement mediaPos
                    ? mediaPos.MediaPlayer?.PlaybackSession?.Position.TotalSeconds ?? 0
                    : 0;
            case "duration":
                return element is MediaPlayerElement mediaDur
                    ? mediaDur.MediaPlayer?.PlaybackSession?.NaturalDuration.TotalSeconds ?? 0
                    : 0;
            case "isPlaying":
                return element is MediaPlayerElement mediaPlaying
                    && mediaPlaying.MediaPlayer?.PlaybackSession?.PlaybackState
                        == Windows.Media.Playback.MediaPlaybackState.Playing;
            case "volume":
                return element is MediaPlayerElement mediaVol ? mediaVol.MediaPlayer?.Volume ?? 1 : 1;
            case "isMuted":
                return element is MediaPlayerElement mediaMute && mediaMute.MediaPlayer?.IsMuted == true;
            case "playbackRate":
                return element is MediaPlayerElement mediaRate
                    ? mediaRate.MediaPlayer?.PlaybackSession?.PlaybackRate ?? 1
                    : 1;
            case "isFullWindow":
                return element is MediaPlayerElement mediaFull && mediaFull.IsFullWindow;

            // ---- 滚动 ----
            case "verticalOffset":
                return (element as ScrollViewer)?.VerticalOffset ?? 0;
            case "viewportHeight":
                return (element as ScrollViewer)?.ViewportHeight ?? 0;
            case "extentHeight":
                return (element as ScrollViewer)?.ExtentHeight ?? 0;

            // ---- 图片 ----
            case "imageSource":
                return (element as Image)?.Source is Microsoft.UI.Xaml.Media.Imaging.BitmapImage bmp
                    ? bmp.UriSource?.ToString()
                    : null;

            default:
                throw new InvalidOperationException($"未知的 query 键 \"{key}\"");
        }
    }

    private string PresenterState()
    {
        if (_window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            return presenter.State switch
            {
                OverlappedPresenterState.Maximized => "maximized",
                OverlappedPresenterState.Minimized => "minimized",
                _ => "normal",
            };
        }

        return "normal";
    }

    private static string? ReadText(FrameworkElement element) => element switch
    {
        TextBox box => box.Text,
        PasswordBox password => password.Password,
        TextBlock block => block.Text,
        AutoSuggestBox suggest => suggest.Text,
        RichTextBlock rich => string.Concat(rich.Blocks.OfType<Paragraph>()
            .SelectMany(p => p.Inlines.OfType<Run>()).Select(r => r.Text)),
        ComboBox combo => combo.SelectedItem as string ?? combo.Text,
        ContentControl content => ReadContentText(content.Content),
        _ => null,
    };

    /// <summary>带图标的按钮把文字放在 Content 里的 StackPanel 中。</summary>
    private static string? ReadContentText(object? content) => content switch
    {
        null => null,
        string text => text,
        TextBlock block => block.Text,
        Panel panel => string.Concat(panel.Children.OfType<TextBlock>().Select(t => t.Text)),
        _ => null,
    };

    private static double ReadValue(FrameworkElement element) => element switch
    {
        Slider slider => slider.Value,
        ProgressBar progress => progress.Value,
        NumberBox number => number.Value,
        RatingControl rating => rating.Value,
        MediaPlayerElement media => media.MediaPlayer?.PlaybackSession?.Position.TotalSeconds ?? 0,
        _ => 0,
    };

    private static bool ReadChecked(FrameworkElement element) => element switch
    {
        CheckBox check => check.IsChecked ?? false,
        RadioButton radio => radio.IsChecked ?? false,
        ToggleSwitch toggle => toggle.IsOn,
        ToggleButton toggleButton => toggleButton.IsChecked ?? false,
        _ => false,
    };

    private static int ReadSelectedIndex(FrameworkElement element) => element switch
    {
        ComboBox combo => combo.SelectedIndex,
        ListView list => list.SelectedIndex,
        Selector selector => selector.SelectedIndex,
        _ => -1,
    };

    private static string? ReadSelectedItem(FrameworkElement element) => element switch
    {
        ComboBox combo => ItemText(combo.SelectedItem),
        Selector selector => ItemText(selector.SelectedItem),
        TreeView tree => ItemText(tree.SelectedItem),
        NavigationView nav => (nav.SelectedItem as NavigationViewItem)?.Content as string,
        _ => null,
    };

    private static JsonArray ReadSelectedItems(FrameworkElement element)
    {
        var result = new JsonArray();
        if (element is ListView list)
        {
            foreach (var item in list.SelectedItems)
            {
                result.Add(ItemText(item));
            }
        }

        return result;
    }

    private static int ReadItemCount(FrameworkElement element) => element switch
    {
        ItemsControl items => items.Items.Count,
        NavigationView nav => nav.MenuItems.Count,
        _ => 0,
    };

    private static string? ItemText(object? item) => item switch
    {
        null => null,
        string text => text,
        ContentControl content => content.Content as string ?? content.Tag as string,
        FrameworkElement fe => fe.Tag as string,
        _ => item.ToString(),
    };

    private static string? ReadDate(FrameworkElement element) => element switch
    {
        DatePicker picker => picker.SelectedDate?.ToString("yyyy-MM-dd"),
        CalendarDatePicker calendar => calendar.Date?.ToString("yyyy-MM-dd"),
        CalendarView view => view.SelectedDates.Count > 0
            ? view.SelectedDates[0].ToString("yyyy-MM-dd")
            : null,
        _ => null,
    };

    private static string? ReadTime(FrameworkElement element) => element switch
    {
        TimePicker picker => picker.SelectedTime?.ToString(@"hh\:mm"),
        _ => null,
    };

    private static string? ReadColor(FrameworkElement element) =>
        element is ColorPicker picker ? $"#{picker.Color.A:X2}{picker.Color.R:X2}{picker.Color.G:X2}{picker.Color.B:X2}" : null;
}
