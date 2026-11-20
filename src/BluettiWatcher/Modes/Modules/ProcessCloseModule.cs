using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// <c>process.close</c>: closes the apps the user listed (by executable name) when a mode is entered: each window is
/// asked to close, and after <c>graceSec</c> what still runs is ended only when <c>kill</c> is set. Irreversible:
/// leaving the mode starts nothing again. It never targets an app that is not listed, and never a protected one
/// (<see cref="ProcessCloser.Protected"/>). Key <c>process/{exe}/running</c>.
/// </summary>
/// <remarks>Pattern: Adapter.</remarks>
internal sealed class ProcessCloseModule(IProcessControl processes, TimeProvider time) : IActionModule
{
    public const string Id = "process.close";
    public const int DefaultGraceSec = 5;
    public const int MaxGraceSec = 60;

    private readonly ProcessCloser _closer = new(processes, time);

    public ActionModuleDescriptor Descriptor { get; } = new(
        Id, 1, "Close apps",
        JsonFields.Parse("""
            {"type":"object","additionalProperties":false,"required":["apps"],"properties":{
              "apps":{"type":"array","items":{"type":"string"}},
              "graceSec":{"type":"integer","minimum":0,"maximum":60},
              "kill":{"type":"boolean"}}}
            """),
        JsonFields.Parse("""
            {"apps":{"widget":"multiselect","list":"apps","label":"Apps to close (executable names)"},
             "graceSec":{"widget":"number","label":"Seconds to wait after asking them to close"},
             "kill":{"widget":"checkbox","label":"End what is still running after the wait (unsaved work is lost)"}}
            """),
        ["apps"], RestorationClass.Irreversible, NeedsAdmin: false,
        JsonFields.Parse("""{"apps":[],"graceSec":5,"kill":false}"""));

    public Task<ModuleAvailability> AvailableAsync(CancellationToken ct) =>
        Task.FromResult(processes.ListApps() is null ? ModuleAvailability.No("the process list cannot be read") : ModuleAvailability.Yes);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>>> DiscoverAsync(CancellationToken ct)
    {
        var lists = new Dictionary<string, IReadOnlyList<DiscoveredItem>>(StringComparer.Ordinal);
        if (processes.ListApps() is { } apps)
            lists["apps"] = [.. apps
                .Where(a => !ProcessCloser.Protected.Contains(a.ExeName))
                .GroupBy(a => a.ExeName, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new DiscoveredItem(g.Key, $"{g.Key} ({g.First().Title})"))];
        return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>>>(lists);
    }

    public IReadOnlyList<string> Validate(JsonObject settings)
    {
        var problems = new List<string>();
        if (settings["apps"] is not JsonArray apps) { problems.Add("apps must be a list"); return problems; }
        if (apps.Any(a => JsonFields.Str(a) is null)) problems.Add("every app must be an executable name");
        var names = JsonFields.Strings(apps);
        JsonFields.CheckNames(names.Select(n => ProcessCloser.Normalize(n.Trim())), "app", problems);
        foreach (var n in names.Where(n => n.Contains('\\', StringComparison.Ordinal) || n.Contains(':', StringComparison.Ordinal)))
            problems.Add($"app '{n}': an executable name, not a path");
        foreach (var n in names.Where(n => ProcessCloser.Protected.Contains(ProcessCloser.Normalize(n.Trim()))))
            problems.Add($"app '{n}' is part of Windows or this service and is never closed");
        return problems;
    }

    public IReadOnlyList<ResourceIntent> Expand(JsonObject settings)
    {
        int grace = JsonFields.Int(settings["graceSec"]) ?? DefaultGraceSec;
        bool kill = JsonFields.Bool(settings["kill"]) ?? false;
        return [.. JsonFields.Strings(settings["apps"])
            .Select(n => ProcessCloser.Normalize(n.Trim()))
            .Where(n => n.Length > 4 && !n.Contains('/', StringComparison.Ordinal) && !ProcessCloser.Protected.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => new ResourceIntent(ProcessCloser.Key(n), ProcessCloser.CloseTarget(grace, kill)))];
    }

    public Task<ResourceRead> ReadAsync(string resourceKey, CancellationToken ct) => _closer.ReadAsync(resourceKey);

    public bool Equivalent(string resourceKey, JsonNode? a, JsonNode? b) => ProcessCloser.Equivalent(a, b);

    public Task<ApplyResult> ApplyAsync(ResourceIntent intent, CancellationToken ct) => _closer.ApplyAsync(intent, ct);
}
