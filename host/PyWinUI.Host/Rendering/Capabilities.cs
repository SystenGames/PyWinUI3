using PyWinUI.Host.Protocol;

namespace PyWinUI.Host.Rendering;

/// <summary>
/// 运行环境能力探测。
///
/// 为什么需要这个：Mica 背景、控件圆角这些效果是 Windows 11 才有的，
/// 在 Windows 10 上直接设置会静默失效甚至抛异常。与其让 Python 侧去猜，
/// 不如宿主在握手时报上去，由宿主自己按能力降级。
/// </summary>
public static class Capabilities
{
    // Mica 与圆角是 Windows 11 (build 22000) 引入的视觉特性
    private const int Windows11Build = 22000;

    // Acrylic 从 Windows 10 1803 起可用
    private const int AcrylicMinBuild = 17134;

    private static CapabilitiesInfo? _cached;

    public static CapabilitiesInfo Detect()
    {
        if (_cached is not null)
        {
            return _cached;
        }

        var build = Environment.OSVersion.Version.Build;
        var mica = build >= Windows11Build;
        var acrylic = build >= AcrylicMinBuild;
        var rounded = build >= Windows11Build;

        _cached = new CapabilitiesInfo(
            Mica: mica,
            Acrylic: acrylic,
            RoundedCorners: rounded,
            Backdrop: mica ? "mica" : acrylic ? "acrylic" : "none");

        return _cached;
    }

    public static OsInfo DescribeOs()
    {
        var version = Environment.OSVersion.Version;
        return new OsInfo(version.ToString(), version.Build);
    }

    /// <summary>
    /// 把 Python 请求的 backdrop 解析成这台机器真正能用的值。
    /// 请求了做不到的效果时不报错，静默降级——界面能出来比效果重要。
    /// </summary>
    public static string ResolveBackdrop(string? requested)
    {
        var caps = Detect();
        return requested switch
        {
            null or "" or "auto" => caps.Backdrop,
            "mica" => caps.Mica ? "mica" : caps.Backdrop,
            "acrylic" => caps.Acrylic ? "acrylic" : "none",
            "none" => "none",
            _ => caps.Backdrop,
        };
    }
}
