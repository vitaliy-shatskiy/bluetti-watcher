using System.Globalization;
using System.Text.Json.Nodes;
using BluettiWatcher.Modes.Broker;

namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// <c>gpu.powerLimit</c>: an NVIDIA GPU's power limit, as a percent of its default limit, in watts, or the card's own
/// minimum (<c>"limit": "min"</c>: a percent cannot say it portably, each card has its own floor). Reads go straight to
/// nvidia-smi (no elevation needed); the write goes through the admin broker, which checks the card's min..max again.
/// Resource <c>gpu/{index}/powerLimit</c>; a read is <c>{ watts, min, max, default }</c>, a target <c>{ percent }</c> or
/// <c>{ watts }</c> or <c>{ limit: "min" }</c>.
/// </summary>
internal sealed class GpuPowerLimitModule(INvidiaSmi smi, IAdminBroker broker) : IActionModule
{
    /// <summary>Two limits closer than this are the same (nvidia-smi rounds to hundredths, a percent to whole watts).</summary>
    private const double ToleranceWatts = 1.0;
    /// <summary>The <c>limit</c> value that means the card's minimum.</summary>
    public const string MinLimit = "min";

    public ActionModuleDescriptor Descriptor { get; } = new(
        "gpu.powerLimit",
        1,
        "GPU power limit",
        (JsonObject)JsonNode.Parse("""
            {"type":"object","additionalProperties":false,"required":["gpu"],"properties":{
              "gpu":{"type":"string","title":"GPU"},
              "percent":{"type":"integer","minimum":10,"maximum":100,"title":"Limit, % of the default"},
              "watts":{"type":"number","minimum":1,"maximum":2000,"title":"Limit, W"},
              "limit":{"type":"string","enum":["min"],"title":"The card's minimum limit"}}}
            """)!,
        (JsonObject)JsonNode.Parse("""{"gpu":{"widget":"select","list":"gpus","label":"GPU"}}""")!,
        ["gpus"],
        RestorationClass.Reversible,
        true,
        new JsonObject { ["gpu"] = "0", ["percent"] = 70 });

