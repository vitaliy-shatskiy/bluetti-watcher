using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// <c>display.offTimeout</c>: turn the display off after N minutes (0 = never), on mains and on battery, on the plan
/// active when applied. Resources <c>display/{ac|dc}/offTimeout</c>; values are seconds and carry their plan
/// (<see cref="PlanSettingAccess"/>), so a restore goes back to the plan the baseline was read from.
/// </summary>
internal sealed class DisplayOffTimeoutModule(IPowerSchemes schemes) : IActionModule
{
    private const int MaxMinutes = 600;

    private static readonly (string Field, PowerSource Source, string Key)[] Fields =
    [
        ("acMinutes", PowerSource.Ac, "display/ac/offTimeout"),
        ("dcMinutes", PowerSource.Dc, "display/dc/offTimeout"),
    ];

    private readonly PlanSettingAccess _access = new(schemes);

    public ActionModuleDescriptor Descriptor { get; } = new(
        "display.offTimeout",
        1,
        "Display off timeout",
        (JsonObject)JsonNode.Parse("""
            {"type":"object","additionalProperties":false,"properties":{
              "acMinutes":{"type":"integer","minimum":0,"maximum":600,"title":"On mains, minutes (0 = never)"},
              "dcMinutes":{"type":"integer","minimum":0,"maximum":600,"title":"On battery, minutes (0 = never)"}}}
            """)!,
        new JsonObject(),
        [],
        RestorationClass.Reversible,
        false,
        new JsonObject { ["acMinutes"] = 10, ["dcMinutes"] = 5 });

    public Task<ModuleAvailability> AvailableAsync(CancellationToken ct) =>
        Task.FromResult(schemes.ActivePlan() is null ? ModuleAvailability.No("the active power plan could not be read") : ModuleAvailability.Yes);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>>> DiscoverAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>>>(new Dictionary<string, IReadOnlyList<DiscoveredItem>>(StringComparer.Ordinal));

    public IReadOnlyList<string> Validate(JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var problems = new List<string>();
        foreach (var (field, _, _) in Fields)
            if (settings[field] is { } v && !JsonNumber.TryWhole(v, 0, MaxMinutes, out _)) problems.Add($"{field} is not 0..{MaxMinutes} whole minutes");
        if (!Fields.Any(f => settings[f.Field] is not null)) problems.Add("no setting chosen");
        return problems;
    }

    public IReadOnlyList<ResourceIntent> Expand(JsonObject settings)
    {
        if (Validate(settings).Count > 0) throw new ArgumentException("the settings are not valid", nameof(settings));
        return [.. Fields.Where(f => settings[f.Field] is not null).Select(f =>
        {
            JsonNumber.TryWhole(settings[f.Field], 0, MaxMinutes, out long minutes);
            return new ResourceIntent(f.Key, new JsonObject { ["value"] = minutes * 60 });
        })];
    }

    private static PowerSource? SourceOf(string key) => Fields.Where(f => f.Key == key).Select(f => (PowerSource?)f.Source).FirstOrDefault();

    public Task<ResourceRead> ReadAsync(string resourceKey, CancellationToken ct) =>
        Task.FromResult(SourceOf(resourceKey) is { } source
            ? _access.Read(null, PowerSetting.DisplayOff, source)
            : ResourceRead.Failed($"unknown resource {resourceKey}"));

    public bool Equivalent(string resourceKey, JsonNode? a, JsonNode? b) => PlanSettingAccess.Equivalent(a, b);

    public Task<ApplyResult> ApplyAsync(ResourceIntent intent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return Task.FromResult(SourceOf(intent.Key) is { } source
            ? _access.Apply(null, PowerSetting.DisplayOff, source, intent.Target)
            : ApplyResult.Failed($"unknown resource {intent.Key}"));
    }
}
