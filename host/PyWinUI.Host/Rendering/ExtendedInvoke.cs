using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PyWinUI.Host.Rendering;

/// <summary>
/// 扩展 invoke 命令。Session 的主 switch 处理不了的都落到这里，
/// 返回 false 才是真正的「未知方法」。
/// </summary>
internal static class ExtendedInvoke
{
    public static bool TryInvoke(
        FrameworkElement? element,
        string method,
        JsonObject? args,
        string nodeId,
        Action<string> warn,
        Action<string, string, JsonObject?, bool> emit)
    {
        if (element is null)
        {
            return false;
        }

        switch (method)
        {
            // ---------------- 文本选区 ----------------
            case "select_range":
                if (element is TextBox rangeBox)
                {
                    var start = ValueParsers.AsInt(args?["start"]) ?? 0;
                    var length = ValueParsers.AsInt(args?["length"]) ?? 0;
                    start = Math.Clamp(start, 0, rangeBox.Text.Length);
                    length = Math.Clamp(length, 0, rangeBox.Text.Length - start);
                    rangeBox.Select(start, length);
                }
                return true;

            case "clear":
                switch (element)
                {
                    case TextBox clearBox: clearBox.Text = ""; break;
                    case PasswordBox clearPwd: clearPwd.Password = ""; break;
                    case AutoSuggestBox clearSuggest: clearSuggest.Text = ""; break;
                    case RichEditBox clearRich:
                        clearRich.Document.SetText(Microsoft.UI.Text.TextSetOptions.None, "");
                        break;
                }
                return true;

            case "copy":
                if (element is TextBox copyBox) copyBox.CopySelectionToClipboard();
                return true;
            case "cut":
                if (element is TextBox cutBox) cutBox.CutSelectionToClipboard();
                return true;
            case "paste":
                if (element is TextBox pasteBox) pasteBox.PasteFromClipboard();
                return true;

            // ---------------- 显示 / 隐藏 ----------------
            case "show":
            case "hide":
                var visible = method == "show";
                switch (element)
                {
                    case InfoBar info: info.IsOpen = visible; break;
                    case Expander expander: expander.IsExpanded = visible; break;
                    case NavigationView nav: nav.IsPaneOpen = visible; break;
                    case ContentDialog dialog: if (!visible) dialog.Hide(); break;
                }
                return true;

            // ---------------- 滚动 ----------------
            case "scroll_into_view":
                var index = ValueParsers.AsInt(args?["index"]) ?? 0;
                if (element is ItemsControl items && index >= 0 && index < items.Items.Count)
                {
                    (element as ListViewBase)?.ScrollIntoView(items.Items[index]);
                }
                return true;

            // ---------------- WebView2 脚本 ----------------
            // 脚本是异步的，且协议没有「命令返回值」，所以结果用事件回推。
            case "execute_script":
                if (element is WebView2 web && ValueParsers.AsString(args?["script"]) is { } script)
                {
                    _ = RunScriptAsync(web, script, nodeId, emit);
                }
                return true;

            default:
                return false;
        }
    }

    private static async Task RunScriptAsync(
        WebView2 web,
        string script,
        string nodeId,
        Action<string, string, JsonObject?, bool> emit)
    {
        try
        {
            if (web.CoreWebView2 is null)
            {
                await web.EnsureCoreWebView2Async();
            }

            var json = await web.CoreWebView2!.ExecuteScriptAsync(script);
            emit(nodeId, "scriptResult", new JsonObject { ["result"] = json }, false);
        }
        catch (Exception ex)
        {
            emit(nodeId, "scriptResult", new JsonObject
            {
                ["error"] = ex.Message,
            }, false);
        }
    }
}
