using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json.Nodes;
using BluettiWatcher.Io;

namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// <c>command.pair</c>: runs one configured command when a mode is entered and another when it is left (pause a backup
/// task and resume it, unload a local model). The commands are defined only in config.json (<see cref="CommandSpec"/>,
/// by name); the settings, and so the localhost API, can only pick names, never an executable or an argument. Each run is
/// windowless, killed at the command's time limit; a non-zero exit is a failed write, and every run is logged with its
/// exit code and the start of its output. Key <c>command/{name}/entered</c> (bool).
/// </summary>
/// <remarks>
/// Pattern: Adapter. Compensatable: the module cannot read what the commands changed, so the value it reads is what it
/// last ran (left until the enter command succeeded). A write of <c>true</c> carries the pair's command names
/// (<c>{ "entered": true, "enter": "name", "leave": "name" }</c>) and the leave name is kept for the restore, which writes
/// the bare baseline <c>false</c>. That memory is runtime state: after a restart the reconciler recalls each pair the
/// journal says is entered (<see cref="IRecallsWrites"/>), so leaving the mode still runs the leave command.
/// </remarks>
internal sealed class CommandPairModule : IActionModule, IRecallsWrites
{
    public const string Id = "command.pair";
    public const int OutputChars = 300;
    public const int MaxTimeoutSec = 600;
    private const string Prefix = "command/";
    private const string Suffix = "/entered";

    private readonly FrozenDictionary<string, CommandSpec> _commands;
    private readonly IProcessRunner _runner;
    private readonly Action<string> _log;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, JsonObject> _entered = new(StringComparer.Ordinal);

    /// <summary>config.json's named commands are copied: a later config change needs a new module.</summary>
    public CommandPairModule(IReadOnlyDictionary<string, CommandSpec> commands, IProcessRunner runner, Action<string> log)
    {
        _commands = commands.ToFrozenDictionary(StringComparer.Ordinal);
        _runner = runner;
        _log = log;
    }

    public ActionModuleDescriptor Descriptor { get; } = new(
        Id, 1, "Run a command on enter and on leave",
        JsonFields.Parse("""
            {"type":"object","additionalProperties":false,"required":["name","enter","leave"],"properties":{
              "name":{"type":"string"},
              "enter":{"type":"string"},
              "leave":{"type":"string"}}}
            """),
        JsonFields.Parse("""
            {"name":{"widget":"text","label":"Name (unique per mode)"},
             "enter":{"widget":"select","list":"commands","label":"On enter: command (empty = none)"},
             "leave":{"widget":"select","list":"commands","label":"On leave: command (empty = none)"}}
            """),
        ["commands"], RestorationClass.Compensatable, NeedsAdmin: false,
        JsonFields.Parse("""{"name":"command","enter":"","leave":""}"""));

    public Task<ModuleAvailability> AvailableAsync(CancellationToken ct) => Task.FromResult(_commands.Count > 0
        ? ModuleAvailability.Yes
        : ModuleAvailability.No("no commands are defined in config.json (modes.commands)"));

    public Task<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>>> DiscoverAsync(CancellationToken ct)
    {
        IReadOnlyList<DiscoveredItem> names = [.. _commands.Keys.Order(StringComparer.Ordinal)
            .Select(n => new DiscoveredItem(n, $"{n} ({Path.GetFileName(_commands[n].Exe)})"))];
        return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>>>(
            new Dictionary<string, IReadOnlyList<DiscoveredItem>>(StringComparer.Ordinal) { ["commands"] = names });
    }

    public IReadOnlyList<string> Validate(JsonObject settings)
    {
        var problems = new List<string>();
        JsonFields.CheckNames([JsonFields.Str(settings["name"])], "command pair", problems);
        foreach (string side in (string[])["enter", "leave"])
        {
            if (JsonFields.Str(settings[side]) is not { } command) problems.Add($"{side} must be the name of a configured command (empty = none)");
            else if (command.Length > 0 && !_commands.ContainsKey(command)) problems.Add($"{side} '{command}' is not a configured command (config.json modes.commands)");
        }
        return problems;
    }

    public IReadOnlyList<ResourceIntent> Expand(JsonObject settings)
    {
        if (JsonFields.Str(settings["name"]) is not { Length: > 0 } name || name.Contains('/', StringComparison.Ordinal)) return [];
        var target = new JsonObject
        {
            ["entered"] = true,
            ["enter"] = JsonFields.Str(settings["enter"]) ?? "",
            ["leave"] = JsonFields.Str(settings["leave"]) ?? "",
        };
        return [new ResourceIntent(Prefix + name + Suffix, target)];
    }

