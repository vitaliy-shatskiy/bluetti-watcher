using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// A number out of a JSON node whatever made it: a parsed value converts to any numeric type, but a value built in code
/// (<c>JsonValue.Create(5u)</c>) answers only to its own type.
/// </summary>
internal static class JsonNumber
{
    public static bool TryDouble(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue v) return false;
        if (v.TryGetValue(out double d)) value = d;
        else if (v.TryGetValue(out long l)) value = l;
        else if (v.TryGetValue(out int i)) value = i;
        else if (v.TryGetValue(out uint u)) value = u;
        else if (v.TryGetValue(out float f)) value = f;
        else if (v.TryGetValue(out decimal m)) value = (double)m;
        else return false;
        return double.IsFinite(value);
    }

    /// <summary>A whole number in <paramref name="min"/>..<paramref name="max"/>.</summary>
    public static bool TryWhole(JsonNode? node, long min, long max, out long value)
    {
        value = 0;
        if (!TryDouble(node, out double d) || d != Math.Floor(d) || d < min || d > max) return false;
        value = (long)d;
        return true;
    }

    public static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}
