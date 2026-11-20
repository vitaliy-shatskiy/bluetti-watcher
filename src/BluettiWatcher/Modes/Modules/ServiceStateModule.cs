using System.Text.Json.Nodes;
using BluettiWatcher.Modes.Broker;

namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// <c>service.state</c>: listed services running or stopped, and optionally their start type, while a mode is active.
/// Keys <c>service/{name}/running</c> (bool) and <c>service/{name}/startType</c> (string). Reads use the user's rights;
/// every change goes through the admin broker (<c>service.start</c>, <c>service.stop</c>, <c>service.setStartType</c>)
/// and is read back once the service has settled. Reversible: the reconciler restores the baseline it journaled.
/// </summary>
/// <remarks>Pattern: Adapter.</remarks>
internal sealed class ServiceStateModule(IServiceReader services, IAdminBroker broker, TimeProvider time) : IActionModule, IBrokerTargets
{
    public const string Id = "service.state";
    private const string Prefix = "service/";
    private const string RunningSuffix = "/running";
    private const string StartTypeSuffix = "/startType";

    /// <summary>How long a start or stop may take to settle before the write counts as failed.</summary>
    public static readonly TimeSpan SettleBudget = TimeSpan.FromSeconds(10);

    /// <summary>Start types the broker can write (boot and system belong to drivers).</summary>
    private static readonly string[] WritableStartTypes = ["automatic", "delayedAutomatic", "manual", "disabled"];

    public ActionModuleDescriptor Descriptor { get; } = new(
        Id, 1, "Windows services",
        JsonFields.Parse("""
            {"type":"object","additionalProperties":false,"required":["services"],"properties":{
              "services":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["name","running"],"properties":{
                "name":{"type":"string"},
                "running":{"type":"boolean"},
                "startType":{"type":"string","enum":["unchanged","manual","automatic"]}}}}}}
            """),
        JsonFields.Parse("""
            {"services":{"widget":"table","label":"Services"},
             "services.name":{"widget":"select","list":"services","label":"Service"},
             "services.running":{"widget":"checkbox","label":"Running while the mode is active"},
             "services.startType":{"widget":"select","label":"Start type while the mode is active"}}
            """),
        ["services"], RestorationClass.Reversible, NeedsAdmin: true,
        JsonFields.Parse("""{"services":[]}"""));

    public Task<ModuleAvailability> AvailableAsync(CancellationToken ct) =>
        Task.FromResult(services.List() is null ? ModuleAvailability.No("the service manager cannot be read") : ModuleAvailability.Yes);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>>> DiscoverAsync(CancellationToken ct)
    {
        var lists = new Dictionary<string, IReadOnlyList<DiscoveredItem>>(StringComparer.Ordinal);
        if (services.List() is { } all)
            lists["services"] = [.. all.OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(s => new DiscoveredItem(s.Name, $"{s.DisplayName} ({StateWord(s.State)})"))];
        return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>>>(lists);
    }

    public IReadOnlyList<string> Validate(JsonObject settings)
    {
        var problems = new List<string>();
        if (settings["services"] is not JsonArray list) { problems.Add("services must be a list"); return problems; }
        if (list.Any(n => n is not JsonObject)) problems.Add("every service entry must be an object");
        var entries = JsonFields.Objects(list).ToList();
        JsonFields.CheckNames(entries.Select(e => JsonFields.Str(e["name"])), "service", problems);
        foreach (var e in entries.Where(e => JsonFields.Bool(e["running"]) is null))
            problems.Add($"service '{JsonFields.Str(e["name"])}': running must be true or false");
        return problems;
    }

    /// <summary>Every start, stop and start-type change goes through the broker: each service needs its policy entry.</summary>
    public IReadOnlyList<BrokerTarget> BrokerTargets(JsonObject settings) =>
        [.. JsonFields.Objects(settings["services"]).Select(e => JsonFields.Str(e["name"])).OfType<string>().Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(n => new BrokerTarget(BrokerTargetKind.Service, n))];

    public IReadOnlyList<ResourceIntent> Expand(JsonObject settings)
    {
        var intents = new List<ResourceIntent>();
        foreach (var e in JsonFields.Objects(settings["services"]))
        {
            if (JsonFields.Str(e["name"]) is not { Length: > 0 } name || JsonFields.Bool(e["running"]) is not { } running) continue;
            // The start type goes first: a service stopped while still Automatic can be started again by a trigger.
            if (JsonFields.Str(e["startType"]) is { } st && !st.Equals("unchanged", StringComparison.OrdinalIgnoreCase))
                intents.Add(new ResourceIntent(Prefix + name + StartTypeSuffix, JsonValue.Create(st)));
            intents.Add(new ResourceIntent(Prefix + name + RunningSuffix, JsonValue.Create(running)));
        }
        return intents;
    }

    public Task<ResourceRead> ReadAsync(string resourceKey, CancellationToken ct)
    {
        if (!TryKey(resourceKey, out string name, out bool isRunning)) return Task.FromResult(ResourceRead.Failed($"not a {Id} key: {resourceKey}"));
        var q = services.Query(name);
        if (q.Info is not { } info) return Task.FromResult(ResourceRead.Failed(q.Error));
        return Task.FromResult(ResourceRead.Of(isRunning ? JsonValue.Create(IsRunning(info.State)) : JsonValue.Create(info.StartType)));
    }

