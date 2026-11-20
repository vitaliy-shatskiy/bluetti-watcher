using System.Text.Json.Nodes;
using BluettiWatcher.Modes.Broker;

namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// <c>scheduledTask.state</c>: listed scheduled tasks enabled or disabled while a mode is active (pause a backup's
/// schedule). Key <c>task/{path}/enabled</c> (bool). A task the user owns is changed directly; one that refuses the user
/// (an admin or SYSTEM task) goes through the admin broker (<c>task.enable</c>, <c>task.disable</c>). Reversible.
/// </summary>
/// <remarks>Pattern: Adapter.</remarks>
internal sealed class ScheduledTaskModule(ITaskScheduler tasks, IAdminBroker broker) : IActionModule, IBrokerTargets
{
    public const string Id = "scheduledTask.state";
    private const string Prefix = "task/";
    private const string Suffix = "/enabled";

    public ActionModuleDescriptor Descriptor { get; } = new(
        Id, 1, "Scheduled tasks",
        JsonFields.Parse("""
            {"type":"object","additionalProperties":false,"required":["tasks"],"properties":{
              "tasks":{"type":"array","items":{"type":"object","additionalProperties":false,"required":["path","enabled"],"properties":{
                "path":{"type":"string"},
                "enabled":{"type":"boolean"}}}}}}
            """),
        JsonFields.Parse("""
            {"tasks":{"widget":"table","label":"Scheduled tasks"},
             "tasks.path":{"widget":"select","list":"tasks","label":"Task"},
             "tasks.enabled":{"widget":"checkbox","label":"Enabled while the mode is active"}}
            """),
        ["tasks"], RestorationClass.Reversible, NeedsAdmin: false,
        JsonFields.Parse("""{"tasks":[]}"""));

    public async Task<ModuleAvailability> AvailableAsync(CancellationToken ct) =>
        await tasks.ListAsync(ct) is null ? ModuleAvailability.No("the scheduled tasks cannot be listed") : ModuleAvailability.Yes;

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>>> DiscoverAsync(CancellationToken ct)
    {
        var lists = new Dictionary<string, IReadOnlyList<DiscoveredItem>>(StringComparer.Ordinal);
        if (await tasks.ListAsync(ct) is { } all) lists["tasks"] = [.. all.Select(p => new DiscoveredItem(p, p))];
        return lists;
    }

    public IReadOnlyList<string> Validate(JsonObject settings)
    {
        var problems = new List<string>();
        if (settings["tasks"] is not JsonArray list) { problems.Add("tasks must be a list"); return problems; }
        if (list.Any(n => n is not JsonObject)) problems.Add("every task entry must be an object");
        var entries = JsonFields.Objects(list).ToList();
        JsonFields.CheckNames(entries.Select(e => JsonFields.Str(e["path"])), "task", problems);
        foreach (var e in entries)
        {
            string? path = JsonFields.Str(e["path"]);
            if (path is { Length: > 0 } && !path.StartsWith('\\')) problems.Add($"task '{path}': the full path starts with '\\'");
            if (JsonFields.Bool(e["enabled"]) is null) problems.Add($"task '{path}': enabled must be true or false");
        }
        return problems;
    }

    /// <summary>A task the user may not change (an administrator's or SYSTEM's) is changed by the broker: it needs its policy entry.</summary>
    public IReadOnlyList<BrokerTarget> BrokerTargets(JsonObject settings) =>
        [.. JsonFields.Objects(settings["tasks"]).Select(e => JsonFields.Str(e["path"])).OfType<string>().Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(p => new BrokerTarget(BrokerTargetKind.Task, p))];

    public IReadOnlyList<ResourceIntent> Expand(JsonObject settings) =>
        [.. JsonFields.Objects(settings["tasks"])
            .Select(e => (Path: JsonFields.Str(e["path"]), Enabled: JsonFields.Bool(e["enabled"])))
            .Where(t => t.Path is { Length: > 0 } p && !p.Contains('/', StringComparison.Ordinal) && t.Enabled is not null)
            .Select(t => new ResourceIntent(Prefix + t.Path + Suffix, JsonValue.Create(t.Enabled)))];

    public Task<ResourceRead> ReadAsync(string resourceKey, CancellationToken ct) =>
        JsonFields.KeyName(resourceKey, Prefix, Suffix) is { } path
            ? tasks.ReadEnabledAsync(path, ct)
            : Task.FromResult(ResourceRead.Failed($"not a {Id} key: {resourceKey}"));

    public bool Equivalent(string resourceKey, JsonNode? a, JsonNode? b) => JsonFields.Bool(a) is { } x && JsonFields.Bool(b) is { } y && x == y;

    public async Task<ApplyResult> ApplyAsync(ResourceIntent intent, CancellationToken ct)
    {
        if (JsonFields.KeyName(intent.Key, Prefix, Suffix) is not { } path) return ApplyResult.Failed($"not a {Id} key: {intent.Key}");
        if (JsonFields.Bool(intent.Target) is not { } want) return ApplyResult.Failed($"task '{path}': the target must be true or false");
        var before = await tasks.ReadEnabledAsync(path, ct);
        if (!before.Ok) return ApplyResult.Failed(before.Error);
        if (JsonFields.Bool(before.Value) == want) return ApplyResult.Written(JsonValue.Create(want));

        string? direct = await tasks.SetEnabledAsync(path, want, ct);
        if (direct is not null)
        {
            var r = await broker.RunAsync(want ? "task.enable" : "task.disable", new JsonObject { ["name"] = path }, ct);
            if (!r.Ok) return ApplyResult.Failed($"task '{path}': {direct}; the broker refused or failed: {r.Error}");
        }
        var after = await tasks.ReadEnabledAsync(path, ct);
        if (!after.Ok) return ApplyResult.Failed($"task '{path}' could not be read back: {after.Error}");
        return JsonFields.Bool(after.Value) == want
            ? ApplyResult.Written(JsonValue.Create(want))
            : ApplyResult.Failed($"task '{path}' reads {(want ? "disabled" : "enabled")} after the change");
    }
}
