using System.Runtime.Versioning;
using BluettiWatcher.Io;
using BluettiWatcher.PowerMode;

namespace BluettiWatcher.Tests;

/// <summary>
/// The C# pieces of the power-mode switch (BluettiWatcher.exe power-mode). Every run uses a private temp flag, its own
/// mutex name, POWER_MODE_TEST=1 and stand-in seams that do not exist: no real powercfg, Task Scheduler, GPU or live flag.
/// The whole CLI contract is the PowerModeScript tests (tests/BluettiWatcher.Tests/PowerMode), through the exe and the forwarders.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PowerModeSwitchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bw-pm-switch-" + Guid.NewGuid().ToString("N"));
    private readonly string _flag;
    private readonly string _mutex = @"Local\BwPmSwitchTest" + Guid.NewGuid().ToString("N");

    public PowerModeSwitchTests()
    {
        Directory.CreateDirectory(_dir);
        _flag = Path.Combine(_dir, "power-mode.json");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private Dictionary<string, string> Env(params (string Key, string Value)[] more)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["POWER_MODE_FILE"] = Path.Combine(_dir, "configured", "power-mode.json"),
            ["POWER_MODE_MUTEX"] = _mutex,
            ["POWER_MODE_TEST"] = "1",
            ["POWER_MODE_ELEVATED"] = "1",
            ["POWER_MODE_TZ"] = "FLE Standard Time",
            ["POWER_MODE_POWERCFG"] = Path.Combine(_dir, "no-such-seam.ps1"),
            ["POWER_MODE_SCHTASKS"] = Path.Combine(_dir, "no-such-seam.ps1"),
        };
        foreach (var (k, v) in more) env[k] = v;
        return env;
    }

    private static (int Code, string Out) Run(Dictionary<string, string> env, params string[] args)
    {
        using var text = new StringWriter();
        using var sw = new PowerModeSwitch(env, text, @"C:\bw\bin\BluettiWatcher.exe");
        int code = sw.Run(args);
        return (code, text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd());
    }

    [Fact]
    public void Arguments_bind_like_PowerShell_names_without_case_a_unique_prefix_name_colon_value_and_two_positions()
    {
        var a = PmArgs.Parse(["eco", "-sou", "station", "-Path:C:\\x.json", "-FOR", "30", "-Adopt"]);
        Assert.Equal(("eco", "station", @"C:\x.json", "30", true), (a.Action, a.Source, a.Path, a.For, a.Adopt));
        Assert.Equal(("gpu-task", "status"), (PmArgs.Parse(["gpu-task", "status"]).Action, PmArgs.Parse(["gpu-task", "status"]).Value));
        Assert.Equal(@"C:\y.json", PmArgs.Parse(["status", "-Path:", @"C:\y.json"]).Path);   // how $args hands -Path:value on
        Assert.Contains("ambiguous", Assert.Throws<ArgumentException>(() => PmArgs.Parse(["on", "-S", "x"])).Message, StringComparison.Ordinal);
        Assert.Contains("Missing an argument", Assert.Throws<ArgumentException>(() => PmArgs.Parse(["on", "-Path"])).Message, StringComparison.Ordinal);
        Assert.Contains("positional", Assert.Throws<ArgumentException>(() => PmArgs.Parse(["on", "a", "b"])).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_switch_takes_its_value_in_both_forms_a_host_hands_on_and_refuses_what_is_not_a_boolean()
    {
        foreach (string[] on in new string[][] { ["eco", "-Adopt"], ["eco", "-Adopt:$true"], ["eco", "-Adopt:true"], ["eco", "-Adopt:", "True"], ["eco", "-Adopt:", "$true"], ["eco", "-adopt:", "true"] })
        {
            var a = PmArgs.Parse([.. on, "-ReturnPlan", "high"]);
            Assert.True(a.Adopt, string.Join(' ', on));
            Assert.Equal(("high", ""), (a.ReturnPlan, a.Value));
        }
        foreach (string[] off in new string[][] { ["eco", "-Adopt:$false"], ["eco", "-Adopt:false"], ["eco", "-Adopt:", "False"], ["eco", "-Adopt:", "$false"], ["eco", "-Adopt:0"] })
        {
            var a = PmArgs.Parse([.. off, "-ReturnPlan", "high"]);
            Assert.False(a.Adopt, string.Join(' ', off));
            Assert.Equal(("high", ""), (a.ReturnPlan, a.Value));
        }
        // After the switch, the next argument is the next name or the Value position: -Adopt alone takes none.
        Assert.Equal(("balanced", true), (PmArgs.Parse(["eco", "-Adopt", "balanced"]).Value, PmArgs.Parse(["eco", "-Adopt", "balanced"]).Adopt));
        foreach (string[] bad in new string[][] { ["eco", "-Adopt:maybe"], ["eco", "-Adopt:", "maybe"], ["eco", "-Adopt:"], ["eco", "-Adopt:", "-ReturnPlan", "high"] })
            Assert.Throws<ArgumentException>(() => PmArgs.Parse(bad));
    }

    // power-mode.ps1 1.0.0's parameters; power-mode-end.ps1 1.0.0's four more come after them.
    private static readonly string[] Legacy = ["Action", "Path", "Until", "For", "TaskUntil", "Source", "Soc", "Level", "Mains", "HibernateAt", "Adopt", "ReturnPlan", "Value", "RestoreWatts"];
    private static readonly string[] Added = ["FlagFile", "Mutex", "TaskPrefix", "ExtraReaders"];

    private static string Bound(string token)
    {
        // The name a prefix binds to: the value goes in the property of that name; a switch has no value.
        var a = PmArgs.Parse(["on", "-" + token, "7"]);
        var names = a.GetType().GetProperties().Where(p => p.PropertyType == typeof(string) || p.PropertyType == typeof(string)).ToList();
        return string.Join(',', names.Where(p => p.GetValue(a) as string == "7" && p.Name != "Source").Select(p => p.Name));
    }

    [Fact]
    public void Every_abbreviation_power_mode_ps1_1_0_0_accepted_binds_to_the_same_name_and_each_ambiguous_one_still_is()
    {
        int checkedPrefixes = 0;
        foreach (string name in Legacy)
        {
            for (int len = 1; len <= name.Length; len++)
            {
                string prefix = name[..len];
                var hits = Legacy.Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                checkedPrefixes++;
                if (hits.Count > 1 && !hits.Contains(prefix, StringComparer.OrdinalIgnoreCase))
                {
                    Assert.Contains("ambiguous", Assert.Throws<ArgumentException>(() => PmArgs.Parse(["on", "-" + prefix, "7"])).Message, StringComparison.Ordinal);
                    continue;
                }
                string want = hits.First(n => n.Equals(prefix, StringComparison.OrdinalIgnoreCase) || hits.Count == 1);
                if (want == "Adopt") Assert.True(PmArgs.Parse(["on", "-" + prefix]).Adopt, prefix);
                else if (want == "Source") Assert.Equal("7", PmArgs.Parse(["on", "-" + prefix, "7"]).Source);
                else Assert.Equal(want, Bound(prefix));
            }
        }
        Assert.True(checkedPrefixes > 60);
    }

    [Fact]
    public void The_names_added_since_1_0_0_are_spelled_out_and_never_make_an_old_abbreviation_ambiguous()
    {
        // -F -M -T keep their 1.0.0 names; the added names bind from the first letter no 1.0.0 name shares.
        Assert.Equal("For", Bound("F"));
        Assert.Equal("Mains", Bound("M"));
        Assert.Equal("TaskUntil", Bound("T"));
        foreach (var (prefix, want) in new[] { ("FlagFile", "FlagFile"), ("Fl", "FlagFile"), ("Mutex", "Mutex"), ("Mu", "Mutex"), ("TaskPrefix", "TaskPrefix"), ("TaskP", "TaskPrefix"), ("ExtraReaders", "ExtraReaders"), ("E", "ExtraReaders") })
            Assert.Equal(want, Bound(prefix));
        foreach (string name in Added) Assert.Equal(name, Bound(name));
        Assert.Contains("matches parameter name", Assert.Throws<ArgumentException>(() => PmArgs.Parse(["on", "-Zz", "7"])).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Extra_reader_whose_lookup_throws_anything_is_left_out_and_an_unexpected_failure_is_exit_2_with_a_line()
    {
        string folder = Path.Combine(_dir, "pd", "BluettiWatcher");
        var env = Env(("POWER_MODE_FILE", Path.Combine(folder, "power-mode.json")), ("POWER_MODE_EXTRA_READERS", "somebody"), ("POWER_MODE_TEST_FAULT", "translate"));
        Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
        var (code, text) = Run(env, "on");
        Assert.Equal((0, true), (code, text.StartsWith("power mode: EMERGENCY since ", StringComparison.Ordinal)));
        var (code2, text2) = Run(Env(("POWER_MODE_TEST_FAULT", "unexpected")), "status");
        Assert.Equal(2, code2);
        Assert.StartsWith("power mode: COULD NOT RUN: ", text2, StringComparison.Ordinal);
    }

    [Fact]
    public void Flag_fields_are_written_in_the_scripts_order_and_shape()
    {
        var battery = new PmBattery { Soc = 61.5, At = "2026-10-06T21:05:06+03:00", Level = "ok", Mains = false, HibernateAt = 5 };
        string fields = PowerModeSwitch.FormatFields("eco", "2026-10-06T20:00:00+03:00", "2026-10-07T00:00:00+03:00", "station", "", "381b4222-f694-41f0-9685-ff5bb260df2e",
            250, 100, new PmReplaced("a", "b", DateTimeOffset.UnixEpoch), battery);
        Assert.Equal("\"mode\": \"eco\", \"since\": \"2026-10-06T20:00:00+03:00\", \"until\": \"2026-10-07T00:00:00+03:00\", \"source\": \"station\", "
            + "\"prevPlan\": \"381b4222-f694-41f0-9685-ff5bb260df2e\", \"prevGpuLimitW\": 250, \"gpuCapW\": 100, \"replaced\": { \"mode\": \"eco\", \"since\": \"a\", \"until\": \"b\" }, "
            + "\"battery\": { \"soc\": 61.5, \"at\": \"2026-10-06T21:05:06+03:00\", \"level\": \"ok\", \"mains\": false, \"hibernateAt\": 5 }", fields);
        // a cap without a limit to return to is not written; a manual source is not written
        Assert.Equal("\"mode\": \"normal\", \"since\": \"s\"", PowerModeSwitch.FormatFields("normal", "s", "", "manual", "", "", null, 100, null, null));
    }

    [Fact]
    public void A_flag_reads_as_PowerShell_read_it_keys_without_case_an_expired_eco_normal_a_station_eco_without_until()
    {
        var now = DateTimeOffset.Parse("2026-10-06T12:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture);
        var f = PowerModeSwitch.ParseFlag("{ \"MODE\": \"eco\", \"since\": \"2026-10-06T10:00:00Z\", \"until\": \"2026-10-06T11:00:00Z\", \"prevPlan\": \"381B4222-F694-41F0-9685-FF5BB260DF2E\" }", new PmFlag(), now);
        Assert.NotNull(f);
        Assert.Equal(("normal", "eco", true, "2026-10-06T11:00:00Z", "381b4222-f694-41f0-9685-ff5bb260df2e"), (f.Mode, f.Raw, f.Expired, f.Since, f.PrevPlan));
        Assert.Equal("eco", PowerModeSwitch.ParseFlag("{ \"mode\": \"eco\", \"since\": \"2026-10-06T10:00:00Z\", \"source\": \"station\" }", new PmFlag(), now)!.Mode);
        Assert.Null(PowerModeSwitch.ParseFlag("{ \"mode\": \"eco\", \"since\": \"2026-10-06T10:00:00Z\" }", new PmFlag(), now));   // no until, not the station's
        Assert.Null(PowerModeSwitch.ParseFlag("{not json", new PmFlag(), now));
        var bad = PowerModeSwitch.ParseFlag("{ \"mode\": \"emergency\", \"since\": \"2026-10-02T22:20:00Z\", \"battery\": { \"soc\": \"lots\" }, \"prevGpuLimitW\": -5 }", new PmFlag(), now);
        Assert.Equal(("emergency", null, (double?)null), (bad!.Mode, bad.Battery, bad.PrevGpuLimitW));
    }

    [Fact]
    public void Gpu_task_xml_is_ok_only_for_system32_nvidia_smi_at_highest_run_level_and_install_watts_stay_in_range()
    {
        string xml = PowerModeSwitch.GpuTaskXml("S-1-5-21-1-2-3-1001", "BluettiWatcherEcoGpuCap", 100);
        Assert.Equal(new PmGpuTask("ok", 100), PowerModeSwitch.ParseGpuTaskXml(xml));
        Assert.Equal("foreign", PowerModeSwitch.ParseGpuTaskXml(xml.Replace("HighestAvailable", "LeastPrivilege", StringComparison.Ordinal)).State);
        Assert.Equal("foreign", PowerModeSwitch.ParseGpuTaskXml(xml.Replace("-pl 100", "-pl 100 -i 1", StringComparison.Ordinal)).State);
        Assert.Equal("D:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;FRFX;;;S-1-5-21-9)", PowerModeSwitch.GpuTaskSddl("S-1-5-21-9"));
        Assert.Equal((100, 250, ""), PowerModeSwitch.ResolveGpuTaskWatts(100, 300, 250, ""));
        Assert.StartsWith("the restore limit 400 W", PowerModeSwitch.ResolveGpuTaskWatts(100, 300, 250, "400").Error, StringComparison.Ordinal);
        Assert.StartsWith("-RestoreWatts '2.5'", PowerModeSwitch.ResolveGpuTaskWatts(100, 300, 250, "2.5").Error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_end_task_runs_the_exe_itself_as_the_user_least_privilege_with_every_name_on_its_command_line()
    {
        using var text = new StringWriter();
        using var sw = new PowerModeSwitch(Env(("POWER_MODE_TASK_PREFIX", "MyEcoMode"), ("POWER_MODE_EXTRA_READERS", "a, b")), text, @"C:\bw\bin\BluettiWatcher.exe");
        Assert.Equal(0, sw.Run(["status", "-Path", _flag]));   // loads the names
        string args = sw.EndTaskArguments(@"C:\f\power-mode.json", "2026-10-07T00:00:00+03:00");
        Assert.Equal($"power-mode end -Path \"C:\\f\\power-mode.json\" -FlagFile \"{Path.Combine(_dir, "configured", "power-mode.json")}\" -Mutex {_mutex} -TaskPrefix MyEcoMode -ExtraReaders \"a,b\" -TaskUntil 2026-10-07T00:00:00+03:00", args);
        string xml = PowerModeSwitch.EndTaskXml("S-1-5-21-1", sw.ExePath, args, DateTimeOffset.Parse("2026-10-07T00:00:00+03:00", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains(@"<Command>C:\bw\bin\BluettiWatcher.exe</Command>", xml, StringComparison.Ordinal);
        Assert.Contains("<Arguments>power-mode end -Path &quot;C:\\f\\power-mode.json&quot;", xml, StringComparison.Ordinal);
        Assert.Contains("<LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel>", xml, StringComparison.Ordinal);
        Assert.Contains("<StartBoundary>2026-10-07T00:00:00+03:00</StartBoundary>", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("conhost", xml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("powershell", xml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void On_status_off_round_trip_writes_the_flag_bytes_and_exit_codes_of_the_script()
    {
        var env = Env();
        Assert.Equal((0, "power mode: normal"), Run(env, "status", "-Path", _flag));
        var on = Run(env, "on", "-Path", _flag);
        Assert.Equal(0, on.Code);
        Assert.Matches(@"^power mode: EMERGENCY since \d{2}\.\d{2}\.\d{4} \d{2}:\d{2}$", on.Out);
        Assert.Matches("^\\{ \"mode\": \"emergency\", \"since\": \"[^\"]+\" \\}\n$", File.ReadAllText(_flag));
        Assert.Equal(1, Run(env, "status", "-Path", _flag).Code);
        Assert.Equal((0, "power mode: normal"), Run(env, "off", "-Path", _flag));
        Assert.Matches("^\\{ \"mode\": \"normal\", \"since\": \"[^\"]+\", \"by\": \"manual\" \\}\n$", File.ReadAllText(_flag));
        Assert.Equal((0, "power mode: normal (already off, since kept)"), Run(env, "off", "-Path", _flag));
        Assert.Equal(["power-mode.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Bad_lines_exit_2_and_write_nothing()
    {
        var env = Env();
        Assert.Equal((2, PowerModeSwitch.Usage), Run(env, "bogus", "-Path", _flag));
        Assert.Equal(2, Run(env, "on", "-Source", "phone", "-Path", _flag).Code);
        Assert.Equal(2, Run(env, "on", "-Bogus", "x", "-Path", _flag).Code);
        Assert.Equal(2, Run(env, "on", "-Mutex", @"Local\x", "-Path", _flag).Code);   // the names go with end only
        Assert.StartsWith("power mode: COULD NOT RUN: POWER_MODE_TASK_PREFIX '-Mutex'", Run(Env(("POWER_MODE_TASK_PREFIX", "-Mutex")), "on", "-Path", _flag).Out, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void A_tool_with_no_stand_in_under_POWER_MODE_TEST_stops_the_command_with_exit_2()
    {
        var env = Env(("POWER_MODE_POWERCFG", ""), ("POWER_MODE_SCHTASKS", ""), ("POWER_MODE_NVIDIASMI", ""));
        File.WriteAllText(_flag, "{ \"mode\": \"emergency\", \"since\": \"2026-10-02T22:20:00Z\", \"prevPlan\": \"381b4222-f694-41f0-9685-ff5bb260df2e\" }\n");
        var r = Run(env, "status", "-Path", _flag);
        Assert.Equal(2, r.Code);
        Assert.EndsWith("power mode: COULD NOT RUN: POWER_MODE_TEST is set and no stand-in names powercfg.exe (a test never starts the real one)", r.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void The_end_takes_its_names_from_its_command_line_and_refuses_one_that_is_not_plain()
    {
        File.WriteAllText(_flag, "{ \"mode\": \"emergency\", \"since\": \"2026-10-02T22:20:00Z\" }\n");
        var env = Env();
        var r = Run(env, "end", "-Path", _flag, "-FlagFile", Path.Combine(_dir, "other", "power-mode.json"), "-Mutex", _mutex, "-TaskPrefix", "MyEcoMode");
        Assert.Equal((0, "power mode: EMERGENCY since 03.10.2026 01:20 (end: no eco to end)"), r);
        Assert.StartsWith("power mode: COULD NOT RUN: POWER_MODE_MUTEX 'Local\\a b'", Run(env, "end", "-Path", _flag, "-FlagFile", _flag, "-Mutex", @"Local\a b").Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_adapter_runs_the_switch_in_process_unless_a_script_is_configured()
    {
        var cfg = new StationConfig { FlagPath = @"C:\t\flag.json" };
        var fake = new FakeSwitch();
        var runner = new FakeRunner();
        var a = new PowerModeAdapter(cfg, runner, _ => { }, fake);
        Assert.True(a.InProcess);
        var r = await a.StationEcoAsync(CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal(["eco", "-Source", "station", "-Path", @"C:\t\flag.json"], Assert.Single(fake.Calls));
        Assert.Empty(runner.Calls);
        Assert.StartsWith("BluettiWatcher.exe power-mode eco -Source station -Path", r.CommandLine, StringComparison.Ordinal);
        var scripted = new PowerModeAdapter(new StationConfig { PowerMode = new() { Script = @"C:\pm\power-mode.ps1" } }, runner, _ => { }, fake);
        Assert.False(scripted.InProcess);
        await scripted.LiftAsync(CancellationToken.None);
        Assert.Single(runner.Calls);
        Assert.Single(fake.Calls);
    }

    [Fact]
    public async Task In_process_the_switch_answers_like_a_finished_child_with_the_configured_names()
    {
        var env = Env();
        var r = await new InProcessPowerMode().RunAsync(["status", "-Path", _flag], TimeSpan.FromSeconds(30), CancellationToken.None, env);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(["power mode: normal"], r.Lines);
        Assert.False(r.TimedOut);
    }

    private sealed class FakeSwitch : IPowerModeRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, IReadOnlyDictionary<string, string>? env = null)
        {
            Calls.Add(args);
            return Task.FromResult(new ProcessResult(0, ["power mode: ECO since 06.10.2026 20:00 while mains is off (station; everything else as usual)"], false));
        }
    }
}
