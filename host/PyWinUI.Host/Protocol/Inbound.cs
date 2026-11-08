using System.Text.Json;
using System.Text.Json.Nodes;

namespace PyWinUI.Host.Protocol;

/// <summary>Python → Host 的消息。</summary>
public abstract record InboundMessage;

public sealed record HelloMessage : InboundMessage
{
    public required int Protocol { get; init; }
    public required string Client { get; init; }
    public int? Pid { get; init; }
}

public sealed record RenderMessage : InboundMessage
{
    public required long Rev { get; init; }
    public required WindowSpec Window { get; init; }
    public required string Root { get; init; }

    /// <summary>扁平节点列表，父子关系由 NodeSpec.Children 表达。</summary>
    public required List<NodeSpec> Nodes { get; init; }
}

public sealed record PatchMessage : InboundMessage
{
    public required long Rev { get; init; }

    /// <summary>按数组顺序依次应用，操作之间允许有依赖。</summary>
    public required List<PatchOp> Ops { get; init; }
}

public sealed record InvokeMessage : InboundMessage
{
    public required string Id { get; init; }
    public required string Method { get; init; }
    public JsonObject? Args { get; init; }
}

/// <summary>弹出模态对话框。主要用于调试和演示，会阻塞窗口交互。</summary>
public sealed record DialogMessage : InboundMessage
{
    public string Title { get; init; } = "提示";
    public string Body { get; init; } = "";

    /// <summary>info / warn / error</summary>
    public string? Kind { get; init; }
}

public sealed record CloseMessage : InboundMessage
{
    public int Code { get; init; }
}