    public bool Equivalent(string resourceKey, JsonNode? a, JsonNode? b)
    {
        if (resourceKey.EndsWith(RunningSuffix, StringComparison.Ordinal)) return JsonFields.Bool(a) is { } x && JsonFields.Bool(b) is { } y && x == y;
        return string.Equals(JsonFields.Str(a), JsonFields.Str(b), StringComparison.OrdinalIgnoreCase) && JsonFields.Str(a) is not null;
    }

    public Task<ApplyResult> ApplyAsync(ResourceIntent intent, CancellationToken ct)
    {
        if (!TryKey(intent.Key, out string name, out bool isRunning)) return Task.FromResult(ApplyResult.Failed($"not a {Id} key: {intent.Key}"));
        return isRunning ? ApplyRunningAsync(name, intent.Target, ct) : ApplyStartTypeAsync(name, intent.Target, ct);
    }

    private async Task<ApplyResult> ApplyRunningAsync(string name, JsonNode? target, CancellationToken ct)
    {
        if (JsonFields.Bool(target) is not { } want) return ApplyResult.Failed($"service '{name}': the target must be true or false");
        var before = services.Query(name);
        if (before.Info is not { } info) return ApplyResult.Failed(before.Error);
        if (Settled(info.State) && IsRunning(info.State) == want) return ApplyResult.Written(JsonValue.Create(want));
        bool heading = want ? info.State == ServiceRunState.StartPending : info.State == ServiceRunState.StopPending;
        if (!heading)
        {
            var r = await broker.RunAsync(want ? "service.start" : "service.stop", new JsonObject { ["name"] = name }, ct);
            if (!r.Ok) return ApplyResult.Failed($"service '{name}': the broker refused or failed: {r.Error}");
        }
        string lastError = "";
        ServiceRunState? last = null;
        bool done = await ModuleWait.UntilAsync(_ =>
        {
            var q = services.Query(name);
            last = q.Info?.State;
            lastError = q.Error;
            return Task.FromResult(q.Info is { } i && Settled(i.State) && IsRunning(i.State) == want);
        }, SettleBudget, time, ct);
        if (done) return ApplyResult.Written(JsonValue.Create(want));
        return ApplyResult.Failed(last is { } s
            ? $"service '{name}' is {StateWord(s)} {SettleBudget.TotalSeconds:0} s after the {(want ? "start" : "stop")}"
            : $"service '{name}' could not be read back: {lastError}");
    }

    private async Task<ApplyResult> ApplyStartTypeAsync(string name, JsonNode? target, CancellationToken ct)
    {
        string? want = WritableStartTypes.FirstOrDefault(t => t.Equals(JsonFields.Str(target), StringComparison.OrdinalIgnoreCase));
        if (want is null) return ApplyResult.Failed($"service '{name}': start type '{JsonFields.Str(target)}' cannot be written");
        var before = services.Query(name);
        if (before.Info is not { } info) return ApplyResult.Failed(before.Error);
        if (info.StartType.Equals(want, StringComparison.OrdinalIgnoreCase)) return ApplyResult.Written(JsonValue.Create(info.StartType));
        var r = await broker.RunAsync("service.setStartType", new JsonObject { ["name"] = name, ["startType"] = want }, ct);
        if (!r.Ok) return ApplyResult.Failed($"service '{name}': the broker refused or failed: {r.Error}");
        var after = services.Query(name);
        if (after.Info is not { } now) return ApplyResult.Failed($"service '{name}' could not be read back: {after.Error}");
        return now.StartType.Equals(want, StringComparison.OrdinalIgnoreCase)
            ? ApplyResult.Written(JsonValue.Create(now.StartType))
            : ApplyResult.Failed($"service '{name}': start type reads '{now.StartType}' after writing '{want}'");
    }

    private static bool TryKey(string key, out string name, out bool isRunning)
    {
        isRunning = key.EndsWith(RunningSuffix, StringComparison.Ordinal);
        name = JsonFields.KeyName(key, Prefix, isRunning ? RunningSuffix : StartTypeSuffix) ?? "";
        return name.Length > 0;
    }

    /// <summary>Running for a mode: anything but stopped or on its way there (a paused service still holds its process).</summary>
    private static bool IsRunning(ServiceRunState s) => s is not (ServiceRunState.Stopped or ServiceRunState.StopPending);

    private static bool Settled(ServiceRunState s) => s is ServiceRunState.Stopped or ServiceRunState.Running or ServiceRunState.Paused;

    private static string StateWord(ServiceRunState s) => s switch
    {
        ServiceRunState.Stopped => "stopped",
        ServiceRunState.StartPending => "starting",
        ServiceRunState.StopPending => "stopping",
        ServiceRunState.Running => "running",
        ServiceRunState.ContinuePending => "resuming",
        ServiceRunState.PausePending => "pausing",
        ServiceRunState.Paused => "paused",
        _ => "unknown",
    };
}
