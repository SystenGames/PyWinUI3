using System.Text.Encodings.Web;
using System.Text.Json;

namespace PyWinUI.Host.Protocol;

/// <summary>
/// 协议级常量与统一的 JSON 配置。两端的序列化口径必须完全一致，
/// 所以所有 JsonSerializer 调用都必须用这里的 <see cref="Json"/>。
/// </summary>
public static class Wire
{
    /// <summary>协议版本。不兼容变更才递增。</summary>
    public const int Version = 1;

    /// <summary>宿主标识，用于 ready 消息。</summary>
    public const string HostId = "PyWinUI.Host/0.1.0";

    /// <summary>单行消息字节上限。超过即视为异常流量。</summary>
    public const int MaxLineBytes = 8 * 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        // 不把中文转义成 \uXXXX：既省带宽，也让抓到的管道日志肉眼可读
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };
}

/// <summary>
/// 协议错误。带一个错误码，对应 protocol.md 第 5 节的表格。
/// </summary>
public sealed class ProtocolException : Exception
{
    public string Code { get; }

    /// <summary>出错节点 id，可选，用于让 Python 侧精确定位。</summary>
    public string? NodeId { get; }

    public ProtocolException(string code, string message, string? nodeId = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        NodeId = nodeId;
    }

    /// <summary>该错误是否必须终止会话。</summary>
    public bool IsFatal => Code is "bad-json" or "bad-protocol" or "version-mismatch";
}
