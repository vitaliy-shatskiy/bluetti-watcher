using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// <c>display.brightness</c>: DDC/CI brightness per physical monitor, in percent of the monitor's own maximum.
/// Resource <c>monitor/{id}/brightness</c>; a monitor that does not answer DDC/CI is unavailable, never guessed.
/// </summary>
/// <remarks>Pattern: Adapter.</remarks>
internal sealed class BrightnessModule(IDdcMonitors ddc) : IActionModule
{
    public const string TypeId = "display.brightness";
    private const string Field = "brightness";

    public ActionModuleDescriptor Descriptor { get; } = new(
        TypeId, 1, "Monitor brightness (DDC/CI)",
        (JsonObject)JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "monitors": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "monitor": { "type": "string" },
                      "percent": { "type": "integer", "minimum": 0, "maximum": 100 }
                    },
                    "required": ["monitor", "percent"],
                    "additionalProperties": false
                  }
                }
              },
              "required": ["monitors"],
              "additionalProperties": false
            }
            """)!,
        (JsonObject)JsonNode.Parse("""{ "monitors": { "widget": "list", "list": "monitors", "label": "Brightness per monitor, %" } }""")!,
        ["monitors"], RestorationClass.Reversible, NeedsAdmin: false,
        (JsonObject)JsonNode.Parse("""{ "monitors": [] }""")!);

    public async Task<ModuleAvailability> AvailableAsync(CancellationToken ct) => await ddc.MonitorsAsync(ct) switch
    {
        null => ModuleAvailability.No("the monitors cannot be listed"),
        var list when list.Any(m => m.Ddc) => ModuleAvailability.Yes,
        _ => ModuleAvailability.No("no monitor answers DDC/CI"),
    };

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>>> DiscoverAsync(CancellationToken ct)
    {
        var lists = new Dictionary<string, IReadOnlyList<DiscoveredItem>>(StringComparer.Ordinal);
        if (await ddc.MonitorsAsync(ct) is { } monitors)
            lists["monitors"] = [.. monitors.Select(m => new DiscoveredItem(m.Id, m.Ddc ? m.Name : $"{m.Name} (no DDC/CI: {m.Reason})"))];
        return lists;
    }

    public IReadOnlyList<string> Validate(JsonObject settings) => MonitorTargets.Validate(settings, "percent");

    public IReadOnlyList<ResourceIntent> Expand(JsonObject settings) => MonitorTargets.Expand(settings, "percent", Field);

    public async Task<ResourceRead> ReadAsync(string resourceKey, CancellationToken ct)
    {
        if (MonitorTargets.MonitorOf(resourceKey, Field) is not { } id) return ResourceRead.Failed($"not a brightness key: {resourceKey}");
        var r = await ddc.ReadBrightnessAsync(id, ct);
        return r.Ok ? ResourceRead.Of(JsonValue.Create(Percent(r))) : ResourceRead.Failed(r.Error.Length > 0 ? r.Error : "the monitor reported no maximum");
    }

    /// <summary>Percent of a monitor maximum that is not 100 rounds both ways: one step apart is the same brightness.</summary>
    public bool Equivalent(string resourceKey, JsonNode? a, JsonNode? b) =>
        MonitorTargets.Number(a) is { } x && MonitorTargets.Number(b) is { } y && Math.Abs(x - y) <= 1;

    public async Task<ApplyResult> ApplyAsync(ResourceIntent intent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (MonitorTargets.MonitorOf(intent.Key, Field) is not { } id) return ApplyResult.Failed($"not a brightness key: {intent.Key}");
        if (MonitorTargets.Number(intent.Target) is not { } target || target < 0 || target > 100) return ApplyResult.Failed("the target is not a percent 0-100");
        var now = await ddc.ReadBrightnessAsync(id, ct);
        if (!now.Ok) return ApplyResult.Failed(now.Error.Length > 0 ? now.Error : "the monitor reported no maximum");
        int raw = (int)Math.Round(target * now.Max / 100, MidpointRounding.AwayFromZero);
        if (raw != now.Current && await ddc.WriteBrightnessAsync(id, raw, ct) is { } error) return ApplyResult.Failed(error);
        var back = await ReadAsync(intent.Key, ct);
        return back.Ok ? ApplyResult.Written(back.Value) : ApplyResult.Failed("written, but the read-back failed: " + back.Error);
    }

    private static int Percent(DdcBrightness r) => (int)Math.Round(r.Current * 100.0 / r.Max, MidpointRounding.AwayFromZero);
}
