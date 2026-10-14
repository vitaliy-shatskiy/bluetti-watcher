using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BluettiWatcher.Hook;

namespace BluettiWatcher.Tests;

/// <summary>
/// The Claude Code hook (BluettiWatcher.exe hook) through its entry: the payload text in, the stdout text out; the flag and
/// the per-session state (TEMP) live in a private temp directory per test. A port of power-mode.test.js, one test per
/// JavaScript test and under its name, so the two map 1:1.
/// </summary>
public sealed partial class HookPowerModeTests
{
    internal static readonly string Repo = FindRepo();
    private static readonly string RulesText = Source("power-mode.rules.md");
    private const string Since = "2026-10-03T01:20:00+03:00";
    private const string Head = "power mode: EMERGENCY since 03.10.2026 01:20";
    private static readonly string Full = $"{Head}\n{RulesText}";
    private const string Bom = "\uFEFF";
    private static readonly TimeZoneInfo Kyiv = TimeZoneInfo.FindSystemTimeZoneById("FLE Standard Time");
    private static readonly UTF8Encoding Utf8 = new(false);

    internal static string FindRepo()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Join(d.FullName, "bluetti-watcher.slnx"))) return d.FullName;
        }
        throw new InvalidOperationException("no bluetti-watcher.slnx above the test binaries");
    }

    /// <summary>A rules file as the JavaScript test read it: CRLF as LF, trimmed.</summary>
    internal static string Source(string name) =>
        File.ReadAllText(Path.Join(Repo, "integrations", "claude-code", name)).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    /// <summary>A private world: its own TEMP (state files) and its own flag file.</summary>
    private sealed class World : IDisposable
    {
        public string Dir { get; } = Directory.CreateTempSubdirectory("power-mode-test-").FullName;
        public string Flag => Path.Join(Dir, "flag", "power-mode.json");
        public string Temp => Path.Join(Dir, "temp");
        public string Sdir => Path.Join(Temp, "claude-power-mode");
        public TimeZoneInfo Tz { get; set; } = Kyiv;
        public Func<string, string?> Rules { get; set; } = HookWorld.EmbeddedRules;
        public string? FlagEnv { get; set; }
        public string? ProgramData { get; set; }

        public World()
        {
            Directory.CreateDirectory(Temp);
            Directory.CreateDirectory(Path.GetDirectoryName(Flag)!);
            FlagEnv = Flag;
        }

        public void Set(string mode, string since = Since) => Raw($"{{\"mode\":\"{mode}\",\"since\":\"{since}\"}}");

        public void Raw(string text) => File.WriteAllText(Flag, text, Utf8);

        public void Clear() => File.Delete(Flag);

        public string? State(string sid)
        {
            string p = Path.Join(Sdir, sid + ".json");
            return File.Exists(p) ? File.ReadAllText(p, Utf8) : null;
        }

        public string[] Files() => Directory.Exists(Sdir) ? Names(Sdir) : [];

        /// <summary>The event with a session id (null = none) and more JSON members (<paramref name="extra"/>: <c>"k":v</c>).</summary>
        public string Fire(string evt, string? sid = "s1", string extra = "")
        {
            var parts = new List<string> { $"\"hook_event_name\":{JsCompat.Quote(evt)}" };
            if (sid is not null) parts.Add($"\"session_id\":{JsCompat.Quote(sid)}");
            if (extra.Length > 0) parts.Add(extra);
            return Stdin("{" + string.Join(',', parts) + "}");
        }

        public string Stdin(string text) => PowerModeHook.Run(text, new HookWorld(
            HookWorld.FlagFileOf(FlagEnv, ProgramData, null), Temp, Tz, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Rules));

        public void Dispose() => Directory.Delete(Dir, recursive: true);
    }

    private static string[] Names(string dir) =>
        [.. new DirectoryInfo(dir).EnumerateFileSystemInfos().Select(e => e.Name).Order(StringComparer.Ordinal)];

    private sealed record Said(string Context, string? System, string[] Keys);

    private static Said Parse(string stdout, string evt)
    {
        Assert.NotEqual("", stdout);
        Assert.Equal('{', stdout[0]);
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        var hso = root.GetProperty("hookSpecificOutput");
        Assert.Equal(evt, hso.GetProperty("hookEventName").GetString());
        Assert.Equal(["hookEventName", "additionalContext"], hso.EnumerateObject().Select(p => p.Name).ToArray());
        string? system = root.TryGetProperty("systemMessage", out var s) ? s.GetString() : null;
        return new Said(hso.GetProperty("additionalContext").GetString()!, system, [.. root.EnumerateObject().Select(p => p.Name)]);
    }

    private static string StateJson(string mode, string since) => $"{{\"mode\":\"{mode}\",\"since\":\"{since}\"}}";

    // ---- SessionStart

    [Theory]
    [InlineData("startup")]
    [InlineData("resume")]
    [InlineData("clear")]
    [InlineData("compact")]
    public void SessionStart_emergency_header_plus_rules_in_context_the_header_for_the_user_recorded(string source)
    {
        using var w = new World();
        w.Set("emergency");
        var o = Parse(w.Fire("SessionStart", "s1", $"\"source\":\"{source}\""), "SessionStart");
        Assert.Equal(Full, o.Context);
        Assert.Equal(Head, o.System);
        Assert.Equal(StateJson("emergency", Since), w.State("s1"));
    }

    [Fact]
    public void SessionStart_normal_no_output_at_all_for_a_missing_flag_and_for_a_normal_one_the_mode_is_recorded()
    {
        using var w = new World();
        Assert.Equal("", w.Fire("SessionStart"));
        Assert.Equal(StateJson("normal", ""), w.State("s1"));
        w.Set("normal");
        Assert.Equal("", w.Fire("SessionStart", "s2"));
        Assert.Equal(StateJson("normal", Since), w.State("s2"));
    }

    [Theory]
    [InlineData("not JSON", "{not json")]
    [InlineData("an empty file", "")]
    [InlineData("a JSON null", "null")]
    [InlineData("an array", "[{\"mode\":\"emergency\",\"since\":\"2026-10-03T01:20:00+03:00\"}]")]
    [InlineData("an unknown mode", "{\"mode\":\"on\",\"since\":\"2026-10-03T01:20:00+03:00\"}")]
    [InlineData("a mode in another case", "{\"mode\":\"EMERGENCY\",\"since\":\"2026-10-03T01:20:00+03:00\"}")]
    [InlineData("no since", "{\"mode\":\"emergency\"}")]
    [InlineData("a since without an offset", "{\"mode\":\"emergency\",\"since\":\"2026-10-03T01:20:00\"}")]
    [InlineData("a since on a day that does not exist", "{\"mode\":\"emergency\",\"since\":\"2026-02-30T01:20:00+02:00\"}")]
    [InlineData("a since that is not a date", "{\"mode\":\"normal\",\"since\":\"yesterday\"}")]
    public void Corrupt_flag_SessionStart_says_one_line_naming_the_file_the_other_events_stay_silent_no_state(string label, string text)
    {
        using var w = new World();
        w.Raw(text);
        var o = Parse(w.Fire("SessionStart"), "SessionStart");
        string line = $"power mode: flag file unreadable ({w.Flag}) - treated as normal, tell the user";
        Assert.Equal(line, o.Context);
        Assert.Equal(line, o.System);
        Assert.Equal("", w.Fire("UserPromptSubmit"));
        Assert.Equal("", w.Fire("SubagentStart"));
        Assert.True(w.Files().Length == 0, label);
    }

    // ---- UserPromptSubmit

    [Fact]
    public void UserPromptSubmit_no_record_normal_silent_and_recorded()
    {
        using var w = new World();
        Assert.Equal("", w.Fire("UserPromptSubmit"));
        Assert.Equal(StateJson("normal", ""), w.State("s1"));
    }

    [Fact]
    public void UserPromptSubmit_no_record_emergency_announces_the_same_prompt_again_is_silent()
    {
        using var w = new World();
        w.Set("emergency");
        var o = Parse(w.Fire("UserPromptSubmit"), "UserPromptSubmit");
        Assert.Equal(Full, o.Context);
        Assert.Equal(Head, o.System);
        Assert.Equal(["hookSpecificOutput", "systemMessage"], o.Keys);
        Assert.Equal("", w.Fire("UserPromptSubmit"));
        Assert.Equal("", w.Fire("UserPromptSubmit"));
    }

    [Fact]
    public void Change_normal_to_emergency_reaches_a_live_session_on_its_next_prompt_once()
    {
        using var w = new World();
        Assert.Equal("", w.Fire("SessionStart"));
        Assert.Equal("", w.Fire("UserPromptSubmit"));
        w.Set("emergency");
        Assert.Equal(Full, Parse(w.Fire("UserPromptSubmit"), "UserPromptSubmit").Context);
        Assert.Equal("", w.Fire("UserPromptSubmit"));
    }

    [Fact]
    public void Change_emergency_to_normal_one_line_that_the_rules_are_lifted_in_both_channels_once()
    {
        using var w = new World();
        w.Set("emergency");
        Parse(w.Fire("SessionStart"), "SessionStart");
        Assert.Equal("", w.Fire("UserPromptSubmit"));
        w.Set("normal", "2026-10-03T09:05:00+03:00");
        var o = Parse(w.Fire("UserPromptSubmit"), "UserPromptSubmit");
        const string line = "power mode: normal since 03.10.2026 09:05 - the emergency rules are lifted";
        Assert.Equal(line, o.Context);
        Assert.Equal(line, o.System);
        Assert.Equal("", w.Fire("UserPromptSubmit"));
        Assert.Equal(StateJson("normal", "2026-10-03T09:05:00+03:00"), w.State("s1"));
    }

    [Fact]
    public void Emergency_then_the_flag_file_removed_lifted_with_no_since_to_name()
    {
        using var w = new World();
        w.Set("emergency");
        Parse(w.Fire("UserPromptSubmit"), "UserPromptSubmit");
        w.Clear();
        Assert.Equal("power mode: normal - the emergency rules are lifted", Parse(w.Fire("UserPromptSubmit"), "UserPromptSubmit").Context);
        Assert.Equal("", w.Fire("UserPromptSubmit"));
    }

    [Fact]
    public void Emergency_switched_off_and_on_again_between_two_prompts_the_new_since_is_announced()
    {
        using var w = new World();
        w.Set("emergency");
        Parse(w.Fire("UserPromptSubmit"), "UserPromptSubmit");
        w.Set("emergency", "2026-10-04T18:00:00+03:00");
        Assert.Equal("power mode: EMERGENCY since 04.10.2026 18:00", Parse(w.Fire("UserPromptSubmit"), "UserPromptSubmit").System);
        Assert.Equal("", w.Fire("UserPromptSubmit"));
    }

    [Fact]
    public void Normal_to_normal_with_another_since_an_on_and_off_this_session_never_heard_silent_recorded()
    {
        using var w = new World();
        Assert.Equal("", w.Fire("UserPromptSubmit"));
        w.Set("normal");
        Assert.Equal("", w.Fire("UserPromptSubmit"));
        Assert.Equal(StateJson("normal", Since), w.State("s1"));
    }

    [Fact]
    public void A_hand_edit_of_the_mode_alone_the_same_since_is_a_change()
    {
        using var w = new World();
        w.Set("emergency");
        Parse(w.Fire("UserPromptSubmit"), "UserPromptSubmit");
        w.Set("normal");
        Assert.Equal("power mode: normal since 03.10.2026 01:20 - the emergency rules are lifted", Parse(w.Fire("UserPromptSubmit"), "UserPromptSubmit").Context);
    }

    [Fact]
    public void A_corrupt_flag_between_two_prompts_leaves_the_record_alone_the_same_emergency_after_it_stays_silent()
    {
        using var w = new World();
        w.Set("emergency");
        Parse(w.Fire("UserPromptSubmit"), "UserPromptSubmit");
        string? before = w.State("s1");
        w.Raw("{not json");
        Assert.Equal("", w.Fire("UserPromptSubmit"));
        Assert.Equal(before, w.State("s1"));
        w.Set("emergency");
        Assert.Equal("", w.Fire("UserPromptSubmit"));
    }

    [Fact]
    public void Two_sessions_keep_separate_state()
    {
        using var w = new World();
        w.Set("emergency");
        Assert.Equal(Full, Parse(w.Fire("UserPromptSubmit", "a"), "UserPromptSubmit").Context);
        Assert.Equal(Full, Parse(w.Fire("UserPromptSubmit", "b"), "UserPromptSubmit").Context);
        Assert.Equal("", w.Fire("UserPromptSubmit", "a"));
        w.Set("normal", "2026-10-03T09:05:00+03:00");
        Assert.EndsWith("rules are lifted", Parse(w.Fire("UserPromptSubmit", "b"), "UserPromptSubmit").Context, StringComparison.Ordinal);
        Assert.Equal("", w.Fire("UserPromptSubmit", "b"));
        Assert.EndsWith("rules are lifted", Parse(w.Fire("UserPromptSubmit", "a"), "UserPromptSubmit").Context, StringComparison.Ordinal);
        Assert.Equal(["a.json", "b.json"], w.Files());
    }

    [Fact]
    public void An_unreadable_state_file_counts_as_no_record_emergency_speaks_normal_stays_silent()
    {
        using var w = new World();
        Directory.CreateDirectory(w.Sdir);
        File.WriteAllText(Path.Join(w.Sdir, "s1.json"), "{not json");
        Assert.Equal("", w.Fire("UserPromptSubmit"));
        File.WriteAllText(Path.Join(w.Sdir, "s1.json"), "{not json");
        w.Set("emergency");
        Assert.Equal(Full, Parse(w.Fire("UserPromptSubmit"), "UserPromptSubmit").Context);
    }

    [Theory]
    [InlineData("missing", "")]
    [InlineData("empty", "\"session_id\":\"\"")]
    [InlineData("an object", "\"session_id\":{\"id\":\"s1\"}")]
    [InlineData("a boolean", "\"session_id\":true")]
    [InlineData("null", "\"session_id\":null")]
    public void Session_id_UserPromptSubmit_stays_silent_SessionStart_and_SubagentStart_still_speak_nothing_recorded(string label, string extra)
    {
        using var w = new World();
        w.Set("emergency");
        ArgumentNullException.ThrowIfNull(extra);
        string Fire(string evt) => w.Stdin($"{{\"hook_event_name\":\"{evt}\"{(extra.Length > 0 ? "," + extra : "")}}}");
        Assert.Equal("", Fire("UserPromptSubmit"));
        Assert.Equal("", Fire("UserPromptSubmit"));
        Assert.Equal(Full, Parse(Fire("SessionStart"), "SessionStart").Context);
        Assert.Equal(Full, Parse(Fire("SubagentStart"), "SubagentStart").Context);
        Assert.True(w.Files().Length == 0, label);
    }

    [Fact]
    public void A_numeric_session_id_counts_as_its_digits_one_record_a_repeat_is_silent()
    {
        using var w = new World();
        w.Set("emergency");
        string Fire() => w.Stdin("{\"hook_event_name\":\"UserPromptSubmit\",\"session_id\":12345}");
        Assert.Equal(Full, Parse(Fire(), "UserPromptSubmit").Context);
        Assert.Equal("", Fire());
        Assert.Equal("", w.Stdin("{\"hook_event_name\":\"UserPromptSubmit\",\"session_id\":\"12345\"}"));
        Assert.Equal(["12345.json"], w.Files());
    }

    [Fact]
    public void A_session_id_over_64_characters_maps_to_a_short_hash_the_same_id_the_same_file_a_repeat_silent()
    {
        using var w = new World();
        w.Set("emergency");
        string long1 = new('x', 300);
        string long2 = new string('x', 299) + "y";
        string id64 = new('a', 64);
        Assert.Equal(Full, Parse(w.Fire("UserPromptSubmit", long1), "UserPromptSubmit").Context);
        Assert.Equal("", w.Fire("UserPromptSubmit", long1));
        var files = w.Files();
        Assert.Single(files);
        Assert.Matches(HashName(), files[0]);
        Parse(w.Fire("UserPromptSubmit", long2), "UserPromptSubmit");
        Assert.Equal(2, w.Files().Length);
        Assert.Equal("", w.Fire("UserPromptSubmit", long2));
        Parse(w.Fire("UserPromptSubmit", id64), "UserPromptSubmit");
        Assert.Contains($"{id64}.json", w.Files());
    }

    [GeneratedRegex(@"^h[0-9a-f]{16}\.json\z")]
    private static partial Regex HashName();

    // ---- the SessionStart sweep of old state files

    private static string Aged(World w, string name, int days, bool dir = false, string? root = null)
    {
        root ??= w.Sdir;
        Directory.CreateDirectory(root);
        string p = Path.Join(root, name);
        var at = DateTime.UtcNow.AddDays(-days);
        if (dir)
        {
            Directory.CreateDirectory(p);
            Directory.SetLastWriteTimeUtc(p, at);
            Directory.SetLastAccessTimeUtc(p, at);
        }
        else
        {
            File.WriteAllText(p, "{\"mode\":\"normal\",\"since\":\"\"}");
            File.SetLastWriteTimeUtc(p, at);
            File.SetLastAccessTimeUtc(p, at);
        }
        return p;
    }

    [Fact]
    public void SessionStart_removes_this_hooks_state_files_older_than_14_days_and_nothing_else()
    {
        using var w = new World();
        Aged(w, "old.json", 15);
        Aged(w, "young.json", 13);
        Aged(w, "old.json.bak", 30);
        Aged(w, ".json", 30);
        Aged(w, "a.b.json", 30);
        Aged(w, "dir.json", 30, dir: true);
        Assert.Equal("", w.Fire("UserPromptSubmit", "u1"));
        Assert.Contains("old.json", w.Files());   // UserPromptSubmit never sweeps
        Assert.Equal("", w.Fire("SubagentStart"));
        Assert.Contains("old.json", w.Files());   // SubagentStart never sweeps
        Assert.Equal("", w.Fire("SessionStart", "s9"));
        Assert.Equal([".json", "a.b.json", "dir.json", "old.json.bak", "s9.json", "u1.json", "young.json"], w.Files());
    }

    [Fact]
    public void The_sweep_lists_only_its_own_directory_never_the_TEMP_root()
    {
        using var w = new World();
        Aged(w, "old.json", 30);
        Aged(w, "old.json", 30, root: w.Temp);
        Aged(w, "claude-power-mode-old.json", 30, root: w.Temp);
        w.Fire("SessionStart", null);
        Assert.Empty(w.Files());
        Assert.Equal(["claude-power-mode", "claude-power-mode-old.json", "old.json"], Names(w.Temp));
    }

    [Fact]
    public void No_state_directory_SessionStart_sweeps_nothing_and_creates_it_only_to_write_its_record()
    {
        using var w = new World();
        w.Set("emergency");
        Parse(w.Fire("SessionStart", null), "SessionStart");
        Assert.Empty(Names(w.Temp));
        Parse(w.Fire("SessionStart", "s1"), "SessionStart");
        Assert.Equal(["s1.json"], w.Files());
    }

    [Fact]
    public void The_sweep_removes_at_most_50_files_a_run()
    {
        using var w = new World();
        for (int i = 0; i < 60; i++) Aged(w, $"old{i}.json", 20);
        w.Fire("SessionStart", null);
        Assert.Equal(10, w.Files().Length);
        w.Fire("SessionStart", null);
        Assert.Empty(w.Files());
    }

    [Fact]
    public void The_sweep_never_follows_or_removes_a_link()
    {
        using var w = new World();
        string target = Path.Join(w.Dir, "target.json");
        File.WriteAllText(target, "keep");
        Directory.CreateDirectory(w.Sdir);
        string link = Path.Join(w.Sdir, "link.json");
        try { File.CreateSymbolicLink(link, target); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;   // no symlink privilege here: the JavaScript test skips too
        }
        var at = DateTime.UtcNow.AddDays(-30);
        File.SetLastWriteTimeUtc(link, at);
        File.SetLastWriteTimeUtc(target, at);
        w.Fire("SessionStart", null);
        Assert.Equal(["link.json"], w.Files());
        Assert.Equal("keep", File.ReadAllText(target));
    }

    [Fact]
    public void A_session_id_with_path_characters_cannot_leave_TEMP()
    {
        using var w = new World();
        w.Set("emergency");
        Parse(w.Fire("UserPromptSubmit", "..\\..\\x/y"), "UserPromptSubmit");
        Assert.Equal(["______x_y.json"], w.Files());
    }

    // ---- SubagentStart

    [Fact]
    public void SubagentStart_emergency_header_plus_rules_as_context_only_normal_nothing_never_any_state()
    {
        using var w = new World();
        Assert.Equal("", w.Fire("SubagentStart", "s1", "\"agent_id\":\"a1\",\"agent_type\":\"Explore\""));
        w.Set("normal");
        Assert.Equal("", w.Fire("SubagentStart"));
        w.Set("emergency");
        for (int i = 0; i < 2; i++)
        {
            var o = Parse(w.Fire("SubagentStart"), "SubagentStart");
            Assert.Equal(Full, o.Context);
            Assert.Equal(["hookSpecificOutput"], o.Keys);
        }
        Assert.Empty(w.Files());
    }

    // ---- the rules file, the time, the size

    [Fact]
    public void A_missing_rules_file_leaves_the_header_alone_on_every_event()
    {
        using var w = new World { Rules = _ => null };
        w.Set("emergency");
        foreach (var evt in new[] { "SessionStart", "UserPromptSubmit", "SubagentStart" })
        {
            Assert.Equal(Head, Parse(w.Fire(evt, $"s-{evt}"), evt).Context);
        }
    }

    /// <summary>JavaScript: "the rules file is read at run time, CRLF and a BOM tolerated"; the texts are built into the exe now.</summary>
    [Fact]
    public void The_rules_text_CRLF_and_a_BOM_tolerated()
    {
        using var w = new World { Rules = name => name == "power-mode.rules.md" ? $"{Bom}- one\r\n- two\r\n\r\n" : null };
        w.Set("emergency");
        Assert.Equal($"{Head}\n- one\n- two", Parse(w.Fire("SubagentStart"), "SubagentStart").Context);
    }

    [Theory]
    [InlineData("2026-10-02T22:20:00Z", "03.10.2026 01:20")]          // UTC, summer: +3, the local date a day ahead
    [InlineData("2026-10-02T18:20:00-04:00", "03.10.2026 01:20")]     // a negative offset
    [InlineData("2026-10-03T03:50:00+05:30", "03.10.2026 01:20")]     // an offset with minutes
    [InlineData("2026-12-01T10:00:00+00:00", "01.12.2026 12:00")]     // winter: +2
    [InlineData("2026-10-25T00:59:59Z", "25.10.2026 03:59")]          // the last second of summer time
    [InlineData("2026-10-25T01:00:00Z", "25.10.2026 03:00")]          // the first of winter time
    [InlineData("2026-12-31T22:30Z", "01.01.2027 00:30")]             // no seconds, a year rollover
    [InlineData("2026-10-03T01:20:00.123+03:00", "03.10.2026 01:20")] // fractional seconds
    public void Since_prints_in_the_pinned_zone(string since, string want)
    {
        using var w = new World();
        w.Set("emergency", since);
        Assert.Equal($"power mode: EMERGENCY since {want}", Parse(w.Fire("SessionStart"), "SessionStart").System);
    }

    [Fact]
    public void The_emergency_output_stays_under_700_characters_with_the_shipped_rules()
    {
        using var w = new World();
        w.Set("emergency");
        var o = Parse(w.Fire("SessionStart"), "SessionStart");
        Assert.Equal(4, RulesText.Split('\n').Length);
        Assert.True(o.Context.Length + o.System!.Length < 700, (o.Context.Length + o.System.Length).ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_flag_file_with_a_BOM_reads()
    {
        using var w = new World();
        w.Raw($"{Bom}{{ \"mode\": \"emergency\", \"since\": \"{Since}\" }}\n");
        Assert.Equal(Full, Parse(w.Fire("SubagentStart"), "SubagentStart").Context);
    }

    [Fact]
    public void No_temp_file_is_left_behind()
    {
        using var w = new World();
        w.Set("emergency");
        w.Fire("SessionStart");
        w.Fire("UserPromptSubmit");
        w.Set("normal");
        w.Fire("UserPromptSubmit");
        Assert.Equal(["s1.json"], w.Files());
        Assert.Equal(["claude-power-mode"], Names(w.Temp));
        Assert.Equal(["flag", "temp"], Names(w.Dir));
        Assert.Equal(["power-mode.json"], Names(Path.GetDirectoryName(w.Flag)!));
    }

    // ---- fail-open

    [Theory]
    [InlineData("empty stdin", "")]
    [InlineData("garbage stdin", "{not json")]
    [InlineData("a JSON null payload", "null")]
    [InlineData("no event name", "{\"session_id\":\"s1\",\"prompt\":\"x\"}")]
    [InlineData("another event", "{\"session_id\":\"s1\",\"hook_event_name\":\"PreToolUse\"}")]
    public void Fail_open_silent_no_state(string label, string stdin)
    {
        using var w = new World();
        w.Set("emergency");
        Assert.Equal("", w.Stdin(stdin));
        Assert.True(w.Files().Length == 0, label);
    }

    /// <summary>
    /// JavaScript: "Jint-ready: no Intl, no locale formatting, no child process, no process.stdin". The exe's analog: the
    /// hook's code starts no process, opens no socket, and formats with no culture of the PC.
    /// </summary>
    [Fact]
    public void Exe_ready_no_locale_formatting_no_child_process_no_network()
    {
        foreach (var file in Directory.GetFiles(Path.Join(Repo, "src", "BluettiWatcher", "Hook"), "*.cs"))
        {
            string code = string.Join('\n', File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            foreach (var banned in new[] { "Process.Start", "System.Diagnostics.Process", "HttpClient", "Socket", "CurrentCulture", "ToLocalTime", "Console.In", "Console.ReadLine" })
            {
                Assert.False(code.Contains(banned, StringComparison.Ordinal), $"{Path.GetFileName(file)}: {banned}");
            }
        }
    }

    // ---- the battery station: an eco with no end time, and the battery levels

    private static readonly string EcoRules = Source("power-mode.eco.rules.md");
    private static readonly string StopRules = Source("power-mode.stop.rules.md");
    private static readonly string HibernateRules = Source("power-mode.hibernate.rules.md");

    private static string IsoNow(long agoMs = 0) =>
        DateTimeOffset.UtcNow.AddMilliseconds(-agoMs).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "+00:00";

    private static string N(double v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>A flag as the switch writes it for the poller: mode, the station as its source, the reading. mains/hibernateAt null = absent.</summary>
    private static string StationFlag(string mode, double soc, string level, bool? mains = false, long agoMs = 0, string source = "station", double? hibernateAt = 5, string extra = "")
    {
        var top = new List<string> { $"\"mode\":\"{mode}\"", $"\"since\":\"{Since}\"" };
        if (source.Length > 0) top.Add($"\"source\":\"{source}\"");
        if (extra.Length > 0) top.Add(extra);
        var battery = new List<string> { $"\"soc\":{N(soc)}", $"\"at\":\"{IsoNow(agoMs)}\"", $"\"level\":\"{level}\"" };
        if (mains is { } m) battery.Add($"\"mains\":{(m ? "true" : "false")}");
        if (hibernateAt is { } h) battery.Add($"\"hibernateAt\":{N(h)}");
        top.Add($"\"battery\":{{{string.Join(',', battery)}}}");
        return "{" + string.Join(',', top) + "}";
    }

    private const string EcoHead = "power mode: ECO since 03.10.2026 01:20 while mains is off";

    [Fact]
    public void An_eco_from_the_station_no_end_time_in_the_header_the_eco_rules_and_one_line_with_the_battery()
    {
        using var w = new World();
        w.Raw(StationFlag("eco", 87, "ok"));
        var s = Parse(w.Fire("SessionStart"), "SessionStart");
        Assert.Equal($"{EcoHead}\n{EcoRules}\n- Mains is off, battery 87 %.", s.Context);
        Assert.Equal(EcoHead, s.System);
        Assert.Equal(s.Context, Parse(w.Fire("SubagentStart"), "SubagentStart").Context);
        w.Raw($"{{\"mode\":\"eco\",\"since\":\"{Since}\",\"source\":\"station\"}}");   // no reading yet
        Assert.Equal($"{EcoHead}\n{EcoRules}", Parse(w.Fire("SessionStart", "s2"), "SessionStart").Context);
    }

    [Fact]
    public void Battery_low_the_emergency_rules_whatever_the_mode_critical_the_STOP_text_hibernating_save_now()
    {
        using var w = new World();
        w.Raw(StationFlag("emergency", 18, "low"));
        Assert.Equal($"{Head}\n{RulesText}\n- Mains is off, battery 18 %.", Parse(w.Fire("SessionStart"), "SessionStart").Context);
        w.Raw(StationFlag("normal", 18.5, "low", source: ""));   // the user switched eco off during the cut
        Assert.Equal($"power mode: EMERGENCY - battery 18.5 %, mains is off\n{RulesText}\n- Mains is off, battery 18.5 %.", Parse(w.Fire("SessionStart"), "SessionStart").Context);
        w.Raw(StationFlag("emergency", 9, "critical"));
        var stop = Parse(w.Fire("SessionStart"), "SessionStart");
        Assert.Equal($"power mode: STOP - battery 9 %, mains is off\n{StopRules}", stop.Context);
        Assert.Equal("power mode: STOP - battery 9 %, mains is off", stop.System);
        Assert.Equal(stop.Context, Parse(w.Fire("SubagentStart"), "SubagentStart").Context);
        w.Raw(StationFlag("emergency", 7, "hibernating"));
        Assert.Equal($"power mode: battery 7 %, mains is off - the PC hibernates at 5 %: save now\n{HibernateRules}", Parse(w.Fire("SessionStart"), "SessionStart").Context);
    }

    [Fact]
    public void The_battery_texts_take_mains_and_the_hibernate_threshold_from_the_reading_mains_on_silences_every_level()
    {
        using var w = new World();
        w.Raw(StationFlag("emergency", 4.5, "hibernating", hibernateAt: 4));
        Assert.Equal("power mode: battery 4.5 %, mains is off - the PC hibernates at 4 %: save now", Parse(w.Fire("SessionStart"), "SessionStart").System);
        w.Raw(StationFlag("emergency", 4.5, "hibernating", hibernateAt: null, mains: null));
        Assert.Equal("power mode: battery 4.5 % - the PC hibernates soon: save now", Parse(w.Fire("SessionStart", "s2"), "SessionStart").System);
        w.Raw(StationFlag("emergency", 9, "critical", mains: null));
        Assert.Equal("power mode: STOP - battery 9 %", Parse(w.Fire("SessionStart", "s3"), "SessionStart").System);
        foreach (var level in new[] { "critical", "hibernating" })
        {
            w.Raw(StationFlag("emergency", 9, level, mains: true));
            var s = Parse(w.Fire("SessionStart", $"s4-{level}"), "SessionStart");
            Assert.Equal(Head, s.System);   // the mode alone: no STOP, no hibernate text while mains is on
            Assert.DoesNotMatch(MainsOffWords(), s.Context);
        }
    }

    [GeneratedRegex("STOP|hibernat|[Mm]ains is off")]
    private static partial Regex MainsOffWords();

    [Fact]
    public void The_stations_emergency_over_the_users_eco_names_that_ecos_end_time()
    {
        using var w = new World();
        string Flag(string until) => StationFlag("emergency", 19, "ok", extra: $"\"replaced\":{{\"mode\":\"eco\",\"since\":\"{Since}\",\"until\":\"{until}\"}}");
        w.Raw(Flag(IsoNow(-3600000)));
        Assert.Matches(OverTheEco(), Parse(w.Fire("SessionStart"), "SessionStart").System);
        w.Raw(Flag(IsoNow(60000)));   // its end time has passed
        Assert.Equal(Head, Parse(w.Fire("SessionStart", "s2"), "SessionStart").System);
    }

    [GeneratedRegex(@"^power mode: EMERGENCY since 03\.10\.2026 01:20 \(over the user's eco until \d{2}\.\d{2}\.\d{4} \d{2}:\d{2}\)\z")]
    private static partial Regex OverTheEco();

    [Fact]
    public void UserPromptSubmit_speaks_once_per_level_change_not_per_reading_mains_back_lifts_it()
    {
        using var w = new World();
        string Prompt() => w.Fire("UserPromptSubmit");
        w.Raw(StationFlag("eco", 60, "ok"));
        Assert.Matches(@"^power mode: ECO since .* while mains is off\n", Parse(Prompt(), "UserPromptSubmit").Context);
        Assert.Equal("", Prompt());
        w.Raw(StationFlag("eco", 41, "ok"));   // another reading, the same level
        Assert.Equal("", Prompt());
        w.Raw(StationFlag("eco", 20, "low"));
        Assert.Equal($"power mode: EMERGENCY - battery 20 %, mains is off\n{RulesText}\n- Mains is off, battery 20 %.", Parse(Prompt(), "UserPromptSubmit").Context);
        w.Raw(StationFlag("eco", 19, "low"));
        Assert.Equal("", Prompt());
        w.Raw(StationFlag("eco", 10, "critical"));
        Assert.Matches(@"^power mode: STOP - battery 10 %, mains is off\n", Parse(Prompt(), "UserPromptSubmit").Context);
        Assert.Equal("", Prompt());
        w.Raw(StationFlag("eco", 7, "hibernating"));
        Assert.Matches(@"^power mode: battery 7 %, mains is off - the PC hibernates at 5 %: save now\n", Parse(Prompt(), "UserPromptSubmit").Context);
        Assert.Equal("", Prompt());
        w.Raw(StationFlag("normal", 30, "ok", mains: true, source: ""));
        Assert.Matches(@"^power mode: normal since .* - the eco rules are lifted\z", Parse(Prompt(), "UserPromptSubmit").Context);
        Assert.Equal("", Prompt());
    }

    [Fact]
    public void A_battery_level_heard_in_normal_mode_is_lifted_when_the_level_is_ok_again()
    {
        using var w = new World();
        w.Raw(StationFlag("normal", 15, "low", source: ""));
        Assert.Matches(@"^power mode: EMERGENCY - battery 15 %, mains is off\n", Parse(w.Fire("UserPromptSubmit"), "UserPromptSubmit").Context);
        w.Raw(StationFlag("normal", 40, "ok", mains: true, source: ""));
        Assert.Matches(@"^power mode: normal since .* - the emergency rules are lifted\z", Parse(w.Fire("UserPromptSubmit"), "UserPromptSubmit").Context);
        Assert.Equal("", w.Fire("UserPromptSubmit"));
    }

    [Fact]
    public void A_reading_older_than_15_minutes_says_nothing_the_mode_speaks_alone()
    {
        using var w = new World();
        w.Raw(StationFlag("eco", 9, "critical", agoMs: 16 * 60000));
        Assert.Equal($"{EcoHead}\n{EcoRules}", Parse(w.Fire("SessionStart"), "SessionStart").Context);
        w.Raw(StationFlag("normal", 9, "critical", agoMs: 16 * 60000, source: ""));
        Assert.Equal("", w.Fire("SessionStart", "s2"));
    }

    [Fact]
    public void A_malformed_battery_is_no_battery_an_eco_with_no_end_time_that_the_station_did_not_set_is_not_a_flag()
    {
        using var w = new World();
        w.Raw($"{{\"mode\":\"emergency\",\"since\":\"{Since}\",\"battery\":{{\"soc\":\"lots\",\"at\":\"x\",\"level\":\"critical\"}}}}");
        Assert.Equal(Full, Parse(w.Fire("SessionStart"), "SessionStart").Context);
        w.Raw($"{{\"mode\":\"eco\",\"since\":\"{Since}\"}}");
        Assert.StartsWith("power mode: flag file unreadable ", Parse(w.Fire("SessionStart", "s2"), "SessionStart").Context, StringComparison.Ordinal);
    }

    // ---- with no POWER_MODE_FILE the flag is %ProgramData%\BluettiWatcher\power-mode.json; the hook writes no flag. A
    // junction or link in its place, or a folder that cannot be listed, is unreadable - for a POWER_MODE_FILE path too.

    private sealed class DefaultWorld : IDisposable
    {
        private readonly World _w = new();

        public DefaultWorld()
        {
            Directory.CreateDirectory(Path.Join(Pd, "BluettiWatcher"));
            _w.ProgramData = Pd;
        }

        public string Dir => _w.Dir;
        public string Pd => Path.Join(_w.Dir, "pd");
        public string Fresh => Path.Join(Pd, "BluettiWatcher", "power-mode.json");

        public string Fire(string sid, string? flagEnv = null)
        {
            _w.FlagEnv = flagEnv;
            return _w.Stdin($"{{\"hook_event_name\":\"SessionStart\",\"session_id\":\"{sid}\",\"source\":\"startup\"}}");
        }

        public static void Set(string file, string mode) => File.WriteAllText(file, $"{{\"mode\":\"{mode}\",\"since\":\"{Since}\"}}");

        public void Dispose() => _w.Dispose();
    }

    private static (int Exit, string Out) Run(string exe, IEnumerable<string> args, IDictionary<string, string?>? env = null)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env ?? new Dictionary<string, string?>())
        {
            if (v is null) psi.Environment.Remove(k);
            else psi.Environment[k] = v;
        }
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        _ = err.Result;
        return (p.ExitCode, output);
    }

    private static string UserSid()
    {
        var (_, output) = Run("whoami", ["/user", "/fo", "csv", "/nh"]);
        return output.Trim().Split(',')[^1].Trim('"');
    }

    [Fact]
    public void No_POWER_MODE_FILE_the_ProgramData_BluettiWatcher_flag_is_read_and_nothing_is_written_there()
    {
        using var w = new DefaultWorld();
        Assert.Equal("", w.Fire("s0"));
        Assert.Empty(Names(Path.GetDirectoryName(w.Fresh)!));
        DefaultWorld.Set(w.Fresh, "emergency");
        Assert.Equal(Head, Parse(w.Fire("s1"), "SessionStart").System);
        Assert.Equal(["power-mode.json"], Names(Path.GetDirectoryName(w.Fresh)!));
    }

    [Fact]
    public void A_flag_that_cannot_be_told_present_access_denied_is_unreadable()
    {
        using var w = new DefaultWorld();
        DefaultWorld.Set(w.Fresh, "emergency");
        string dir = Path.GetDirectoryName(w.Fresh)!;
        string sid = UserSid();
        Assert.Equal(0, Run("icacls", [dir, "/deny", $"*{sid}:(OI)(CI)(RD,RA,REA,X)"]).Exit);
        string output;
        try { output = w.Fire("s1"); }
        finally { Run("icacls", [dir, "/remove:d", $"*{sid}"]); }   // before the world's own cleanup
        Assert.Matches(@"^power mode: flag file unreadable \(.*BluettiWatcher.power-mode\.json\)", Parse(output, "SessionStart").System);
    }

    [Fact]
    public void A_junction_in_place_of_the_flag_folder_is_unreadable_the_default_one_and_a_POWER_MODE_FILE_one_alike()
    {
        using var w = new DefaultWorld();
        string dir = Path.GetDirectoryName(w.Fresh)!;
        string target = dir + "-target";
        Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(target);
        Assert.Equal(0, Run("cmd", ["/d", "/c", "mklink", "/J", dir, target]).Exit);
        DefaultWorld.Set(w.Fresh, "normal");
        Assert.StartsWith("power mode: flag file unreadable (", Parse(w.Fire("s1"), "SessionStart").System, StringComparison.Ordinal);
        string other = Path.Join(w.Dir, "mine");
        Directory.CreateDirectory(other + "-target");
        Assert.Equal(0, Run("cmd", ["/d", "/c", "mklink", "/J", other, other + "-target"]).Exit);
        DefaultWorld.Set(Path.Join(other, "power-mode.json"), "normal");
        Assert.Matches(@"^power mode: flag file unreadable \(.*mine.power-mode\.json\)", Parse(w.Fire("s2", Path.Join(other, "power-mode.json")), "SessionStart").System);
        Directory.Delete(other);   // the positive control: the same file in a plain folder reads
        Directory.Move(other + "-target", other);
        Assert.Equal("", w.Fire("s3", Path.Join(other, "power-mode.json")));
        Directory.Delete(dir);   // the junction itself, before the world's recursive cleanup
    }

    [Fact]
    public void A_ProgramData_that_cannot_be_listed_cannot_be_told_free_of_links_unreadable()
    {
        using var w = new DefaultWorld();
        DefaultWorld.Set(w.Fresh, "normal");
        Assert.Equal("", w.Fire("s0"));   // the positive control
        string sid = UserSid();
        Assert.Equal(0, Run("icacls", [w.Pd, "/deny", $"*{sid}:(RD)"]).Exit);   // list only, on the folder itself: the flag stays reachable
        string output;
        try { output = w.Fire("s1"); }
        finally { Run("icacls", [w.Pd, "/remove:d", $"*{sid}"]); }   // before the world's own cleanup
        Assert.StartsWith("power mode: flag file unreadable (", Parse(output, "SessionStart").System, StringComparison.Ordinal);
    }

    [Fact]
    public void Times_print_in_the_processs_local_zone_another_TZ_another_wall_clock_no_zone_label()
    {
        using var w = new World { Tz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time") };
        w.Set("emergency", "2026-10-02T22:20:00Z");
        Assert.Equal("power mode: EMERGENCY since 02.10.2026 18:20", Parse(w.Fire("SessionStart"), "SessionStart").System);
        w.Tz = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
        Assert.Equal("power mode: EMERGENCY since 03.10.2026 07:20", Parse(w.Fire("SessionStart", "s2"), "SessionStart").System);
    }

    // The switch (power-mode/power-mode.ps1, through pwsh) writes the flag, the hook reads it. powercfg and schtasks name
    // stand-ins that do not exist and POWER_MODE_TEST=1 refuses any real tool: the switch's `on` and `off` need none.
    private static (int Status, string Out) SwitchCli(string verb, string flag)
    {
        string seam = Path.Join(Path.GetTempPath(), "power-mode-no-such-seam.ps1");
        var env = new Dictionary<string, string?>
        {
            ["POWER_MODE_FILE"] = null, ["POWER_MODE_TEST"] = "1", ["POWER_MODE_ELEVATED"] = "1", ["TZ"] = "Europe/Kyiv",
            ["POWER_MODE_TZ"] = "Europe/Kyiv,FLE Standard Time", ["POWER_MODE_POWERCFG"] = seam, ["POWER_MODE_SCHTASKS"] = seam,
            // The script forwards to the exe: name it here, never rely on another test class having set it process-wide.
            ["BLUETTI_WATCHER_EXE"] = PowerModeScript.Pm.Exe,
        };
        var (exit, output) = Run("pwsh", ["-NoProfile", "-NonInteractive", "-File", Path.Join(Repo, "power-mode", "power-mode.ps1"), verb, "-Path", flag], env);
        return (exit, output.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd());
    }

    [Fact]
    public void The_hook_reads_what_the_switch_wrote_on_announces_off_lifts()
    {
        using var w = new World();
        string Prompt()
        {
            string o = w.Fire("UserPromptSubmit");
            if (o.Length == 0) return "";
            using var doc = JsonDocument.Parse(o);
            return doc.RootElement.GetProperty("systemMessage").GetString()!;
        }
        Assert.Equal("", Prompt());
        var on = SwitchCli("on", w.Flag);
        Assert.True(on.Status == 0, on.Out);
        Assert.Equal(on.Out, Prompt());
        Assert.Equal("", Prompt());
        Assert.Equal(0, SwitchCli("off", w.Flag).Status);
        Assert.Matches(@"^power mode: normal since \d{2}\.\d{2}\.\d{4} \d{2}:\d{2} - the emergency rules are lifted\z", Prompt());
        Assert.Equal("", Prompt());
    }
}
