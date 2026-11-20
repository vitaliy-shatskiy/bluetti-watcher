using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BluettiWatcher.Tests.Modes;

/// <summary>
/// The JSON Schema subset the module descriptors use (type, properties, required, enum, minimum, maximum, items,
/// additionalProperties): the problems of <c>value</c> against <c>schema</c>, empty = valid.
/// </summary>
internal static class SchemaCheck
{
    public static IReadOnlyList<string> Problems(JsonObject schema, JsonNode? value)
    {
        var problems = new List<string>();
        Check(schema, value, "$", problems);
        return problems;
    }

    private static void Check(JsonObject schema, JsonNode? value, string at, List<string> problems)
    {
        if (schema["type"] is JsonValue t && !TypeMatches(t.GetValue<string>(), value))
        {
            problems.Add($"{at}: not a {t.GetValue<string>()}");
            return;
        }
        if (schema["enum"] is JsonArray options && !options.Any(o => JsonNode.DeepEquals(o, value)))
            problems.Add($"{at}: not one of the enum values");
        if (value is JsonValue v && v.GetValueKind() == JsonValueKind.Number)
        {
            double n = double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture);
            if (schema["minimum"] is JsonValue min && n < min.GetValue<double>()) problems.Add($"{at}: below the minimum");
            if (schema["maximum"] is JsonValue max && n > max.GetValue<double>()) problems.Add($"{at}: above the maximum");
        }
        if (value is JsonObject o)
        {
            var props = schema["properties"] as JsonObject ?? [];
            if (schema["required"] is JsonArray required)
                foreach (var r in required)
                    if (!o.ContainsKey(r!.GetValue<string>())) problems.Add($"{at}: {r} is required");
            foreach (var (name, child) in o)
            {
                if (props[name] is JsonObject childSchema) Check(childSchema, child, $"{at}.{name}", problems);
                else if (schema["additionalProperties"] is JsonValue extra && extra.GetValueKind() == JsonValueKind.False) problems.Add($"{at}.{name}: not allowed");
            }
        }
        if (value is JsonArray a && schema["items"] is JsonObject items)
            for (int i = 0; i < a.Count; i++) Check(items, a[i], $"{at}[{i}]", problems);
    }

    private static bool TypeMatches(string type, JsonNode? value) => type switch
    {
        "object" => value is JsonObject,
        "array" => value is JsonArray,
        "string" => value is JsonValue v && v.GetValueKind() == JsonValueKind.String,
        "boolean" => value is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "number" => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number,
        "integer" => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number
            && double.Parse(v.ToJsonString(), CultureInfo.InvariantCulture) is var n && n == Math.Floor(n),
        _ => false,
    };
}
