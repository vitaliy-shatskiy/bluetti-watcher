using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BluettiWatcher.Modes.Broker;

/// <summary>
/// One request file: <c>requests\&lt;id&gt;.json</c> = <c>{ "v": 1, "id", "op", "args": {}, "issued": unix ms }</c>. The id is
/// 32 lower-case hex digits and names the file; the client writes it, the broker reads and deletes it.
/// </summary>
internal sealed partial record BrokerRequest(string Id, string Operation, JsonObject Args, DateTimeOffset Issued)
{
    /// <summary>A request bigger than this is refused unread.</summary>
    public const int MaxBytes = 4096;

    private static readonly HashSet<string> Keys = new(["v", "id", "op", "args", "issued"], StringComparer.Ordinal);

    [GeneratedRegex("^([0-9a-f]{32})\\.json$", RegexOptions.CultureInvariant, BrokerPolicy.MatchTimeoutMs)]
    internal static partial Regex FileNamePattern();

    [GeneratedRegex("^[a-z]+(\\.[a-zA-Z]+)+$", RegexOptions.CultureInvariant, BrokerPolicy.MatchTimeoutMs)]
    internal static partial Regex OperationPattern();

    /// <summary>The id a request file name carries · null = not a request file (never answered, only removed).</summary>
    public static string? IdOf(string fileName)
    {
        try
        {
            var m = FileNamePattern().Match(fileName);
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (RegexMatchTimeoutException) { return null; }   // not a request file: removed unanswered
    }

    /// <summary>The request text of the file <paramref name="id"/> names, or null with the reason.</summary>
    public static BrokerRequest? Parse(string id, string text, out string error)
    {
        if (text.Length > MaxBytes) { error = "the request is too large"; return null; }
        JsonNode? node;
        try { node = JsonNode.Parse(text); }
        catch (JsonException) { error = "the request is not JSON"; return null; }
        if (node is not JsonObject o) { error = "the request is not a JSON object"; return null; }
        string? extra = o.Select(p => p.Key).FirstOrDefault(k => !Keys.Contains(k));
        if (extra is not null) { error = $"the request has an unknown key '{extra}'"; return null; }
        if (o["v"] is not JsonValue v || !v.TryGetValue(out int version) || version != 1) { error = "the request version is not 1"; return null; }
        if (o["id"] is not JsonValue i || !i.TryGetValue(out string? rid) || !string.Equals(rid, id, StringComparison.Ordinal)) { error = "the request id is not its file name"; return null; }
        if (o["op"] is not JsonValue op || !op.TryGetValue(out string? operation) || !BrokerPolicy.Matches(OperationPattern(), operation)) { error = "the request has no operation"; return null; }
        if (o["args"] is not JsonObject args) { error = "the request args is not an object"; return null; }
        if (o["issued"] is not JsonValue t || !t.TryGetValue(out long ms) || ms < 0 || ms > 253402300799999) { error = "the request has no issue time"; return null; }
        error = "";
        return new BrokerRequest(id, operation, (JsonObject)args.DeepClone(), DateTimeOffset.FromUnixTimeMilliseconds(ms));
    }

    /// <summary>The request as the client writes it.</summary>
    public string ToJson() => new JsonObject
    {
        ["v"] = 1,
        ["id"] = Id,
        ["op"] = Operation,
        ["args"] = Args.DeepClone(),
        ["issued"] = Issued.ToUnixTimeMilliseconds(),
    }.ToJsonString();

    /// <summary>A fresh id: 128 random bits as hex.</summary>
    public static string NewId() => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    internal static string Ms(DateTimeOffset at) => at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
}