    public async Task<ModuleAvailability> AvailableAsync(CancellationToken ct)
    {
        var q = await smi.QueryAsync(ct);
        if (q.Gpus is null) return ModuleAvailability.No(q.Error);
        return q.Gpus.Count == 0 ? ModuleAvailability.No("no NVIDIA GPU with a settable power limit") : ModuleAvailability.Yes;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>>> DiscoverAsync(CancellationToken ct)
    {
        var lists = new Dictionary<string, IReadOnlyList<DiscoveredItem>>(StringComparer.Ordinal);
        var q = await smi.QueryAsync(ct);
        if (q.Gpus is not null)
            lists["gpus"] = [.. q.Gpus.Select(g => new DiscoveredItem(g.Id, string.Create(CultureInfo.InvariantCulture, $"{g.Name} ({g.Min:0.#}..{g.Max:0.#} W, default {g.Default:0.#} W)")))];
        return lists;
    }

    public IReadOnlyList<string> Validate(JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var problems = new List<string>();
        if (JsonNumber.Text(settings["gpu"]) is not { } gpu || !int.TryParse(gpu, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index > 99)
            problems.Add("gpu is not a GPU index");
        bool percent = settings["percent"] is not null, watts = settings["watts"] is not null, min = settings["limit"] is not null;
        if ((percent ? 1 : 0) + (watts ? 1 : 0) + (min ? 1 : 0) != 1) problems.Add("set exactly one of percent, watts and limit");
        if (min && JsonNumber.Text(settings["limit"]) != MinLimit) problems.Add("limit is not \"min\"");
        if (percent && !JsonNumber.TryWhole(settings["percent"], 10, 100, out _)) problems.Add("percent is not 10..100");
        if (watts && (!JsonNumber.TryDouble(settings["watts"], out double w) || w < 1 || w > 2000)) problems.Add("watts is not 1..2000");
        return problems;
    }

    public IReadOnlyList<ResourceIntent> Expand(JsonObject settings)
    {
        if (Validate(settings).Count > 0) throw new ArgumentException("the settings are not valid", nameof(settings));
        var target = settings["percent"] is { } p ? new JsonObject { ["percent"] = p.DeepClone() }
            : settings["watts"] is { } w ? new JsonObject { ["watts"] = w.DeepClone() }
            : new JsonObject { ["limit"] = MinLimit };
        return [new ResourceIntent($"gpu/{JsonNumber.Text(settings["gpu"])}/powerLimit", target)];
    }

    private static string? GpuOf(string key)
    {
        var parts = key.Split('/');
        return parts.Length == 3 && parts[0] == "gpu" && parts[2] == "powerLimit" && parts[1].Length is > 0 and <= 2 && parts[1].All(char.IsAsciiDigit) ? parts[1] : null;
    }

    public async Task<ResourceRead> ReadAsync(string resourceKey, CancellationToken ct)
    {
        if (GpuOf(resourceKey) is not { } id) return ResourceRead.Failed($"unknown resource {resourceKey}");
        var q = await smi.QueryAsync(ct);
        if (q.Gpus is null) return ResourceRead.Failed(q.Error);
        return q.Gpus.FirstOrDefault(g => g.Id == id) is { } gpu ? ResourceRead.Of(BrokerCore.GpuJson(gpu)) : ResourceRead.Failed($"there is no GPU {id} with a power limit");
    }

    /// <summary>
    /// The watts a value means: its own watts, its percent of the default limit, or the card's minimum limit (its own, or
    /// the other value's: a target says "min", the read names the floor).
    /// </summary>
    private static double? WattsOf(JsonNode? value, JsonNode? other)
    {
        if (value is not JsonObject o) return null;
        if (JsonNumber.TryDouble(o["watts"], out double w)) return w;
        if (JsonNumber.Text(o["limit"]) == MinLimit)
            return JsonNumber.TryDouble(o["min"], out double own) ? own : other is JsonObject read && JsonNumber.TryDouble(read["min"], out double floor) ? floor : null;
        if (!JsonNumber.TryDouble(o["percent"], out double p)) return null;
        double def = 0;
        if (!JsonNumber.TryDouble(o["default"], out def) && !(other is JsonObject x && JsonNumber.TryDouble(x["default"], out def))) return null;
        return Math.Round(def * p / 100);
    }

    public bool Equivalent(string resourceKey, JsonNode? a, JsonNode? b)
    {
        if (WattsOf(a, b) is { } wa && WattsOf(b, a) is { } wb) return Math.Abs(wa - wb) < ToleranceWatts;
        // Two percent targets with no limits to turn them into watts.
        return a is JsonObject x && b is JsonObject y && JsonNumber.TryDouble(x["percent"], out double pa) && JsonNumber.TryDouble(y["percent"], out double pb) && pa == pb;
    }

    public async Task<ApplyResult> ApplyAsync(ResourceIntent intent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (GpuOf(intent.Key) is not { } id) return ApplyResult.Failed($"unknown resource {intent.Key}");
        var now = await ReadAsync(intent.Key, ct);
        if (!now.Ok) return ApplyResult.Failed(now.Error);
        if (WattsOf(intent.Target, now.Value) is not { } watts) return ApplyResult.Failed($"not a power limit: {intent.Target?.ToJsonString()}");
        double min = now.Value!["min"]!.GetValue<double>(), max = now.Value["max"]!.GetValue<double>();   // GpuJson writes all four
        if (watts < min || watts > max)
            return ApplyResult.Failed(string.Create(CultureInfo.InvariantCulture, $"{watts} W is outside GPU {id}'s {min}..{max} W"));
        var r = await broker.RunAsync("gpu.setPowerLimit", new JsonObject { ["gpu"] = id, ["watts"] = watts }, ct);
        if (!r.Ok) return ApplyResult.Failed(r.Error ?? "the broker refused");
        var back = await ReadAsync(intent.Key, ct);
        return back.Ok ? ApplyResult.Written(back.Value) : ApplyResult.Failed($"set, but the read-back failed: {back.Error}");
    }
}
