using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes;

/// <summary>
/// The JSON Schema subset module settings use: <c>type</c> (one or a list), <c>properties</c>, <c>required</c>,
/// <c>additionalProperties: false</c>, <c>enum</c>, <c>minimum</c>, <c>maximum</c>, <c>minLength</c>, <c>maxLength</c>,
/// <c>items</c>, <c>maxItems</c>. Any other keyword is ignored (UI hints such as <c>title</c>).
/// </summary>
internal static class SettingsSchema
{
    /// <summary>The problems of <paramref name="value"/> against <paramref name="schema"/>, each with its JSON path; empty = valid.</summary>
    public static IReadOnlyList<string> Check(JsonObject schema, JsonNode? value)
    {
        var problems = new List<string>();
        Check(schema, value, "$", problems);
        return problems;
    }

    private static void Check(JsonObject schema, JsonNode? value, string path, List<string> problems)
    {
        if (schema["type"] is { } type && !TypeMatches(type, value))
        {
            problems.Add($"{path}: expected {type.ToJsonString()}, got {KindOf(value)}");
            return;
        }
        if (schema["enum"] is JsonArray allowed && !allowed.Any(a => JsonNode.DeepEquals(a, value)))
            problems.Add($"{path}: not one of {allowed.ToJsonString()}");
        switch (value)
        {
            case JsonObject obj: CheckObject(schema, obj, path, problems); break;
            case JsonArray arr: CheckArray(schema, arr, path, problems); break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.Number: CheckNumber(schema, Num(v), path, problems); break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String: CheckString(schema, v.GetValue<string>(), path, problems); break;
            default: break;
        }
    }

    private static void CheckObject(JsonObject schema, JsonObject obj, string path, List<string> problems)
    {
        var props = schema["properties"] as JsonObject;
        if (schema["required"] is JsonArray required)
            foreach (var r in required)
                if (r?.GetValue<string>() is { } name && !obj.ContainsKey(name)) problems.Add($"{path}.{name}: required");
        bool closed = schema["additionalProperties"] is JsonValue ap && ap.GetValueKind() == JsonValueKind.False;
        foreach (var (name, child) in obj)
        {
            if (props?[name] is JsonObject sub) Check(sub, child, $"{path}.{name}", problems);
            else if (closed) problems.Add($"{path}.{name}: not allowed");
        }
    }

    private static void CheckArray(JsonObject schema, JsonArray arr, string path, List<string> problems)
    {
        if (Number(schema, "maxItems") is { } max && arr.Count > max) problems.Add($"{path}: more than {Fmt(max)} items");
        if (schema["items"] is not JsonObject items) return;
        for (int i = 0; i < arr.Count; i++) Check(items, arr[i], $"{path}[{i.ToString(CultureInfo.InvariantCulture)}]", problems);
    }

    private static void CheckNumber(JsonObject schema, double v, string path, List<string> problems)
    {
        if (Number(schema, "minimum") is { } min && v < min) problems.Add($"{path}: under {Fmt(min)}");
        if (Number(schema, "maximum") is { } max && v > max) problems.Add($"{path}: over {Fmt(max)}");
    }

    private static void CheckString(JsonObject schema, string v, string path, List<string> problems)
    {
        if (Number(schema, "minLength") is { } min && v.Length < min) problems.Add($"{path}: shorter than {Fmt(min)}");
        if (Number(schema, "maxLength") is { } max && v.Length > max) problems.Add($"{path}: longer than {Fmt(max)}");
    }

    private static double? Number(JsonObject schema, string key) =>
        schema[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? Num(v) : null;

    /// <summary>A number node as a double whatever CLR value backs it (a node built from an int refuses GetValue&lt;double&gt;).</summary>
    private static double Num(JsonValue v) => double.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static string Fmt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static bool TypeMatches(JsonNode type, JsonNode? value) => type switch
    {
        JsonArray list => list.Any(t => t is JsonValue tv && Is(tv.GetValue<string>(), value)),
        JsonValue one => Is(one.GetValue<string>(), value),
        _ => false,
    };

    private static bool Is(string type, JsonNode? value) => (type, KindOf(value)) switch
    {
        ("integer", "number") => Num(value!.AsValue()) is var d && d == Math.Floor(d),   // number kind: a value
        var (t, k) => t == k,
    };

    private static string KindOf(JsonNode? value) => value switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            _ => "null",
        },
        _ => "null",
    };
}
