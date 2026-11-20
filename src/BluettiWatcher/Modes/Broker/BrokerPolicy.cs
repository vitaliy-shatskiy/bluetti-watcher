using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BluettiWatcher.Modes.Broker;

/// <summary>
/// What the broker may touch, written only by an elevated step (install, <c>--broker-allow</c>): the user whose requests it
/// takes, the services it may start and stop, the .nip profiles (file names inside the profiles folder) and the NVIDIA
/// Profile Inspector exe. Parsed strictly: an unknown key, a wrong type or a malformed name makes the whole file invalid,
/// and an invalid policy refuses everything.
/// </summary>
internal sealed partial record BrokerPolicy(string UserSid, IReadOnlyList<string> Services, IReadOnlyList<string> Tasks, IReadOnlyList<string> Profiles, string ProfileInspector)
{
    private static readonly HashSet<string> Keys = new(["version", "userSid", "services", "tasks", "profiles", "profileInspector"], StringComparer.Ordinal);

    /// <summary>The match time-out of every broker pattern: the input comes from files the user writes.</summary>
    internal const int MatchTimeoutMs = 200;

    /// <summary><paramref name="input"/> matches; a match that runs out of time is no match, so the request or policy is refused.</summary>
    internal static bool Matches(Regex pattern, string input)
    {
        try { return pattern.IsMatch(input); }
        catch (RegexMatchTimeoutException) { return false; }   // refused: never a pass on input that defeats the check
    }

    [GeneratedRegex(@"^S-1-5(-[0-9]{1,10}){1,14}$", RegexOptions.CultureInvariant, BrokerPolicy.MatchTimeoutMs)]
    internal static partial Regex SidPattern();

    /// <summary>A service key name: letters, digits, '_', '.', '-'; no space, no path, no wildcard.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.\-]{0,79}$", RegexOptions.CultureInvariant, BrokerPolicy.MatchTimeoutMs)]
    internal static partial Regex ServiceNamePattern();

    /// <summary>A profile file name in the profiles folder: no folder part, no leading dot, ends in .nip.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_\-][A-Za-z0-9 _.\-]{0,95}\.nip$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, BrokerPolicy.MatchTimeoutMs)]
    internal static partial Regex ProfileNamePattern();

    /// <summary>
    /// A scheduled task's full name: folders and name of letters, digits, space, '_', '.', '-', separated by '\'; no '..'
    /// segment, no wildcard, no quote.
    /// </summary>
    [GeneratedRegex(@"^\\?(?:(?!\.\.?\\)[A-Za-z0-9_\-.][A-Za-z0-9 _.\-]{0,99}\\){0,8}(?!\.\.?$)[A-Za-z0-9_\-.][A-Za-z0-9 _.\-]{0,199}$", RegexOptions.CultureInvariant, BrokerPolicy.MatchTimeoutMs)]
    internal static partial Regex TaskNamePattern();

    public bool AllowsService(string name) => Services.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The policy's own spelling of a task name (a leading '\' optional on both sides) · null = not listed.</summary>
    public string? ListedTask(string name) =>
        Tasks.FirstOrDefault(t => string.Equals(t.TrimStart('\\'), name.TrimStart('\\'), StringComparison.OrdinalIgnoreCase));

    public bool AllowsProfile(string name) => Profiles.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The policy file at <paramref name="path"/>; null when it is missing, unreadable or invalid.</summary>
    public static BrokerPolicy? Read(string path)
    {
        try { return File.Exists(path) ? Parse(File.ReadAllText(path), out _) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }   // no policy known: nothing is warned about
    }

    /// <summary>The policy in <paramref name="text"/>, or null with <paramref name="error"/> naming the first problem.</summary>
    public static BrokerPolicy? Parse(string text, out string error)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(text); }
        catch (JsonException e) { error = $"the policy is not JSON: {e.Message}"; return null; }
        if (node is not JsonObject o) { error = "the policy is not a JSON object"; return null; }
        var unknown = o.Select(p => p.Key).FirstOrDefault(k => !Keys.Contains(k));
        if (unknown is not null) { error = $"the policy has an unknown key '{unknown}'"; return null; }
        if (o["version"] is not JsonValue v || !v.TryGetValue(out int version) || version != 1) { error = "the policy version is not 1"; return null; }
        if (o["userSid"] is not JsonValue s || !s.TryGetValue(out string? sid) || !BrokerPolicy.Matches(SidPattern(), sid)) { error = "the policy userSid is not a SID"; return null; }
        var services = Names(o["services"], ServiceNamePattern(), "services", out error);
        if (services is null) return null;
        var tasks = Names(o["tasks"], TaskNamePattern(), "tasks", out error);
        if (tasks is null) return null;
        var profiles = Names(o["profiles"], ProfileNamePattern(), "profiles", out error);
        if (profiles is null) return null;
        string inspector = "";
        if (o["profileInspector"] is { } pi)
        {
            if (pi is not JsonValue pv || !pv.TryGetValue(out string? path) || (path.Length > 0 && !Path.IsPathFullyQualified(path)))
            { error = "the policy profileInspector is not an absolute path"; return null; }
            inspector = path;
        }
        error = "";
        return new BrokerPolicy(sid, services, tasks, profiles, inspector);
    }

    private static List<string>? Names(JsonNode? node, Regex pattern, string key, out string error)
    {
        error = "";
        if (node is null) return [];
        if (node is not JsonArray a) { error = $"the policy {key} is not a list"; return null; }
        var list = new List<string>();
        foreach (var item in a)
        {
            if (item is not JsonValue iv || !iv.TryGetValue(out string? name) || !Matches(pattern, name)) { error = $"the policy {key} holds a name that is not allowed: {item?.ToJsonString()}"; return null; }
            list.Add(name);
        }
        return list;
    }

    /// <summary>The policy as its file holds it (indented, the lists in the order they were allowed).</summary>
    public string ToJson()
    {
        var o = new JsonObject
        {
            ["version"] = 1,
            ["userSid"] = UserSid,
            ["services"] = new JsonArray([.. Services.Select(s => (JsonNode?)JsonValue.Create(s))]),
            ["tasks"] = new JsonArray([.. Tasks.Select(s => (JsonNode?)JsonValue.Create(s))]),
            ["profiles"] = new JsonArray([.. Profiles.Select(s => (JsonNode?)JsonValue.Create(s))]),
            ["profileInspector"] = ProfileInspector,
        };
        return o.ToJsonString(new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = ModesJson.Default }) + "\n";
    }
}
