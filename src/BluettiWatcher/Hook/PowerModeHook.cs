using System.Text.Json;

namespace BluettiWatcher.Hook;

/// <summary>
/// Claude Code hook (SessionStart / UserPromptSubmit / SubagentStart), run as <c>BluettiWatcher.exe hook</c>: a power mode
/// the user or the watcher switches (power-mode.ps1 on | off | eco) reaches every live Claude Code session on its next
/// prompt. The hook event's JSON comes on stdin, the answer goes to stdout; integrations/claude-code/README.md shows the
/// settings.json entries.
/// <list type="bullet">
/// <item>flag: POWER_MODE_FILE, else %ProgramData%\BluettiWatcher\power-mode.json <c>{ "mode": "emergency" | "normal" |
/// "eco", "since": "&lt;ISO 8601 with offset&gt;" [, "until"] [, "source": "station"] [, "battery": { soc, at, level, mains }] }</c>;
/// a missing file = normal. An eco with <c>source: station</c> has no <c>until</c>: it lasts while mains is off. The flag's
/// folder or file being a junction or link, or a folder that cannot be listed, = unreadable. The hook writes no flag.</item>
/// <item>rules: power-mode.rules.md (emergency), power-mode.eco.rules.md, power-mode.stop.rules.md,
/// power-mode.hibernate.rules.md, built into the exe from integrations/claude-code; missing = header only.</item>
/// <item>battery: the watcher's reading, heard while younger than 15 minutes: low -> the emergency rules, critical -> the
/// STOP text, hibernating -> "the PC hibernates at N %: save now"; with mains off the eco and emergency rules end with
/// "Mains is off, battery N %". A session hears each level once (its state file).</item>
/// <item>state: one file per session in TEMP\claude-power-mode\&lt;session&gt;.json (what it last heard), temp + rename,
/// last writer wins, no lock. A numeric id counts as its digits, one over 64 characters as h&lt;16-hex hash&gt;;
/// SessionStart removes state files older than 14 days (at most 50 a run).</item>
/// </list>
/// SessionStart (every source): emergency -> header + rules as additionalContext and the header as systemMessage; normal
/// -> nothing; an unreadable flag -> one line naming the file. Records what it said. UserPromptSubmit: speaks only when the
/// flag differs from what this session last heard; emergency -> normal -> "the emergency rules are lifted"; no record:
/// normal is recorded silently, emergency speaks; normal -> normal with another since is recorded silently; no usable
/// session id = silent. SubagentStart: emergency -> header + rules, no state. An unreadable flag is silent outside
/// SessionStart and never touches the state. Fail-open: any error = nothing on stdout, exit 0 (exit 2 would block a prompt).
/// </summary>
internal static class PowerModeHook
{
    private const long Hour = 3600 * 1000L;
    private const long MaxAgeMs = 14 * 24 * Hour;
    private const int MaxSweep = 50;
    private const int MaxStat = 100;
    private const long BatteryFreshMs = 15 * 60 * 1000L;
    private static readonly string[] Levels = ["ok", "low", "critical", "hibernating"];

    private sealed record Battery(double Soc, string Level, bool? Mains, double? HibernateAt, long Ms);

    /// <summary>The flag as read: <see cref="Ms"/> is null for a missing file; <see cref="UntilMs"/> is null for the station's eco.</summary>
    private sealed record Flag(string Mode, string Since, long? Ms, bool Station, Battery? Battery, long? UntilMs = null, long? ReplacedUntilMs = null);

    private sealed record State(string Mode, string? Since, string Level);

    /// <summary>The process entry: stdin in, stdout out, always exit 0.</summary>
    public static int RunProcess()
    {
        try
        {
            string input;
            using (var stdin = Console.OpenStandardInput())
            using (var buffer = new MemoryStream())
            {
                stdin.CopyTo(buffer);
                input = JsCompat.Utf8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            }
            string output = Run(input, HookWorld.FromProcess());
            if (output.Length == 0) return 0;
            using var stdout = Console.OpenStandardOutput();
            // No stdout handle (run by hand in a terminal, nothing redirected): the console Program.cs attached prints it.
            if (ReferenceEquals(stdout, Stream.Null))
            {
                Console.Out.Write(output);
                return 0;
            }
            stdout.Write(JsCompat.Utf8.GetBytes(output));
            stdout.Flush();
            return 0;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return 0;   // fail-open: a broken hook never blocks a prompt
        }
    }

