using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// The per-monitor settings both display modules share: <c>{ "monitors": [ { "monitor": id, "&lt;field&gt;": n } ] }</c>,
/// and the <c>monitor/{id}/{field}</c> resource keys they expand to.
/// </summary>
internal static class MonitorTargets
{
    public const string Prefix = "monitor/";

    /// <summary>The (monitor id, value) pairs; entries the schema would refuse are skipped (Validate reports them).</summary>
    public static IReadOnlyList<(string Monitor, int Value)> Read(JsonObject settings, string field)
    {
        var list = new List<(string, int)>();
        if (settings["monitors"] is not JsonArray items) return list;
        foreach (var item in items)
        {
            if (item is not JsonObject o || o["monitor"] is not JsonValue m || m.GetValueKind() != JsonValueKind.String) continue;
            if (Number(o[field]) is not { } v || v != Math.Floor(v)) continue;
            list.Add((m.GetValue<string>(), (int)v));
        }
        return list;
    }

    /// <summary>What the schema cannot say: a usable id and one entry per monitor (two would fight over one resource).</summary>
    public static IReadOnlyList<string> Validate(JsonObject settings, string field)
    {
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (monitor, _) in Read(settings, field))
        {
            if (monitor.Length == 0 || monitor.Contains('/', StringComparison.Ordinal)) errors.Add($"monitor id \"{monitor}\" is not a monitor from the list");
            else if (!seen.Add(monitor)) errors.Add($"monitor \"{monitor}\" is listed twice");
        }
        return errors;
    }

    /// <summary>One intent per monitor: key <c>monitor/{id}/{keyField}</c>, target the entry's <paramref name="field"/>.</summary>
    public static IReadOnlyList<ResourceIntent> Expand(JsonObject settings, string field, string keyField) =>
        [.. Read(settings, field).Select(t => new ResourceIntent(Key(t.Monitor, keyField), JsonValue.Create(t.Value)))];

    public static string Key(string monitor, string field) => $"{Prefix}{monitor}/{field}";

    /// <summary>The monitor id of <paramref name="key"/>, or null when the key is not this module's.</summary>
    public static string? MonitorOf(string key, string field)
    {
        string suffix = "/" + field;
        return key.StartsWith(Prefix, StringComparison.Ordinal) && key.EndsWith(suffix, StringComparison.Ordinal) && key.Length > Prefix.Length + suffix.Length
            ? key[Prefix.Length..^suffix.Length]
            : null;
    }

    /// <summary>A JSON number however it was built (parsed or created), else null.</summary>
    public static double? Number(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number
            ? double.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture)
            : null;
}
