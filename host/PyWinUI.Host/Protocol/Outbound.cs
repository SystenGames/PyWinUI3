using System.Text.Json;
using System.Text.Json.Nodes;

namespace PyWinUI.Host.Protocol;

/// <summary>宿主运行时环境的回报。</summary>
public sealed record OsInfo(string Version, int Build);

/// <summary>
/// 宿主能力位。Python 侧据此决定要不要请求 Mica 这类 Win11 专属效果。
/// </summary>
public sealed record CapabilitiesInfo(bool Mica, bool Acrylic, bool RoundedCorners, string Backdrop);

/// <summary>
/// Host → Python 的消息构造。每个方法返回**单行 JSON**（不含换行符），
/// 由 Transport 层负责加分隔符并写出。
/// </summary>
public static class Outbound
{
    public static string Ready(OsInfo os, CapabilitiesInfo capabilities) => Serialize(new
    {
        t = "ready",
        protocol = Wire.Version,
        host = Wire.HostId,
        os = new { version = os.Version, build = os.Build },
        capabilities,
    });

    public static string Ack(long rev) => Serialize(new { t = "ack", rev });

    public static string Event(string id, string eventName, long rev, JsonObject? data, bool echo) => Serialize(new
    {
        t = "event",
        id,
        @event = eventName,
        rev,
        data,
        // 只在为 true 时输出，避免每条事件都带一个 false 字段
        echo = echo ? true : (bool?)null,
    });

    /// <summary>
    /// query 的应答。value 允许是任意 JSON 值（数字、字符串、布尔、数组、对象、null）。
    /// 失败时 ok=false 并带 error，Python 侧据此抛异常而不是静默返回 None。
    /// </summary>
    public static string Result(string id, bool ok, JsonNode? value, string? error = null) => Serialize(new
    {
        t = "result",
        id,
        ok,
        value,
        error = ok ? null : error,
    });

    public static string Error(string code, string message, string? id = null) => Serialize(new
    {
        t = "error",
        code,
        message,
        id,
    });

    public static string Bye(int code) => Serialize(new { t = "bye", code });

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Wire.Json);
}
