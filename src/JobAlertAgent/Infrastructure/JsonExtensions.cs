using System.Text.Json;

namespace JobAlertAgent.Infrastructure;

internal static class JsonExtensions
{
    /// <summary>Returns the named property or null when absent / JSON null / not an object.</summary>
    public static JsonElement? Prop(this JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object
        && el.TryGetProperty(name, out var v)
        && v.ValueKind != JsonValueKind.Null
        && v.ValueKind != JsonValueKind.Undefined
            ? v
            : null;

    /// <summary>String value of a property; numbers are returned in their raw form. Null when absent.</summary>
    public static string? Str(this JsonElement el, string name)
    {
        var p = el.Prop(name);
        if (p is null) return null;
        return p.Value.ValueKind switch
        {
            JsonValueKind.String => p.Value.GetString(),
            JsonValueKind.Number => p.Value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static IEnumerable<JsonElement> Items(this JsonElement? el) =>
        el is { ValueKind: JsonValueKind.Array } arr ? arr.EnumerateArray() : Enumerable.Empty<JsonElement>();

    public static long? Long(this JsonElement el, string name)
    {
        var p = el.Prop(name);
        if (p is null) return null;
        if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out var n)) return n;
        if (p.Value.ValueKind == JsonValueKind.String && long.TryParse(p.Value.GetString(), out var s)) return s;
        return null;
    }
}
