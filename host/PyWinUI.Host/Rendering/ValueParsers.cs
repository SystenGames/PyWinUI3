using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;

namespace PyWinUI.Host.Rendering;

/// <summary>
/// 把协议里的 JSON 值翻译成 WinUI 类型。
///
/// 原则：**解析失败不抛异常**，返回 null 让调用方决定用默认值。
/// 一个属性写错不该让整个界面消失，用户应该看到其余部分正常渲染 + 一条错误日志。
/// </summary>
internal static class ValueParsers
{
    // ------------------------------------------------------------------
    // 基础标量
    // ------------------------------------------------------------------

    public static double? AsDouble(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<double>(out var d))
        {
            return d;
        }

        // 允许 "12" 这种字符串形式的数字，容错优先
        if (value.TryGetValue<string>(out var s)
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    public static int? AsInt(JsonNode? node)
    {
        var d = AsDouble(node);
        return d is null ? null : (int)Math.Round(d.Value);
    }

    public static bool? AsBool(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<bool>(out var b))
        {
            return b;
        }

        if (value.TryGetValue<string>(out var s))
        {
            return s.ToLowerInvariant() switch
            {
                "true" or "1" or "yes" => true,
                "false" or "0" or "no" => false,
                _ => null,
            };
        }

        return null;
    }