/// <summary>
/// 入站消息解析。所有校验都在这里做完，渲染层拿到的就是类型化的对象。
/// </summary>
public static class Inbound
{
    public static InboundMessage Parse(string line)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            throw new ProtocolException("bad-json", "消息不是合法 JSON", inner: ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ProtocolException("bad-protocol", "消息必须是 JSON 对象");

            return RequireString(root, "op") switch
            {
                "hello" => ParseHello(root),
                "render" => ParseRender(root),
                "patch" => ParsePatch(root),
                "invoke" => ParseInvoke(root),
                "dialog" => ParseDialog(root),
                "close" => ParseClose(root),
                var op => throw new ProtocolException("bad-protocol", $"未知消息 op \"{op}\""),
            };
        }
    }

    private static HelloMessage ParseHello(JsonElement e) => new()
    {
        Protocol = (int)RequireLong(e, "protocol"),
        Client = RequireString(e, "client"),
        Pid = (int?)TryLong(e, "pid"),
    };

    private static RenderMessage ParseRender(JsonElement e)
    {
        var nodesElement = RequireArray(e, "nodes");
        var nodes = new List<NodeSpec>(nodesElement.GetArrayLength());
        foreach (var n in nodesElement.EnumerateArray())
            nodes.Add(ParseNode(n));

        return new RenderMessage
        {
            Rev = RequireLong(e, "rev"),
            Window = ParseWindow(RequireElement(e, "window")),
            Root = RequireString(e, "root"),
            Nodes = nodes,
        };
    }

    private static PatchMessage ParsePatch(JsonElement e)
    {
        var opsElement = RequireArray(e, "ops");
        var ops = new List<PatchOp>(opsElement.GetArrayLength());
        foreach (var o in opsElement.EnumerateArray())
            ops.Add(ParseOp(o));

        return new PatchMessage { Rev = RequireLong(e, "rev"), Ops = ops };
    }

    private static InvokeMessage ParseInvoke(JsonElement e) => new()
    {
        Id = RequireString(e, "id"),
        Method = RequireString(e, "method"),
        Args = e.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object
            ? ToJsonObject(a)
            : null,
    };

    private static CloseMessage ParseClose(JsonElement e) => new()
    {
        Code = (int)(TryLong(e, "code") ?? 0),
    };

    private static DialogMessage ParseDialog(JsonElement e) => new()
    {
        Title = TryString(e, "title") ?? "提示",
        Body = TryString(e, "body") ?? "",
        Kind = TryString(e, "kind"),
    };

    private static WindowSpec ParseWindow(JsonElement e)
    {
        var spec = new WindowSpec();
        spec.Title = TryString(e, "title");
        spec.Width = TryDouble(e, "width");
        spec.Height = TryDouble(e, "height");
        spec.MinWidth = TryDouble(e, "minWidth");
        spec.MinHeight = TryDouble(e, "minHeight");
        spec.Resizable = TryBool(e, "resizable");
        spec.Center = TryBool(e, "center");
        spec.X = TryDouble(e, "x");
        spec.Y = TryDouble(e, "y");
        spec.Icon = TryString(e, "icon");
        spec.AlwaysOnTop = TryBool(e, "alwaysOnTop");
        spec.Minimizable = TryBool(e, "minimizable");
        spec.Maximizable = TryBool(e, "maximizable");
        spec.Maximized = TryBool(e, "maximized");
        spec.Scroll = TryBool(e, "scroll");
        spec.Theme = TryString(e, "theme");
        spec.Backdrop = TryString(e, "backdrop");
        return spec;
    }

    private static NodeSpec ParseNode(JsonElement e)
    {
        var node = new NodeSpec
        {
            Id = RequireString(e, "id"),
            Type = RequireString(e, "type"),
        };

        if (e.TryGetProperty("props", out var props) && props.ValueKind == JsonValueKind.Object)
            node.Props = ToJsonObject(props);

        if (e.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            node.Children = new List<string>(children.GetArrayLength());
            foreach (var c in children.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.String)
                    throw new ProtocolException("bad-protocol", $"节点 {node.Id} 的 children 含非字符串项");
                node.Children.Add(c.GetString()!);
            }
        }

        if (e.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Object)
            node.Events = TryEvents(e, "events");

        return node;
    }

    private static PatchOp ParseOp(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
            throw new ProtocolException("bad-op", "patch 操作必须是 JSON 对象");

        var t = RequireString(e, "t");
        return t switch
        {
            "set" => new PatchOp
            {
                Kind = PatchOpKind.Set,
                Id = RequireString(e, "id"),
                Props = RequireObject(e, "props"),
                Events = TryEvents(e, "events"),
            },
            "insert" => new PatchOp
            {
                Kind = PatchOpKind.Insert,
                Parent = RequireString(e, "parent"),
                Index = (int)RequireLong(e, "index"),
                Node = ParseNode(RequireElement(e, "node")),
            },
            "remove" => new PatchOp
            {
                Kind = PatchOpKind.Remove,
                Id = RequireString(e, "id"),
            },
            "move" => new PatchOp
            {
                Kind = PatchOpKind.Move,
                Id = RequireString(e, "id"),
                Parent = RequireString(e, "parent"),
                Index = (int)RequireLong(e, "index"),
            },
            "window" => new PatchOp
            {
                Kind = PatchOpKind.Window,
                Props = RequireObject(e, "props"),
            },
            _ => throw new ProtocolException("bad-op", $"未知 patch 操作 \"{t}\""),
        };
    }

    // ---- 字段读取辅助 ----

    /// <summary>
    /// 把 JsonElement 转成脱离 JsonDocument 生命周期的独立 JsonObject。
    /// 直接持有 element 会在 using 结束后变成非法访问，所以必须复制。
    /// </summary>
    private static JsonObject ToJsonObject(JsonElement e) =>
        (JsonObject)JsonNode.Parse(e.GetRawText(), null, default)!;

    private static JsonElement RequireElement(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
            throw new ProtocolException("bad-protocol", $"缺少必需字段 \"{name}\"");
        return v;
    }

    private static string RequireString(JsonElement e, string name)
    {
        var v = RequireElement(e, name);
        if (v.ValueKind != JsonValueKind.String)
            throw new ProtocolException("bad-protocol", $"字段 \"{name}\" 应为字符串");
        return v.GetString()!;
    }

    private static long RequireLong(JsonElement e, string name)
    {
        var v = RequireElement(e, name);
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var n))
            throw new ProtocolException("bad-protocol", $"字段 \"{name}\" 应为整数");
        return n;
    }

    private static JsonObject RequireObject(JsonElement e, string name)
    {
        var v = RequireElement(e, name);
        if (v.ValueKind != JsonValueKind.Object)
            throw new ProtocolException("bad-protocol", $"字段 \"{name}\" 应为对象");
        return ToJsonObject(v);
    }

    private static JsonElement RequireArray(JsonElement e, string name)
    {
        var v = RequireElement(e, name);
        if (v.ValueKind != JsonValueKind.Array)
            throw new ProtocolException("bad-protocol", $"字段 \"{name}\" 应为数组");
        return v;
    }

    private static Dictionary<string, string>? TryEvents(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object)
            return null;

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in v.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String)
                throw new ProtocolException("bad-protocol", $"事件 \"{p.Name}\" 的 handler 必须是字符串");
            map[p.Name] = p.Value.GetString()!;
        }
        return map;
    }

    private static string? TryString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? TryDouble(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static long? TryLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static bool? TryBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;
}
