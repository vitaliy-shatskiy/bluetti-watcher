using System.Text.Json;
using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// Tolerant reads of module settings and resource values: a field of the wrong JSON kind reads as absent, so a module's
/// Validate reports it and Expand never throws on settings the schema check has not seen yet.
/// </summary>
internal static class JsonFields
{
    public static string? Str(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    public static bool? Bool(JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    public static int? Int(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue(out int i) ? i : null;

    public static JsonArray Arr(JsonNode? node) => node as JsonArray ?? [];

    public static IEnumerable<JsonObject> Objects(JsonNode? node) => Arr(node).OfType<JsonObject>();

    /// <summary>The strings of an array; non-string items are skipped (Validate names them).</summary>
    public static IReadOnlyList<string> Strings(JsonNode? node) => [.. Arr(node).Select(Str).OfType<string>()];

    /// <summary>
    /// The name inside a resource key <c>{prefix}{name}{suffix}</c>, or null when the key has another shape. Names hold
    /// any character but '/' (Validate refuses it), so the split is unambiguous.
    /// </summary>
    public static string? KeyName(string key, string prefix, string suffix)
    {
        if (!key.StartsWith(prefix, StringComparison.Ordinal) || !key.EndsWith(suffix, StringComparison.Ordinal)) return null;
        if (key.Length <= prefix.Length + suffix.Length) return null;
        string name = key[prefix.Length..^suffix.Length];
        return name.Contains('/', StringComparison.Ordinal) ? null : name;
    }

    /// <summary>Problems with a list of names: empty, holding '/', or repeated (case-insensitive, as Windows compares them).</summary>
    public static void CheckNames(IEnumerable<string?> names, string what, List<string> problems)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) { problems.Add($"a {what} has no name"); continue; }
            if (name.Contains('/', StringComparison.Ordinal)) problems.Add($"{what} '{name}': a name cannot contain '/'");
            if (!seen.Add(name)) problems.Add($"{what} '{name}' is listed twice");
        }
    }

    /// <summary>A JSON object from a constant text (schemas, defaults); a fresh copy each call, so no caller shares a mutable node.</summary>
    public static JsonObject Parse(string json) => JsonNode.Parse(json)?.AsObject() ?? throw new ArgumentException("not a JSON object", nameof(json));
}
