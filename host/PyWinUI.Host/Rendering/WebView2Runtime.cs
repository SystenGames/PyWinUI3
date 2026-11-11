using Microsoft.Win32;

namespace PyWinUI.Host.Rendering;

/// <summary>
/// WebView2 Runtime 探测。
///
/// 为什么必须在建树阶段拦下来：WebView2 控件本身能构造，但一旦开始初始化
/// （EnsureCoreWebView2Async）而机器上没有 Evergreen Runtime，控件会抛异常，
/// 那块区域直接变白，甚至拖累整个窗口。与其等它炸，不如先探测，
/// 用灰色占位把这个位置顶上。
///
/// 探测只查注册表和文件系统，不加载 WebView2 的任何 Core 类型，
/// 所以即便运行时彻底缺失，这段代码自己也能安全跑完。
///
/// 设环境变量 <c>PYWINUI3_WEBVIEW2_FALLBACK=1</c> 可强制走降级分支（演示 / 测试用）。
/// 结果缓存一次：同一个宿主进程里系统运行时状态不会变，没必要每个节点查一遍注册表。
/// </summary>
internal static class WebView2Runtime
{
    internal const string ForceFallbackEnv = "PYWINUI3_WEBVIEW2_FALLBACK";

    // Evergreen Runtime 的 EdgeUpdate 客户端 GUID
    private const string ClientSubKey =
        @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    private static readonly Lazy<bool> Available = new(Detect);

    public static bool IsAvailable => Available.Value;

    public static bool ForceFallback =>
        string.Equals(Environment.GetEnvironmentVariable(ForceFallbackEnv), "1", StringComparison.Ordinal);

    private static bool Detect()
    {
        if (ForceFallback)
        {
            HostLog.Write("[webview2] fallback forced by " + ForceFallbackEnv);
            return false;
        }

        if (HasEvergreenRegistration())
        {
            HostLog.Write("[webview2] runtime found (EdgeUpdate registration)");
            return true;
        }

        if (HasRuntimeDirectory())
        {
            HostLog.Write("[webview2] runtime found (application directory)");
            return true;
        }

        HostLog.Write("[webview2] runtime NOT found, fallback view will be used");
        return false;
    }

    /// <summary>
    /// 32/64 两个注册表视图、HKLM/HKCU 两个 hive 都试一遍。
    /// Evergreen Runtime 通常落在 HKLM 的 WOW6432Node 下，
    /// 但按用户安装时会写 HKCU，只看一处会漏判。
    /// </summary>
    private static bool HasEvergreenRegistration()
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var key = root.OpenSubKey(ClientSubKey);
                    if (key?.GetValue("pv") is string version
                        && !string.IsNullOrWhiteSpace(version)
                        && version != "0.0.0.0")
                    {
                        return true;
                    }
                }
                catch
                {
                    // 某个视图不存在或没权限，继续试下一个
                }
            }
        }

        return false;
    }

    /// <summary>固定版本（Fixed Version）运行时不写 EdgeUpdate 注册表，再扫一眼常见安装目录。</summary>
    private static bool HasRuntimeDirectory()
    {
        var candidates = new List<string>();
        var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrEmpty(programFilesX86))
        {
            candidates.Add(programFilesX86);
        }

        if (!string.IsNullOrEmpty(localAppData))
        {
            candidates.Add(localAppData);
        }

        foreach (var rootPath in candidates)
        {
            try
            {
                var appDir = Path.Combine(rootPath, "Microsoft", "EdgeWebView", "Application");
                if (!Directory.Exists(appDir))
                {
                    continue;
                }

                foreach (var versionDir in Directory.EnumerateDirectories(appDir))
                {
                    if (File.Exists(Path.Combine(versionDir, "msedgewebview2.exe")))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // 目录不可读就当作没有
            }
        }

        return false;
    }
}
