using System.Text.Json.Nodes;
using BluettiWatcher.Modes;

namespace BluettiWatcher.Tests.Modes;

/// <summary>The machine the fake modules read and write: one value per resource key, with injectable failures.</summary>
internal sealed class FakeMachine
{
    public Dictionary<string, JsonNode?> Values { get; } = new(StringComparer.Ordinal);
    public HashSet<string> FailRead { get; } = new(StringComparer.Ordinal);
    public HashSet<string> FailWrite { get; } = new(StringComparer.Ordinal);
    /// <summary>The next write to this key stops the process (OperationCanceledException) before it changes anything.</summary>
    public HashSet<string> StopBeforeWrite { get; } = new(StringComparer.Ordinal);
    /// <summary>The next write to this key changes the value, then stops the process before Applied is journaled.</summary>
    public HashSet<string> StopAfterWrite { get; } = new(StringComparer.Ordinal);
    public List<string> Writes { get; } = [];

    public string? Text(string key) => Values.TryGetValue(key, out var v) ? v?.ToJsonString() : null;
}

/// <summary>
/// A module over one resource: settings <c>{ "key": "...", "value": ... }</c> want <c>key</c> to read <c>value</c>.
/// </summary>
internal sealed class FakeModule(string typeId, FakeMachine machine, RestorationClass restoration = RestorationClass.Reversible) : IActionModule
{
    public ActionModuleDescriptor Descriptor { get; } = new(
        typeId, 1, "Fake " + typeId,
        new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["key"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                ["value"] = new JsonObject { ["type"] = new JsonArray("string", "number", "boolean") },
            },
            ["required"] = new JsonArray("key", "value"),
            ["additionalProperties"] = false,
        },
        new JsonObject { ["value"] = new JsonObject { ["widget"] = "text" } },
        ["things"], restoration, false,
        new JsonObject { ["key"] = typeId + "/x", ["value"] = "on" });

    public bool Unavailable { get; set; }

    public Task<ModuleAvailability> AvailableAsync(CancellationToken ct) =>
        Task.FromResult(Unavailable ? ModuleAvailability.No("not on this machine") : ModuleAvailability.Yes);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>>> DiscoverAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>>>(
            new Dictionary<string, IReadOnlyList<DiscoveredItem>> { ["things"] = [new DiscoveredItem("a", "Thing A")] });

    public IReadOnlyList<string> Validate(JsonObject settings) => [];

    public IReadOnlyList<ResourceIntent> Expand(JsonObject settings) =>
        [new ResourceIntent(settings["key"]?.GetValue<string>() ?? "plan", (settings["value"] ?? settings["plan"])?.DeepClone())];

    public Task<ResourceRead> ReadAsync(string resourceKey, CancellationToken ct) => Task.FromResult(
        machine.FailRead.Contains(resourceKey) ? ResourceRead.Failed("read failed (fake)")
        : ResourceRead.Of(machine.Values.TryGetValue(resourceKey, out var v) ? v?.DeepClone() : null));

    public bool Equivalent(string resourceKey, JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);

    public Task<ApplyResult> ApplyAsync(ResourceIntent intent, CancellationToken ct)
    {
        machine.Writes.Add(intent.Key);
        if (machine.StopBeforeWrite.Remove(intent.Key)) throw new OperationCanceledException("stopped before the write");
        if (machine.FailWrite.Contains(intent.Key)) return Task.FromResult(ApplyResult.Failed("write failed (fake)"));
        machine.Values[intent.Key] = intent.Target?.DeepClone();
        if (machine.StopAfterWrite.Remove(intent.Key)) throw new OperationCanceledException("stopped after the write");
        return Task.FromResult(ApplyResult.Written(intent.Target?.DeepClone()));
    }
}