    public Task<ResourceRead> ReadAsync(string resourceKey, CancellationToken ct)
    {
        if (JsonFields.KeyName(resourceKey, Prefix, Suffix) is null) return Task.FromResult(ResourceRead.Failed($"not a {Id} key: {resourceKey}"));
        lock (_lock) return Task.FromResult(ResourceRead.Of(JsonValue.Create(_entered.ContainsKey(resourceKey))));
    }

    public bool Equivalent(string resourceKey, JsonNode? a, JsonNode? b) => EnteredOf(a) is { } x && EnteredOf(b) is { } y && x == y;

    public async Task<ApplyResult> ApplyAsync(ResourceIntent intent, CancellationToken ct)
    {
        if (JsonFields.KeyName(intent.Key, Prefix, Suffix) is not { } name) return ApplyResult.Failed($"not a {Id} key: {intent.Key}");
        if (EnteredOf(intent.Target) is not { } want) return ApplyResult.Failed($"command pair '{name}': the target must say entered true or false");
        JsonObject? pair;
        lock (_lock) pair = _entered.GetValueOrDefault(intent.Key);
        if ((pair is not null) == want) return ApplyResult.Written(JsonValue.Create(want));

        if (want)
        {
            if (intent.Target is not JsonObject enter) return ApplyResult.Failed($"command pair '{name}': entering needs the pair's command names");
            string? error = await RunAsync(name, "enter", enter["enter"], ct);
            if (error is not null) return ApplyResult.Failed(error);
            lock (_lock) _entered[intent.Key] = (JsonObject)enter.DeepClone();
            return ApplyResult.Written(JsonValue.Create(true));
        }

        // Leaving: the names given with the target win, else the ones kept when the pair was entered.
        var source = intent.Target as JsonObject ?? pair!; // pair is non-null here: (pair is not null) != want, want is false
        string? leaveError = await RunAsync(name, "leave", source["leave"], ct);
        if (leaveError is not null) return ApplyResult.Failed(leaveError);
        lock (_lock) _entered.Remove(intent.Key);
        return ApplyResult.Written(JsonValue.Create(false));
    }

    /// <summary>A pair the journal says was entered (the enter target with its command names) reads entered again after a restart.</summary>
    public void Recall(string resourceKey, JsonNode? target)
    {
        if (JsonFields.KeyName(resourceKey, Prefix, Suffix) is null || target is not JsonObject pair || EnteredOf(pair) != true) return;
        lock (_lock) _entered[resourceKey] = (JsonObject)pair.DeepClone();
    }

    private static bool? EnteredOf(JsonNode? value) => value is JsonObject o ? JsonFields.Bool(o["entered"]) : JsonFields.Bool(value);

    /// <summary>Runs one side by its configured name; null when it succeeded or names no command, else the failure text.</summary>
    private async Task<string?> RunAsync(string name, string side, JsonNode? commandName, CancellationToken ct)
    {
        if (JsonFields.Str(commandName) is not { } command) return $"command pair '{name}' {side}: not a command name";
        if (command.Length == 0) return null;
        if (!_commands.TryGetValue(command, out var spec)) return $"command pair '{name}' {side}: '{command}' is not a configured command";
        var timeout = TimeSpan.FromSeconds(Math.Clamp(spec.TimeoutSec, 1, MaxTimeoutSec));
        string what = $"command.pair '{name}' {side}: {command}";
        string detail;
        bool ok = false;
        try
        {
            var r = await _runner.RunAsync(spec.Exe, spec.Args, timeout, ct);
            string output = string.Join('\n', r.Lines);
            if (output.Length > OutputChars) output = output[..OutputChars];
            ok = !r.TimedOut && r.ExitCode == 0;
            detail = r.TimedOut
                ? string.Create(CultureInfo.InvariantCulture, $"killed after {timeout.TotalSeconds:0} s: {output}")
                : string.Create(CultureInfo.InvariantCulture, $"exit {r.ExitCode}: {output}");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            detail = $"could not run: {e.GetType().Name}: {e.Message}";
        }
        _log($"{what} -> {detail}");
        return ok ? null : $"{what} -> {detail}";
    }
}
