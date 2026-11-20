using System.Globalization;
using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// <c>display.refreshRate</c>: a refresh rate per monitor, among the rates the monitor offers at its current resolution.
/// Resource <c>monitor/{id}/refresh</c> (whole Hz); the baseline is the rate read before the mode wrote.
/// </summary>
/// <remarks>Pattern: Adapter.</remarks>
internal sealed class RefreshRateModule(IDisplayConfig display) : IActionModule
{
    public const string TypeId = "display.refreshRate";
    private const string Field = "refresh";

    public ActionModuleDescriptor Descriptor { get; } = new(
        TypeId, 1, "Monitor refresh rate",
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
                      "hz": { "type": "integer", "minimum": 1, "maximum": 1000 }
                    },
                    "required": ["monitor", "hz"],
                    "additionalProperties": false
                  }
                }
              },
              "required": ["monitors"],
              "additionalProperties": false
            }
            """)!,
        (JsonObject)JsonNode.Parse("""{ "monitors": { "widget": "list", "list": "monitors", "label": "Refresh rate per monitor" } }""")!,
        ["monitors"], RestorationClass.Reversible, NeedsAdmin: false,
        (JsonObject)JsonNode.Parse("""{ "monitors": [] }""")!);

    public Task<ModuleAvailability> AvailableAsync(CancellationToken ct) => Task.FromResult(display.Monitors() switch
    {
        null => ModuleAvailability.No("the display configuration cannot be read"),
        { Count: 0 } => ModuleAvailability.No("no active monitor"),
        _ => ModuleAvailability.Yes,
    });

    public Task<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>>> DiscoverAsync(CancellationToken ct)
    {
        var lists = new Dictionary<string, IReadOnlyList<DiscoveredItem>>(StringComparer.Ordinal);
        if (display.Monitors() is { } monitors)
            lists["monitors"] = [.. monitors.Select(m => new DiscoveredItem(m.Id, string.Create(CultureInfo.InvariantCulture,
                $"{m.Name} ({m.Width}x{m.Height}, {m.Hz} Hz; offers {string.Join(", ", m.SupportedHz)} Hz)")))];
        return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>>>(lists);
    }

    public IReadOnlyList<string> Validate(JsonObject settings) => MonitorTargets.Validate(settings, "hz");

    public IReadOnlyList<ResourceIntent> Expand(JsonObject settings) => MonitorTargets.Expand(settings, "hz", Field);

    public Task<ResourceRead> ReadAsync(string resourceKey, CancellationToken ct) => Task.FromResult(Read(resourceKey));

    public bool Equivalent(string resourceKey, JsonNode? a, JsonNode? b) =>
        MonitorTargets.Number(a) is { } x && MonitorTargets.Number(b) is { } y && Math.Round(x) == Math.Round(y);

    public Task<ApplyResult> ApplyAsync(ResourceIntent intent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return Task.FromResult(Apply(intent));
    }

    private ApplyResult Apply(ResourceIntent intent)
    {
        if (MonitorTargets.MonitorOf(intent.Key, Field) is not { } id) return ApplyResult.Failed($"not a refresh-rate key: {intent.Key}");
        if (MonitorTargets.Number(intent.Target) is not { } target) return ApplyResult.Failed("the target is not a number of Hz");
        int hz = (int)Math.Round(target);
        if (Find(id) is not { } m) return ApplyResult.Failed(Missing(id));
        if (m.Hz != hz)
        {
            if (!m.SupportedHz.Contains(hz))
                return ApplyResult.Failed(string.Create(CultureInfo.InvariantCulture,
                    $"{m.Name} does not offer {hz} Hz at {m.Width}x{m.Height} (offers {string.Join(", ", m.SupportedHz)} Hz)"));
            if (display.SetRefreshRate(id, hz) is { } error) return ApplyResult.Failed(error);
        }
        var back = Read(intent.Key);
        return back.Ok ? ApplyResult.Written(back.Value) : ApplyResult.Failed("written, but the read-back failed: " + back.Error);
    }

    private ResourceRead Read(string key)
    {
        if (MonitorTargets.MonitorOf(key, Field) is not { } id) return ResourceRead.Failed($"not a refresh-rate key: {key}");
        if (display.Monitors() is null) return ResourceRead.Failed("the display configuration cannot be read");
        return Find(id) is { } m ? ResourceRead.Of(JsonValue.Create(m.Hz)) : ResourceRead.Failed(Missing(id));
    }

    private DisplayMonitor? Find(string id) =>
        display.Monitors()?.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    private static string Missing(string id) => $"monitor {id} is not connected or not active";
}
