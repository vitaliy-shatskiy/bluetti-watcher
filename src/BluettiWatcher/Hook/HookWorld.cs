using System.Reflection;

namespace BluettiWatcher.Hook;

/// <summary>
/// Everything the hook reads from outside its stdin: the flag's path, TEMP, the PC's zone, the clock, the rules texts.
/// <see cref="FromProcess"/> takes them from the environment; the tests build one per test.
/// </summary>
/// <param name="FlagFile">The flag: POWER_MODE_FILE made absolute, else %ProgramData%\BluettiWatcher\power-mode.json.</param>
/// <param name="TempDir">TEMP as node's os.tmpdir() reads it: TEMP, then TMP, then %SystemRoot%\temp.</param>
/// <param name="Zone">The zone times print in: POWER_MODE_TZ (comma-separated ids), then TZ, else the PC's.</param>
/// <param name="NowMs">The clock, in Unix milliseconds.</param>
/// <param name="Rules">A rules file's text by its name (power-mode.rules.md, ...); null = no such file.</param>
internal sealed record HookWorld(string FlagFile, string TempDir, TimeZoneInfo Zone, Func<long> NowMs, Func<string, string?> Rules)
{
    /// <summary>The world of this process.</summary>
    public static HookWorld FromProcess() => new(
        FlagFileOf(Env("POWER_MODE_FILE"), Env("ProgramData"), Env("ALLUSERSPROFILE")),
        TempDirOf(Env("TEMP"), Env("TMP"), Env("SystemRoot"), Env("windir")),
        ZoneOf(Env("POWER_MODE_TZ"), Env("TZ")),
        () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        EmbeddedRules);

    /// <summary>An unset and an empty variable alike are none (JavaScript's <c>||</c>).</summary>
    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    /// <summary>The configured flag file (node's path.resolve: absolute, no trailing separator), else the default one.</summary>
    internal static string FlagFileOf(string? configured, string? programData, string? allUsersProfile)
    {
        if (configured is not null)
        {
            string full = Path.GetFullPath(configured);
            string root = Path.GetPathRoot(full) ?? "";
            while (full.Length > root.Length && full[^1] is '\\' or '/') full = full[..^1];
            return full;
        }
        string pd = (programData ?? allUsersProfile ?? @"C:\ProgramData").Replace('/', '\\');
        return Path.Join(pd, "BluettiWatcher", "power-mode.json");
    }

    /// <summary>node's os.tmpdir() on Windows: TEMP, TMP, then %SystemRoot%\temp; a trailing backslash dropped unless after a drive.</summary>
    internal static string TempDirOf(string? temp, string? tmp, string? systemRoot, string? windir)
    {
        string t = temp ?? tmp ?? (systemRoot ?? windir ?? "undefined") + "\\temp";
        if (t.Length > 1 && t[^1] == '\\' && !t.EndsWith(":\\", StringComparison.Ordinal)) t = t[..^1];
        return t;
    }

    /// <summary>The first id of POWER_MODE_TZ (comma-separated), then TZ, that names a zone on this PC; else the PC's own zone.</summary>
    internal static TimeZoneInfo ZoneOf(string? powerModeTz, string? tz)
    {
        foreach (var id in (powerModeTz ?? "").Split(',').Append(tz ?? ""))
        {
            if (id.Length > 0 && TimeZoneInfo.TryFindSystemTimeZoneById(id.Trim(), out var zone)) return zone;
        }
        return TimeZoneInfo.Local;
    }

    /// <summary>The rules texts built into the exe (integrations/claude-code/*.rules.md); null for a name it does not carry.</summary>
    internal static string? EmbeddedRules(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (s is null) return null;
        using var m = new MemoryStream();
        s.CopyTo(m);
        return JsCompat.Utf8.GetString(m.GetBuffer(), 0, (int)m.Length);
    }
}