    /// <summary>The hook for one event: the payload text in, the stdout text out ("" = say nothing).</summary>
    public static string Run(string payloadText, HookWorld world)
    {
        try { return Handle(payloadText, world); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return "";   // fail-open: nothing on stdout
        }
    }

    private static string Handle(string payloadText, HookWorld w)
    {
        using var doc = JsCompat.Parse(payloadText);
        var payload = doc.RootElement;
        string? evt = JsCompat.Str(JsCompat.Get(payload, "hook_event_name"));
        if (evt is not ("SessionStart" or "UserPromptSubmit" or "SubagentStart")) return "";
        var (file, untrusted) = FlagPath(w.FlagFile);
        var flag = untrusted ? null : ReadFlag(file, w.NowMs());
        if (flag is null)
        {
            if (evt != "SessionStart") return "";
            string line = $"power mode: flag file unreadable ({file}) - treated as normal, tell the user";
            return Say(evt, line, line);
        }
        bool emergency = VoiceOf(flag, w.NowMs()) != "normal";   // emergency, eco, or a battery level: something with rules to say
        if (evt == "SubagentStart") return emergency ? Say(evt, EmergencyText(flag, w), null) : "";
        string? key = SessionKey(JsCompat.Get(payload, "session_id"));
        if (evt == "SessionStart")
        {
            Sweep(w);
            if (key is not null) WriteState(w, key, flag);
            return emergency ? Say(evt, EmergencyText(flag, w), EmergencyHead(flag, w)) : "";
        }
        if (key is null) return "";   // no record possible: speaking would repeat on every prompt
        var prev = ReadState(w, key);
        if (prev is not null && prev.Mode == flag.Mode && prev.Since == flag.Since && prev.Level == LevelOf(flag, w.NowMs())) return "";
        WriteState(w, key, flag);
        if (emergency) return Say(evt, EmergencyText(flag, w), EmergencyHead(flag, w));
        if (prev is not null && prev.Mode != "normal") return Say(evt, NormalLine(flag, prev.Mode, w), NormalLine(flag, prev.Mode, w));
        if (prev is not null && prev.Level != "ok") return Say(evt, NormalLine(flag, "emergency", w), NormalLine(flag, "emergency", w));   // the battery level was the only thing said
        return "";
    }

    // ---- the flag

    /// <summary>
    /// The flag file and whether it is untrusted: a junction or link in place of its folder or of the file, a folder that
    /// cannot be listed, or a file that cannot be told present or absent.
    /// </summary>
    private static (string File, bool Untrusted) FlagPath(string file)
    {
        string dir = Path.GetDirectoryName(file) ?? file;
        if (Linked(Path.GetDirectoryName(dir) ?? dir, Path.GetFileName(dir))) return (file, true);
        bool present;
        try
        {
            _ = File.GetAttributes(file);
            present = true;
        }
        catch (Exception e) when (NotFound(e))
        {
            present = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (file, true);
        }
        if (present && Linked(dir, Path.GetFileName(file))) return (file, true);
        return (file, false);
    }

