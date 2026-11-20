using System.Globalization;
using System.Text.Json.Nodes;
using BluettiWatcher.Io;

namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// <c>command.pair</c>: runs one command when a mode is entered and another when it is left (pause a backup task and
/// resume it, unload a local model). Each command is an executable plus an argument list (no shell, nothing
/// interpolated), started windowless, killed at <c>timeoutSec</c>; a non-zero exit is a failed write, and every run is
/// logged with its exit code and the start of its output. Key <c>command/{name}/entered</c> (bool).
/// </summary>
/// <remarks>
/// Pattern: Adapter. Compensatable: the module cannot read what the commands changed, so the value it reads is what it
/// last ran (left until the enter command succeeded). A write of <c>true</c> carries the pair
/// (<c>{ "entered": true, "enter": {...}, "leave": {...}, "timeoutSec": n }</c>) and the leave half is kept for the
/// restore, which writes the bare baseline <c>false</c>. That memory is runtime state: after a restart the pair reads
/// left again, so the leave command is not run for an enter made before the restart.
/// </remarks>
internal sealed class CommandPairModule(IProcessRunner runner, Action<string> log) : IActionModule
{
    public const string Id = "command.pair";
    public const int DefaultTimeoutSec = 30;
    public const int MaxTimeoutSec = 600;
    public const int OutputChars = 300;
    private const string Prefix = "command/";
    private const string Suffix = "/entered";

    private readonly Lock _lock = new();
    private readonly Dictionary<string, JsonObject> _entered = new(StringComparer.Ordinal);

    public ActionModuleDescriptor Descriptor { get; } = new(
        Id, 1, "Run a command on enter and on leave",
        JsonFields.Parse("""
            {"type":"object","additionalProperties":false,"required":["name","enter","leave"],"properties":{
              "name":{"type":"string"},
              "enter":{"type":"object","additionalProperties":false,"required":["exe","args"],"properties":{
                "exe":{"type":"string"},"args":{"type":"array","items":{"type":"string"}}}},
              "leave":{"type":"object","additionalProperties":false,"required":["exe","args"],"properties":{
                "exe":{"type":"string"},"args":{"type":"array","items":{"type":"string"}}}},
              "timeoutSec":{"type":"integer","minimum":1,"maximum":600}}}
            """),
        JsonFields.Parse("""
            {"name":{"widget":"text","label":"Name (unique per mode)"},
             "enter.exe":{"widget":"text","label":"On enter: executable"},
             "enter.args":{"widget":"list","label":"On enter: arguments"},
             "leave.exe":{"widget":"text","label":"On leave: executable"},
             "leave.args":{"widget":"list","label":"On leave: arguments"},
             "timeoutSec":{"widget":"number","label":"Time limit per command, seconds"}}
            """),
        [], RestorationClass.Compensatable, NeedsAdmin: false,
        JsonFields.Parse("""{"name":"command","enter":{"exe":"","args":[]},"leave":{"exe":"","args":[]},"timeoutSec":30}"""));

    public Task<ModuleAvailability> AvailableAsync(CancellationToken ct) => Task.FromResult(ModuleAvailability.Yes);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>>> DiscoverAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>>>(new Dictionary<string, IReadOnlyList<DiscoveredItem>>(StringComparer.Ordinal));

    public IReadOnlyList<string> Validate(JsonObject settings)
    {
        var problems = new List<string>();
        JsonFields.CheckNames([JsonFields.Str(settings["name"])], "command pair", problems);
        foreach (string side in (string[])["enter", "leave"])
        {
            if (settings[side] is not JsonObject c) { problems.Add($"{side} must be an object with exe and args"); continue; }
            if (JsonFields.Str(c["exe"]) is null) problems.Add($"{side}.exe must be a string (empty = no command)");
            if (c["args"] is not JsonArray args || args.Any(a => JsonFields.Str(a) is null)) problems.Add($"{side}.args must be a list of strings");
        }
        return problems;
    }

    public IReadOnlyList<ResourceIntent> Expand(JsonObject settings)
    {
        if (JsonFields.Str(settings["name"]) is not { Length: > 0 } name || name.Contains('/', StringComparison.Ordinal)) return [];
        var target = new JsonObject
        {
            ["entered"] = true,
            ["enter"] = settings["enter"]?.DeepClone(),
            ["leave"] = settings["leave"]?.DeepClone(),
            ["timeoutSec"] = JsonFields.Int(settings["timeoutSec"]) ?? DefaultTimeoutSec,
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
            if (intent.Target is not JsonObject enter) return ApplyResult.Failed($"command pair '{name}': entering needs the pair's commands");
            string? error = await RunAsync(name, "enter", enter["enter"], Timeout(enter), ct);
            if (error is not null) return ApplyResult.Failed(error);
            lock (_lock) _entered[intent.Key] = (JsonObject)enter.DeepClone();
            return ApplyResult.Written(JsonValue.Create(true));
        }

        // Leaving: the pair given with the target wins, else the one kept when it was entered.
        var source = intent.Target as JsonObject ?? pair!; // pair is non-null here: (pair is not null) != want, want is false
        string? leaveError = await RunAsync(name, "leave", source["leave"], Timeout(source), ct);
        if (leaveError is not null) return ApplyResult.Failed(leaveError);
        lock (_lock) _entered.Remove(intent.Key);
        return ApplyResult.Written(JsonValue.Create(false));
    }

    private static bool? EnteredOf(JsonNode? value) => value is JsonObject o ? JsonFields.Bool(o["entered"]) : JsonFields.Bool(value);

    private static TimeSpan Timeout(JsonObject pair) =>
        TimeSpan.FromSeconds(Math.Clamp(JsonFields.Int(pair["timeoutSec"]) ?? DefaultTimeoutSec, 1, MaxTimeoutSec));

    /// <summary>Runs one side; null when it succeeded or has no command, else the failure text (also logged).</summary>
    private async Task<string?> RunAsync(string name, string side, JsonNode? command, TimeSpan timeout, CancellationToken ct)
    {
        string exe = JsonFields.Str(command?["exe"])?.Trim() ?? "";
        if (exe.Length == 0) return null;
        IReadOnlyList<string> args = JsonFields.Strings(command?["args"]);
        string what = $"command.pair '{name}' {side}: {exe}";
        string detail;
        bool ok = false;
        try
        {
            var r = await runner.RunAsync(exe, args, timeout, ct);
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
        log($"{what} -> {detail}");
        return ok ? null : $"{what} -> {detail}";
    }
}
