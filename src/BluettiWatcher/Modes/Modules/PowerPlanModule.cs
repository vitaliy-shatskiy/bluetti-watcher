using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// <c>windows.powerPlan</c>: makes a plan active, chosen by GUID from the live list (user-created plans included). A plan
/// that is not on the machine is an error, never another plan in its place. Resource <c>windows.activePlan</c>.
/// </summary>
internal sealed class PowerPlanModule(IPowerSchemes schemes) : IActionModule
{
    public const string ActivePlanKey = "windows.activePlan";

    /// <summary>Balanced: the one plan every Windows has.</summary>
    private const string BalancedPlan = "381b4222-f694-41f0-9685-ff5bb260df2e";

    public ActionModuleDescriptor Descriptor { get; } = new(
        "windows.powerPlan",
        1,
        "Power plan",
        (JsonObject)JsonNode.Parse("""
            {"type":"object","properties":{"plan":{"type":"string","title":"Power plan"}},"required":["plan"],"additionalProperties":false}
            """)!,
        (JsonObject)JsonNode.Parse("""{"plan":{"widget":"select","list":"plans","label":"Power plan"}}""")!,
        ["plans"],
        RestorationClass.Reversible,
        false,
        new JsonObject { ["plan"] = BalancedPlan });

    public Task<ModuleAvailability> AvailableAsync(CancellationToken ct) =>
        Task.FromResult(schemes.ListPlans() is null ? ModuleAvailability.No("the power plans could not be read") : ModuleAvailability.Yes);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>>> DiscoverAsync(CancellationToken ct) =>
        Task.FromResult(PlanLists(schemes));

    /// <summary>The live <c>plans</c> list; absent when the plans cannot be read.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>> PlanLists(IPowerSchemes schemes)
    {
        var plans = schemes.ListPlans();
        var lists = new Dictionary<string, IReadOnlyList<DiscoveredItem>>(StringComparer.Ordinal);
        if (plans is not null) lists["plans"] = [.. plans.Select(p => new DiscoveredItem(p.Id.ToString("D"), p.Name))];
        return lists;
    }

    public IReadOnlyList<string> Validate(JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonNumber.Text(settings["plan"]) is { } p && Guid.TryParseExact(p, "D", out _) ? [] : ["plan is not a plan GUID"];
    }

    public IReadOnlyList<ResourceIntent> Expand(JsonObject settings)
    {
        if (Validate(settings).Count > 0) throw new ArgumentException("the settings are not valid", nameof(settings));
        return [new ResourceIntent(ActivePlanKey, Guid.ParseExact(JsonNumber.Text(settings["plan"])!, "D").ToString("D"))];
    }

    public Task<ResourceRead> ReadAsync(string resourceKey, CancellationToken ct)
    {
        if (resourceKey != ActivePlanKey) return Task.FromResult(ResourceRead.Failed($"unknown resource {resourceKey}"));
        return Task.FromResult(schemes.ActivePlan() is { } g ? ResourceRead.Of(g.ToString("D")) : ResourceRead.Failed("the active power plan could not be read"));
    }

    public bool Equivalent(string resourceKey, JsonNode? a, JsonNode? b) =>
        Guid.TryParse(JsonNumber.Text(a), out var x) && Guid.TryParse(JsonNumber.Text(b), out var y) && x == y;

    public async Task<ApplyResult> ApplyAsync(ResourceIntent intent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.Key != ActivePlanKey) return ApplyResult.Failed($"unknown resource {intent.Key}");
        if (!Guid.TryParse(JsonNumber.Text(intent.Target), out var plan)) return ApplyResult.Failed($"not a plan GUID: {intent.Target?.ToJsonString()}");
        var plans = schemes.ListPlans();
        if (plans is null) return ApplyResult.Failed("the power plans could not be read");
        if (!plans.Any(p => p.Id == plan)) return ApplyResult.Failed($"the power plan {plan:D} is not on this machine");
        if (schemes.SetActivePlan(plan) is { } error) return ApplyResult.Failed(error);
        var back = await ReadAsync(ActivePlanKey, ct);
        return back.Ok ? ApplyResult.Written(back.Value) : ApplyResult.Failed($"set, but the read-back failed: {back.Error}");
    }
}