    /// <summary>Whether the entry <paramref name="name"/> of <paramref name="parent"/> is a reparse point; a folder that cannot be listed counts as one.</summary>
    private static bool Linked(string parent, string name)
    {
        try
        {
            foreach (var e in new DirectoryInfo(parent).EnumerateFileSystemInfos())
            {
                if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) return (e.Attributes & FileAttributes.ReparsePoint) != 0;
            }
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return true;   // cannot be told free of links: untrusted, like a link
        }
    }

    /// <summary>The errors node reads as "no such entry" (ENOENT): not found, no such path, a bad name or drive.</summary>
    private static bool NotFound(Exception e) =>
        e is FileNotFoundException or DirectoryNotFoundException or DriveNotFoundException
        || (e is IOException && (e.HResult & 0xFFFF) is 2 or 3 or 15 or 53 or 67 or 123 or 161 or 267);

    /// <summary>The flag; a missing file is normal with no since; null = the file is there and cannot be trusted.</summary>
    private static Flag? ReadFlag(string file, long now)
    {
        if (!Path.Exists(file)) return new Flag("normal", "", null, false, null);
        try
        {
            string text = JsCompat.Utf8.GetString(File.ReadAllBytes(file));
            if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
            using var doc = JsCompat.Parse(text);
            var j = doc.RootElement;
            string? mode = JsCompat.Str(JsCompat.Get(j, "mode"));
            if (mode is not ("emergency" or "normal" or "eco")) return null;
            string since = JsCompat.ToJsString(JsCompat.Get(j, "since"));
            if (JsCompat.ParseSince(since) is not { } ms) return null;
            bool station = JsCompat.Str(JsCompat.Get(j, "source")) == "station";
            var battery = ReadBattery(JsCompat.Get(j, "battery"));
            // eco carries `until`: past it the flag reads as normal since that instant. The station's eco has none.
            if (mode == "eco")
            {
                var untilValue = JsCompat.Get(j, "until");
                if (station && untilValue is null) return new Flag("eco", since, ms, station, battery);
                string until = JsCompat.ToJsString(untilValue);
                if (JsCompat.ParseSince(until) is not { } untilMs) return null;
                if (now >= untilMs) return new Flag("normal", until, untilMs, false, battery);
                return new Flag("eco", since, ms, station, battery, UntilMs: untilMs);
            }
            // The user's eco the station's emergency replaced: its end time is still named while on battery.
            long? replacedUntil = mode == "emergency" && JsCompat.Get(j, "replaced") is { ValueKind: JsonValueKind.Object } replaced
                ? JsCompat.ParseSince(JsCompat.ToJsString(JsCompat.Get(replaced, "until")))
                : null;
            return new Flag(mode, since, ms, station, battery, ReplacedUntilMs: replacedUntil);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>The station's battery as the poller wrote it; null when absent or malformed.</summary>
    private static Battery? ReadBattery(JsonElement? b)
    {
        if (b is not { ValueKind: JsonValueKind.Object }) return null;
        if (JsCompat.Num(JsCompat.Get(b, "soc")) is not { } soc || !(soc >= 0 && soc <= 100)) return null;
        string? level = JsCompat.Str(JsCompat.Get(b, "level"));
        if (level is null || Array.IndexOf(Levels, level) < 0) return null;
        if (JsCompat.ParseSince(JsCompat.ToJsString(JsCompat.Get(b, "at"))) is not { } ms) return null;
        double? hibernateAt = JsCompat.Num(JsCompat.Get(b, "hibernateAt")) is { } h && h >= 0 && h <= 100 ? h : null;
        bool? mains = JsCompat.Get(b, "mains") is { } m && m.ValueKind is JsonValueKind.True or JsonValueKind.False ? m.GetBoolean() : null;
        return new Battery(soc, level, mains, hibernateAt, ms);
    }

    /// <summary>A reading older than 15 minutes says nothing: the poller stopped, and a stale STOP must not outlive it.</summary>
    private static Battery? FreshBattery(Flag flag, long now) =>
        flag.Battery is { } b && Math.Abs(now - b.Ms) <= BatteryFreshMs ? b : null;

    /// <summary>Mains on: no battery level is heard, whatever the reading says - STOP and hibernate are for a cut only.</summary>
    private static string LevelOf(Flag flag, long now) => FreshBattery(flag, now) is { } b && b.Mains != true ? b.Level : "ok";

    /// <summary>What the session must hear: the battery level outranks the mode (low = emergency, critical = stop, hibernating = hibernate).</summary>
    private static string VoiceOf(Flag flag, long now) => LevelOf(flag, now) switch
    {
        "low" => "emergency",
        "critical" => "stop",
        "hibernating" => "hibernate",
        _ => flag.Mode,
    };

    private static string ReadRules(string voice, HookWorld w)
    {
        string name = voice switch
        {
            "eco" => "power-mode.eco.rules.md",
            "stop" => "power-mode.stop.rules.md",
            "hibernate" => "power-mode.hibernate.rules.md",
            _ => "power-mode.rules.md",
        };
        return w.Rules(name) is { } text ? JsCompat.Trim(text.Replace("\r\n", "\n", StringComparison.Ordinal)) : "";
    }

    // ---- the texts

    private static string Pct(Battery b) => $"battery {JsCompat.Hundredths(b.Soc)} %";

    private static string MainsOff(Battery b) => b.Mains == false ? ", mains is off" : "";

    private static string EmergencyHead(Flag flag, HookWorld w)
    {
        long now = w.NowMs();
        string voice = VoiceOf(flag, now);
        var b = FreshBattery(flag, now);
        if (voice == "hibernate") return $"power mode: {Pct(b!)}{MainsOff(b!)} - the PC hibernates {(b!.HibernateAt is { } at ? $"at {JsCompat.Hundredths(at)} %" : "soon")}: save now";
        if (voice == "stop") return $"power mode: STOP - {Pct(b!)}{MainsOff(b!)}";
        if (flag.Mode == "eco" && voice == "eco")
        {
            return flag.UntilMs is { } until
                ? $"power mode: ECO since {Local(flag.Ms, w)} until {JsCompat.LocalTime(until, w.Zone)}"
                : $"power mode: ECO since {Local(flag.Ms, w)} while mains is off";
        }
        if (flag.Mode == "emergency")
        {
            string over = flag.ReplacedUntilMs is { } r && r != 0 && r > now ? $" (over the user's eco until {JsCompat.LocalTime(r, w.Zone)})" : "";
            return $"power mode: EMERGENCY since {Local(flag.Ms, w)}{over}";
        }
        return $"power mode: EMERGENCY - {Pct(b!)}{MainsOff(b!)}";   // battery low while the mode itself is not emergency
    }

    private static string Local(long? ms, HookWorld w) => JsCompat.LocalTime(ms!.Value, w.Zone);

    private static string NormalLine(Flag flag, string was, HookWorld w) =>
        $"power mode: normal{(flag.Ms is { } ms ? $" since {JsCompat.LocalTime(ms, w.Zone)}" : "")} - the {(was == "eco" ? "eco" : "emergency")} rules are lifted";

    private static string EmergencyText(Flag flag, HookWorld w)
    {
        long now = w.NowMs();
        string voice = VoiceOf(flag, now);
        string rules = ReadRules(voice, w);
        var b = FreshBattery(flag, now);
        // The eco and emergency rules get the station's reading as one more line; the STOP and hibernate heads carry it.
        string mains = b is not null && b.Mains == false && voice is "eco" or "emergency" ? $"\n- Mains is off, {Pct(b)}." : "";
        return rules.Length > 0 ? $"{EmergencyHead(flag, w)}\n{rules}{mains}" : $"{EmergencyHead(flag, w)}{mains}";
    }

    private static string Say(string evt, string context, string? systemMessage) =>
        $"{{\"hookSpecificOutput\":{{\"hookEventName\":{JsCompat.Quote(evt)},\"additionalContext\":{JsCompat.Quote(context)}}}"
        + (string.IsNullOrEmpty(systemMessage) ? "" : $",\"systemMessage\":{JsCompat.Quote(systemMessage)}") + "}";

    // ---- the per-session state

    private static string StateDir(HookWorld w) => Path.Join(w.TempDir, "claude-power-mode");

    private static string StateFile(HookWorld w, string key) => Path.Join(StateDir(w), key + ".json");

    /// <summary>Two 32-bit FNV-1a passes with different offsets: 16 hex characters, the same id always the same.</summary>
    internal static string ShortHash(string text)
    {
        uint a = 0x811c9dc5;
        uint b = 0x01000193 ^ 0x9e3779b9;
        foreach (char c in text)
        {
            a = unchecked((a ^ c) * 0x01000193);
            b = unchecked((b ^ c) * 0x01000193);
        }
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{a:x8}{b:x8}");
    }

    /// <summary>
    /// The state key of a session id: a number as its digits (JavaScript's String), an id over 64 characters as h&lt;hash&gt;,
    /// else the id with every character outside [A-Za-z0-9_-] as <c>_</c>; null for a missing, empty or non-string id.
    /// </summary>
    internal static string? SessionKey(JsonElement? id)
    {
        string? s = JsCompat.Num(id) is { } n && double.IsFinite(n) ? JsCompat.NumberToString(n) : JsCompat.Str(id);
        if (string.IsNullOrEmpty(s)) return null;
        if (s.Length > 64) return "h" + ShortHash(s);
        return string.Create(s.Length, s, static (span, src) =>
        {
            for (int i = 0; i < src.Length; i++) span[i] = IsKeyChar(src[i]) ? src[i] : '_';
        });
    }

    private static bool IsKeyChar(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-';

    /// <summary>A state file's name: 1 to 64 of [A-Za-z0-9_-], then .json.</summary>
    private static bool IsStateName(string name) =>
        name.Length is > 5 and <= 69 && name.EndsWith(".json", StringComparison.Ordinal) && name[..^5].All(IsKeyChar);

    /// <summary>
    /// SessionStart only: removes this hook's state files older than 14 days, at most 50 a run; plain files of the exact
    /// name shape only, never a link; every error ignored. At most 100 files are looked at per run, from a random start.
    /// </summary>
    private static void Sweep(HookWorld w)
    {
        long now = w.NowMs();
        string dir = StateDir(w);
        List<string> names;
        try
        {
            names = [.. new DirectoryInfo(dir).EnumerateFileSystemInfos()
                .Where(e => IsStateName(e.Name) && (e.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0)
                .Select(e => e.Name)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return;   // no state directory: no state, no sweep
        }
        if (names.Count == 0) return;
        // lift-verified 2026-10-14 CA5394 fires without it; the start only spreads the sweep, no security role.
#pragma warning disable CA5394
        int start = Random.Shared.Next(names.Count);
#pragma warning restore CA5394
        int removed = 0;
        for (int i = 0; i < Math.Min(names.Count, MaxStat) && removed < MaxSweep; i++)
        {
            try
            {
                var f = new FileInfo(Path.Join(dir, names[(start + i) % names.Count]));
                if (!f.Exists) continue;
                if (now - f.LastWriteTimeUtc.Subtract(DateTime.UnixEpoch).TotalMilliseconds > MaxAgeMs)
                {
                    f.Delete();
                    removed++;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;   // the next one
            }
        }
    }

    private static State? ReadState(HookWorld w, string key)
    {
        try
        {
            using var doc = JsCompat.Parse(JsCompat.Utf8.GetString(File.ReadAllBytes(StateFile(w, key))));
            var j = doc.RootElement;
            if (JsCompat.Str(JsCompat.Get(j, "mode")) is not { } mode) return null;
            string? level = JsCompat.Str(JsCompat.Get(j, "level"));
            return new State(mode, JsCompat.Str(JsCompat.Get(j, "since")), level is not null && Array.IndexOf(Levels, level) > 0 ? level : "ok");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static void WriteState(HookWorld w, string key, Flag flag)
    {
        string file = StateFile(w, key);
        string tmp = $"{file}.{Environment.ProcessId}.{w.NowMs()}.tmp";
        try
        {
            Directory.CreateDirectory(StateDir(w));
            string level = LevelOf(flag, w.NowMs());
            string json = $"{{\"mode\":{JsCompat.Quote(flag.Mode)},\"since\":{JsCompat.Quote(flag.Since)}"
                + (level == "ok" ? "" : $",\"level\":{JsCompat.Quote(level)}") + "}";
            File.WriteAllBytes(tmp, JsCompat.Utf8.GetBytes(json));
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            DropTemp(tmp);
        }
    }

    /// <summary>Removes a temp file a failed write left; File.Delete of a missing file is no error.</summary>
    private static void DropTemp(string tmp)
    {
        try { File.Delete(tmp); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;   // locked: the next write replaces the record anyway
        }
    }
}
