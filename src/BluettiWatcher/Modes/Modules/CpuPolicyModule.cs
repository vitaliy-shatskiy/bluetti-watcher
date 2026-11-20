using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// <c>windows.cpuPolicy</c>: maximum processor state (%) and boost mode, on mains and on battery separately, written to
/// one plan: <c>plan</c> = a GUID, or <c>active</c> = the plan active when applied. Resources
/// <c>plan/{guid|active}/{ac|dc}/{maxProcessorState|boostMode}</c>, one per field set; values carry their plan
/// (<see cref="PlanSettingAccess"/>).
/// </summary>
internal sealed class CpuPolicyModule(IPowerSchemes schemes) : IActionModule
{
    /// <summary>PERFBOOSTMODE's values, in their index order.</summary>
    internal static readonly string[] BoostModes =
        ["disabled", "enabled", "aggressive", "efficientEnabled", "efficientAggressive", "aggressiveAtGuaranteed", "efficientAggressiveAtGuaranteed"];

    private static readonly (string Field, PowerSetting Setting, PowerSource Source)[] Fields =
    [
        ("maxStateAc", PowerSetting.MaxProcessorState, PowerSource.Ac),
        ("maxStateDc", PowerSetting.MaxProcessorState, PowerSource.Dc),
        ("boostAc", PowerSetting.BoostMode, PowerSource.Ac),
        ("boostDc", PowerSetting.BoostMode, PowerSource.Dc),
    ];

    private readonly PlanSettingAccess _access = new(schemes);

    public ActionModuleDescriptor Descriptor { get; } = new(
        "windows.cpuPolicy",
        1,
        "Processor policy",
        (JsonObject)JsonNode.Parse("""
            {"type":"object","additionalProperties":false,"properties":{
              "plan":{"type":"string","title":"Plan (a plan GUID, or active)"},
              "maxStateAc":{"type":"integer","minimum":1,"maximum":100,"title":"Maximum processor state on mains, %"},
              "maxStateDc":{"type":"integer","minimum":1,"maximum":100,"title":"Maximum processor state on battery, %"},
              "boostAc":{"type":"string","enum":BOOST,"title":"Boost mode on mains"},
              "boostDc":{"type":"string","enum":BOOST,"title":"Boost mode on battery"}}}
            """.Replace("BOOST", Enum(), StringComparison.Ordinal))!,
        (JsonObject)JsonNode.Parse("""{"plan":{"widget":"select","list":"plans","extra":["active"],"label":"Plan"}}""")!,
        ["plans"],
        RestorationClass.Reversible,
        false,
        new JsonObject { ["plan"] = "active", ["boostAc"] = "disabled", ["boostDc"] = "disabled" });

    private static string Enum() => "[" + string.Join(',', BoostModes.Select(m => $"\"{m}\"")) + "]";

    public Task<ModuleAvailability> AvailableAsync(CancellationToken ct) =>
        Task.FromResult(schemes.ActivePlan() is null ? ModuleAvailability.No("the active power plan could not be read") : ModuleAvailability.Yes);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>>> DiscoverAsync(CancellationToken ct) =>
        Task.FromResult(PowerPlanModule.PlanLists(schemes));

    public IReadOnlyList<string> Validate(JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var problems = new List<string>();
        if (settings["plan"] is { } p && (JsonNumber.Text(p) is not { } text || !PlanSettingAccess.TryParsePlan(text, out _)))
            problems.Add("plan is neither a plan GUID nor active");
        foreach (var (field, setting, _) in Fields)
        {
            if (settings[field] is not { } v) continue;
            if (setting == PowerSetting.MaxProcessorState ? !JsonNumber.TryWhole(v, 1, 100, out _) : BoostIndex(v) is null)
                problems.Add($"{field} is not a valid value");
        }
        if (!Fields.Any(f => settings[f.Field] is not null)) problems.Add("no setting chosen");
        return problems;
    }

    private static int? BoostIndex(JsonNode? node) =>
        JsonNumber.Text(node) is { } s && Array.IndexOf(BoostModes, s) is var i and >= 0 ? i : null;

    public IReadOnlyList<ResourceIntent> Expand(JsonObject settings)
    {
        if (Validate(settings).Count > 0) throw new ArgumentException("the settings are not valid", nameof(settings));
        if (!PlanSettingAccess.TryParsePlan(JsonNumber.Text(settings["plan"]) ?? "active", out var plan)) throw new ArgumentException("plan is not valid", nameof(settings));
        var intents = new List<ResourceIntent>();
        foreach (var (field, setting, source) in Fields)
        {
            if (settings[field] is not { } v) continue;
            long value = setting == PowerSetting.MaxProcessorState ? (JsonNumber.TryWhole(v, 1, 100, out long n) ? n : 0) : BoostIndex(v)!.Value;
            intents.Add(new ResourceIntent(Key(plan, source, setting), new JsonObject { ["value"] = value }));
        }
        return intents;
    }

    internal static string Key(Guid? plan, PowerSource source, PowerSetting setting) =>
        $"plan/{PlanSettingAccess.PlanText(plan)}/{(source == PowerSource.Ac ? "ac" : "dc")}/{setting.Name}";

    private static bool TryParseKey(string key, out Guid? plan, out PowerSource source, out PowerSetting setting)
    {
        plan = null;
        source = PowerSource.Ac;
        setting = PowerSetting.MaxProcessorState;
        var parts = key.Split('/');
        if (parts.Length != 4 || parts[0] != "plan" || !PlanSettingAccess.TryParsePlan(parts[1], out plan)) return false;
        if (parts[2] is not ("ac" or "dc")) return false;
        source = parts[2] == "ac" ? PowerSource.Ac : PowerSource.Dc;
        var match = Fields.Select(f => f.Setting).FirstOrDefault(s => s.Name == parts[3]);
        if (match is null) return false;
        setting = match;
        return true;
    }

    public Task<ResourceRead> ReadAsync(string resourceKey, CancellationToken ct) =>
        Task.FromResult(TryParseKey(resourceKey, out var plan, out var source, out var setting)
            ? _access.Read(plan, setting, source)
            : ResourceRead.Failed($"unknown resource {resourceKey}"));

    public bool Equivalent(string resourceKey, JsonNode? a, JsonNode? b) => PlanSettingAccess.Equivalent(a, b);

    public Task<ApplyResult> ApplyAsync(ResourceIntent intent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return Task.FromResult(TryParseKey(intent.Key, out var plan, out var source, out var setting)
            ? _access.Apply(plan, setting, source, intent.Target)
            : ApplyResult.Failed($"unknown resource {intent.Key}"));
    }
}
