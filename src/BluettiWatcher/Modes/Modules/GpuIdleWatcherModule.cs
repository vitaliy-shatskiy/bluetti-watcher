using System.Globalization;
using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// <c>gpu.idleWatcher</c>: an observer, not a setter. While armed it samples nvidia-smi; when a GPU stays in P0 at a high
/// graphics clock with next to no utilisation for the configured minutes, it records the processes holding the GPU (an
/// event row and a log line), once per idle stretch. It writes nothing to the machine.
/// </summary>
/// <remarks>
/// Restoration is Token: the resource <c>gpu/idleWatcher</c> is the watcher's own arming, released (disarmed) when the
/// mode is left and never resurrected after a restart. Pattern: Observer over a polled source.
/// </remarks>
internal sealed class GpuIdleWatcherModule(IGpuSampler smi, TimeProvider time, Action<DateTimeOffset, string, string> addEvent, Action<string> log)
    : IActionModule
{
    public const string TypeId = "gpu.idleWatcher";
    public const string ResourceKey = "gpu/idleWatcher";
    public const string EventKind = "gpu-idle-p0";
    private static readonly TimeSpan SampleEvery = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private readonly Dictionary<int, DateTimeOffset> _idleSince = [];
    private readonly HashSet<int> _reported = [];
    private JsonObject? _armed;
    // Each arming starts one sampling loop; a loop whose generation is no longer current ends at its next tick.
    private long _generation;

    public ActionModuleDescriptor Descriptor { get; } = new(
        TypeId, 1, "GPU idle-at-P0 watcher",
        (JsonObject)JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "minutes": { "type": "integer", "minimum": 1, "maximum": 240 },
                "minClockMhz": { "type": "integer", "minimum": 0, "maximum": 5000 },
                "maxUtilization": { "type": "integer", "minimum": 0, "maximum": 100 }
              },
              "required": ["minutes", "minClockMhz", "maxUtilization"],
              "additionalProperties": false
            }
            """)!,
        (JsonObject)JsonNode.Parse("""
            {
              "minutes": { "widget": "number", "label": "Idle minutes before logging" },
              "minClockMhz": { "widget": "number", "label": "Graphics clock counted as high, MHz" },
              "maxUtilization": { "widget": "number", "label": "Utilisation counted as idle, %" }
            }
            """)!,
        [], RestorationClass.Token, NeedsAdmin: false,
        (JsonObject)JsonNode.Parse("""{ "minutes": 5, "minClockMhz": 1000, "maxUtilization": 2 }""")!);

    public async Task<ModuleAvailability> AvailableAsync(CancellationToken ct) =>
        await smi.SampleAsync(ct) is { Count: > 0 } ? ModuleAvailability.Yes : ModuleAvailability.No("nvidia-smi reports no NVIDIA GPU");

    public Task<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>>> DiscoverAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>>>(new Dictionary<string, IReadOnlyList<DiscoveredItem>>(StringComparer.Ordinal));

    public IReadOnlyList<string> Validate(JsonObject settings) => [];

    public IReadOnlyList<ResourceIntent> Expand(JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return [new ResourceIntent(ResourceKey, new JsonObject
        {
            ["armed"] = true,
            ["minutes"] = Setting(settings, "minutes", 5),
            ["minClockMhz"] = Setting(settings, "minClockMhz", 1000),
            ["maxUtilization"] = Setting(settings, "maxUtilization", 2),
        })];
    }

    public Task<ResourceRead> ReadAsync(string resourceKey, CancellationToken ct)
    {
        if (!string.Equals(resourceKey, ResourceKey, StringComparison.Ordinal)) return Task.FromResult(ResourceRead.Failed($"not the watcher key: {resourceKey}"));
        lock (_gate) return Task.FromResult(ResourceRead.Of(_armed?.DeepClone() ?? new JsonObject { ["armed"] = false }));
    }

    public bool Equivalent(string resourceKey, JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);

    /// <summary>A target with <c>armed: true</c> arms (or re-tunes) the watcher; anything else disarms it.</summary>
    public async Task<ApplyResult> ApplyAsync(ResourceIntent intent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (!string.Equals(intent.Key, ResourceKey, StringComparison.Ordinal)) return ApplyResult.Failed($"not the watcher key: {intent.Key}");
        bool arm = intent.Target is JsonObject o && o["armed"] is JsonValue v && v.TryGetValue(out bool b) && b;
        lock (_gate)
        {
            long generation = ++_generation;
            _armed = arm ? (JsonObject)intent.Target!.DeepClone() : null;   // arm implies Target is a JsonObject
            _idleSince.Clear();
            _reported.Clear();
            // Runs past this call on purpose: the loop lives as long as the arming, not the request that armed it.
            if (arm) _ = LoopAsync(generation, CancellationToken.None);
        }
        var back = await ReadAsync(ResourceKey, ct);
        return ApplyResult.Written(back.Value);
    }

    /// <summary>One look at the GPUs: extends or breaks each GPU's idle stretch and reports a stretch that reached the limit.</summary>
    internal async Task SampleOnceAsync(CancellationToken ct)
    {
        JsonObject? armed;
        lock (_gate) armed = _armed;
        if (armed is null) return;
        var samples = await smi.SampleAsync(ct);
        var now = time.GetUtcNow();
        var due = new List<GpuSample>();
        lock (_gate)
        {
            if (!ReferenceEquals(armed, _armed)) return;   // re-armed or disarmed while sampling
            if (samples is null)
            {
                // No reading: the stretch cannot be called continuous, so it starts over.
                _idleSince.Clear();
                _reported.Clear();
                return;
            }
            var limit = TimeSpan.FromMinutes(Setting(armed, "minutes", 5));
            foreach (var s in samples)
            {
                if (!IsIdleAtP0(s, armed))
                {
                    _idleSince.Remove(s.Index);
                    _reported.Remove(s.Index);
                    continue;
                }
                if (!_idleSince.TryGetValue(s.Index, out var since)) _idleSince[s.Index] = since = now;
                if (now - since >= limit && _reported.Add(s.Index)) due.Add(s);
            }
        }
        if (due.Count == 0) return;
        var processes = await smi.ProcessesAsync(ct);
        foreach (var s in due) Report(s, processes, now, Setting(armed, "minutes", 5));
    }

    private async Task LoopAsync(long generation, CancellationToken ct)
    {
        while (true)
        {
            await Task.Delay(SampleEvery, time, ct);
            lock (_gate)
                if (generation != _generation) return;   // disarmed or re-armed: a newer loop (or none) owns sampling
            // One failed look (a store write, an odd nvidia-smi answer) must not end the watch silently.
            try { await SampleOnceAsync(ct); }
            catch (Exception e) when (e is not OperationCanceledException) { log($"gpu idle watcher: a sample failed: {e.Message}"); }
        }
    }

    private void Report(GpuSample s, IReadOnlyList<GpuProcess>? processes, DateTimeOffset now, int minutes)
    {
        string holders = processes is null
            ? "the process list could not be read"
            : processes.Where(p => p.Gpu == s.Index).ToList() is { Count: > 0 } mine
                ? string.Join(", ", mine.Select(p => string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileName(p.Name)} (pid {p.Pid}, {p.Type})")))
                : "no process listed";
        string power = s.PowerW is { } w ? string.Create(CultureInfo.InvariantCulture, $"{w:0} W") : "power n/a";
        string detail = string.Create(CultureInfo.InvariantCulture,
            $"GPU {s.Index} in {s.PState} at {s.ClockMhz:0} MHz, {power}, {s.UtilizationPct:0} % busy for {minutes} min; holding it: {holders}");
        addEvent(now, EventKind, detail);
        log("gpu idle watcher: " + detail);
    }

    private static bool IsIdleAtP0(GpuSample s, JsonObject armed) =>
        string.Equals(s.PState.Trim(), "P0", StringComparison.OrdinalIgnoreCase)
        && s.ClockMhz >= Setting(armed, "minClockMhz", 1000)
        && s.UtilizationPct <= Setting(armed, "maxUtilization", 2);

    private static int Setting(JsonObject o, string name, int fallback) =>
        MonitorTargets.Number(o[name]) is { } n ? (int)n : fallback;
}
