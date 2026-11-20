using System.Collections.Frozen;
using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// The shared core of the app-closing modules: resource <c>process/{exe}/running</c>, read as a bool. A write of
/// <c>false</c> carries how to close (<c>{ "running": false, "graceSec": n, "kill": bool }</c>): the windows are asked to
/// close, and after the grace period what still runs is ended only when <c>kill</c> is set. A write of <c>true</c> never
/// starts anything (closing is irreversible): it succeeds only when the app already runs.
/// </summary>
internal sealed class ProcessCloser(IProcessControl processes, TimeProvider time)
{
    public const string Prefix = "process/";
    public const string Suffix = "/running";

    /// <summary>How long a killed process may take to go before the write counts as failed.</summary>
    public static readonly TimeSpan KillBudget = TimeSpan.FromSeconds(3);

    /// <summary>Never closed, whatever a list says: the session's own shell and system processes, and this service.</summary>
    public static readonly FrozenSet<string> Protected = new[]
    {
        "bluettiwatcher.exe", "system.exe", "registry.exe", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe",
        "lsaiso.exe", "svchost.exe", "dwm.exe", "explorer.exe", "fontdrvhost.exe", "sihost.exe", "ctfmon.exe", "conhost.exe",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static string Key(string exe) => Prefix + exe + Suffix;

    public static string? ExeOf(string key) => JsonFields.KeyName(key, Prefix, Suffix);

    /// <summary><c>name.exe</c> for <c>name</c> or <c>name.exe</c>: a list may name an app either way.</summary>
    public static string Normalize(string exe) => exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe : exe + ".exe";

    public static JsonObject CloseTarget(int graceSec, bool kill) => new() { ["running"] = false, ["graceSec"] = graceSec, ["kill"] = kill };

    /// <summary>The running flag of a value: a bare bool (a read) or a close target's <c>running</c>.</summary>
    public static bool? RunningOf(JsonNode? value) => value is JsonObject o ? JsonFields.Bool(o["running"]) : JsonFields.Bool(value);

    public Task<ResourceRead> ReadAsync(string key)
    {
        if (ExeOf(key) is not { } exe) return Task.FromResult(ResourceRead.Failed($"not a process key: {key}"));
        return Task.FromResult(processes.Count(exe) is { } n ? ResourceRead.Of(JsonValue.Create(n > 0)) : ResourceRead.Failed($"the processes of {exe} cannot be listed"));
    }

    public static bool Equivalent(JsonNode? a, JsonNode? b) => RunningOf(a) is { } x && RunningOf(b) is { } y && x == y;

    public async Task<ApplyResult> ApplyAsync(ResourceIntent intent, CancellationToken ct)
    {
        if (ExeOf(intent.Key) is not { } exe) return ApplyResult.Failed($"not a process key: {intent.Key}");
        if (Protected.Contains(Normalize(exe))) return ApplyResult.Failed($"{exe} is never closed by a mode");
        if (RunningOf(intent.Target) is not { } want) return ApplyResult.Failed($"{exe}: the target must say running true or false");
        if (processes.Count(exe) is not { } count) return ApplyResult.Failed($"the processes of {exe} cannot be listed");
        if (want) return count > 0 ? ApplyResult.Written(JsonValue.Create(true)) : ApplyResult.Failed($"{exe} is not running and closing an app is not undone: nothing is started");
        if (count == 0) return ApplyResult.Written(JsonValue.Create(false));

        var options = intent.Target as JsonObject;
        int graceSec = Math.Clamp(JsonFields.Int(options?["graceSec"]) ?? ProcessCloseModule.DefaultGraceSec, 0, ProcessCloseModule.MaxGraceSec);
        bool kill = JsonFields.Bool(options?["kill"]) ?? false;
        processes.RequestClose(exe);
        if (await GoneAsync(exe, TimeSpan.FromSeconds(graceSec), ct)) return ApplyResult.Written(JsonValue.Create(false));
        if (!kill) return ApplyResult.Failed($"{exe} still runs {graceSec} s after it was asked to close (unsaved work?); ending it is off");
        processes.Kill(exe);
        if (await GoneAsync(exe, KillBudget, ct)) return ApplyResult.Written(JsonValue.Create(false));
        return ApplyResult.Failed($"{exe} still runs after it was ended (protected, or started again)");
    }

    private Task<bool> GoneAsync(string exe, TimeSpan budget, CancellationToken ct) =>
        ModuleWait.UntilAsync(_ => Task.FromResult(processes.Count(exe) == 0), budget, time, ct);
}