    public static string? AsString(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var s))
        {
            return s;
        }

        return null;
    }

    public static List<string>? AsStringList(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return null;
        }

        var list = new List<string>(array.Count);
        foreach (var item in array)
        {
            list.Add(AsString(item) ?? string.Empty);
        }

        return list;
    }

    // ------------------------------------------------------------------
    // 结构
    // ------------------------------------------------------------------

    /// <summary>
    /// 外边距 / 内边距。接受三种写法：
    /// <c>12</c>（四边）、<c>[l, t, r, b]</c>、<c>{l, t, r, b}</c>。
    /// </summary>
    public static Thickness? AsThickness(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;

            case JsonValue value when value.TryGetValue<double>(out var uniform):
                return new Thickness(uniform, uniform, uniform, uniform);

            case JsonArray array:
            {
                var values = new double[array.Count];
                for (var i = 0; i < array.Count; i++)
                {
                    values[i] = AsDouble(array[i]) ?? 0;
                }

                return values.Length switch
                {
                    1 => new Thickness(values[0]),
                    2 => new Thickness(values[0], values[1], values[0], values[1]),
                    4 => new Thickness(values[0], values[1], values[2], values[3]),
                    _ => null,
                };
            }

            case JsonObject obj:
            {
                double Read(params string[] keys)
                {
                    foreach (var key in keys)
                    {
                        if (obj.TryGetPropertyValue(key, out var item) && AsDouble(item) is { } d)
                        {
                            return d;
                        }
                    }

                    return 0;
                }

                return new Thickness(Read("left", "l"), Read("top", "t"), Read("right", "r"), Read("bottom", "b"));
            }

            default:
                return null;
        }
    }

    /// <summary>圆角。接受 <c>4</c> 或 <c>[tl, tr, br, bl]</c>。</summary>
    public static CornerRadius? AsCornerRadius(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;

            case JsonValue value when value.TryGetValue<double>(out var uniform):
                return new CornerRadius(uniform);

            case JsonArray array:
            {
                var values = new double[array.Count];
                for (var i = 0; i < array.Count; i++)
                {
                    values[i] = AsDouble(array[i]) ?? 0;
                }

                return values.Length switch
                {
                    1 => new CornerRadius(values[0]),
                    4 => new CornerRadius(values[0], values[1], values[2], values[3]),
                    _ => null,
                };
            }

            default:
                return null;
        }
    }

    // ------------------------------------------------------------------
    // 颜色
    // ------------------------------------------------------------------

    private static readonly Dictionary<string, Color> NamedColors = BuildNamedColorTable();

    private static Dictionary<string, Color> BuildNamedColorTable()
    {
        var map = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // 借用 WinUI 自带的 140+ 具名颜色，不手写色表
            foreach (var property in typeof(Microsoft.UI.Colors).GetProperties(
                         BindingFlags.Public | BindingFlags.Static))
            {
                if (property.PropertyType == typeof(Color)
                    && property.GetValue(null) is Color color)
                {
                    map[property.Name] = color;
                }
            }
        }
        catch
        {
            // 反射失败就只剩十六进制写法可用，不影响主流程
        }

        return map;
    }

    public static Brush? AsBrush(JsonNode? node)
    {
        var color = AsColor(node);
        return color is null ? null : new SolidColorBrush(color.Value);
    }

    public static Color? AsColor(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;

            case JsonValue value when value.TryGetValue<string>(out var text):
                return ColorFromString(text);

            case JsonObject obj:
            {
                byte Channel(string key, byte fallback)
                {
                    var d = AsDouble(obj[key]);
                    return d is null ? fallback : (byte)Math.Clamp(Math.Round(d.Value), 0, 255);
                }

                return Color.FromArgb(
                    Channel("a", 255),
                    Channel("r", 0),
                    Channel("g", 0),
                    Channel("b", 0));
            }

            default:
                return null;
        }
    }

    private static Color? ColorFromString(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Equals("accent", StringComparison.OrdinalIgnoreCase))
        {
            return AccentColor();
        }

        if (text[0] == '#')
        {
            var hex = text[1..];
            return hex.Length switch
            {
                3 => ParseHex(hex, expandShort: true, alpha: 255),
                4 => ParseHexArgb(hex, expandShort: true),
                6 => ParseHex(hex, expandShort: false, alpha: 255),
                8 => ParseHexArgb(hex, expandShort: false),
                _ => null,
            };
        }

        return NamedColors.TryGetValue(text, out var named) ? named : null;
    }

    private static Color? ParseHex(string hex, bool expandShort, byte alpha)
    {
        if (expandShort)
        {
            if (!byte.TryParse(string.Concat(hex[0], hex[0]), NumberStyles.HexNumber, null, out var r3)
                || !byte.TryParse(string.Concat(hex[1], hex[1]), NumberStyles.HexNumber, null, out var g3)
                || !byte.TryParse(string.Concat(hex[2], hex[2]), NumberStyles.HexNumber, null, out var b3))
            {
                return null;
            }

            return Color.FromArgb(alpha, r3, g3, b3);
        }

        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return null;
        }

        return Color.FromArgb(
            alpha,
            (byte)((rgb >> 16) & 0xFF),
            (byte)((rgb >> 8) & 0xFF),
            (byte)(rgb & 0xFF));
    }

    private static Color? ParseHexArgb(string hex, bool expandShort)
    {
        if (expandShort)
        {
            if (!byte.TryParse(string.Concat(hex[0], hex[0]), NumberStyles.HexNumber, null, out var a4)
                || !byte.TryParse(string.Concat(hex[1], hex[1]), NumberStyles.HexNumber, null, out var r4)
                || !byte.TryParse(string.Concat(hex[2], hex[2]), NumberStyles.HexNumber, null, out var g4)
                || !byte.TryParse(string.Concat(hex[3], hex[3]), NumberStyles.HexNumber, null, out var b4))
            {
                return null;
            }

            return Color.FromArgb(a4, r4, g4, b4);
        }

        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
        {
            return null;
        }

        return Color.FromArgb(
            (byte)((argb >> 24) & 0xFF),
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF));
    }

    private static Color? AccentColor()
    {
        try
        {
            if (Application.Current?.Resources is { } resources
                && resources.TryGetValue("SystemAccentColor", out var value)
                && value is Color color)
            {
                return color;
            }
        }
        catch
        {
        }

        return null;
    }

    // ------------------------------------------------------------------
    // 枚举
    // ------------------------------------------------------------------

    private static TEnum? AsEnum<TEnum>(JsonNode? node)
        where TEnum : struct, Enum
    {
        var text = AsString(node);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // 允许 "uniformToFill" / "uniform-to-fill" / "UNIFORMTOFILL"
        var normalized = text.Replace("-", "").Replace("_", "");
        return Enum.TryParse<TEnum>(normalized, ignoreCase: true, out var value) ? value : null;
    }

    public static HorizontalAlignment? AsHorizontalAlignment(JsonNode? node) =>
        AsEnum<HorizontalAlignment>(node);

    public static VerticalAlignment? AsVerticalAlignment(JsonNode? node) =>
        AsEnum<VerticalAlignment>(node);

    public static TextAlignment? AsTextAlignment(JsonNode? node) =>
        AsEnum<TextAlignment>(node);

    public static Orientation? AsOrientation(JsonNode? node) =>
        AsEnum<Orientation>(node);

    public static Stretch? AsStretch(JsonNode? node) =>
        AsEnum<Stretch>(node);

    /// <summary>字体粗细。WinUI 的 FontWeight 是结构不是枚举，只能查表。</summary>
    public static FontWeight? AsFontWeight(JsonNode? node)
    {
        var text = AsString(node)?.Trim().ToLowerInvariant().Replace("-", "").Replace(" ", "");
        return text switch
        {
            "thin" => FontWeights.Thin,
            "extralight" => FontWeights.ExtraLight,
            "light" => FontWeights.Light,
            "normal" or "regular" or "400" => FontWeights.Normal,
            "medium" => FontWeights.Medium,
            "semibold" => FontWeights.SemiBold,
            "bold" or "700" => FontWeights.Bold,
            "extrabold" => FontWeights.ExtraBold,
            "black" => FontWeights.Black,
            _ => null,
        };
    }
}
