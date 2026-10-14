// The GPU tasks: a caller that is not elevated, the guard, the timeouts, drift; the library functions; the neutral defaults.
// A port of power-mode/power-mode.test.js, test for test.
using System.Text.RegularExpressions;
using static BluettiWatcher.Tests.PowerModeScript.Pm;

namespace BluettiWatcher.Tests.PowerModeScript;

public sealed class PowerModeGpuTaskATests
{
    [Fact(DisplayName = "not elevated, both tasks registered: eco runs the cap task, off -Source station runs the restore task; nvidia-smi -pl is never called")]
    public async Task Not_elevated_both_tasks_registered()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, gpu: [250, 100, 250]);
        m.RegisterGpu(100, 250);
        Res eco = await m.Run(["eco"], NotElevated);
        Eq(0, eco.Status, eco.Out);
        Ok(Lines(eco.Out).Contains("gpu cap: 100 W (returns to 250 W)"), eco.Out);
        Eq("prev=250 cap=100 gpu=100", Obj(("prev", m.Json().V("prevGpuLimitW")), ("cap", m.Json().V("gpuCapW")), ("gpu", m.Gpu())));
        Ok(m.Calls().Contains($"schtasks /run /tn {CapTask}"), string.Join('|', m.Calls()));
        string[] st = Lines((await m.Run(["status"], NotElevated)).Out);
        Ok(st.Contains($"gpu helper: {CapTask} 100 W, {RestoreTask} 250 W") && st.Contains("gpu live: 100 W"), string.Join('|', st));
        Res off = await m.Run(["off", "-Source", "station"], NotElevated);
        Eq(0, off.Status, off.Out);
        Ok(Lines(off.Out).Contains("gpu: power limit back to 250 W"), off.Out);
        Ok(m.Calls().Contains($"schtasks /run /tn {RestoreTask}"), string.Join('|', m.Calls()));
        Eq("prev=undefined gpu=250", Obj(("prev", m.Json().V("prevGpuLimitW")), ("gpu", m.Gpu())));
        Assert.DoesNotContain(m.GpuCalls(), c => c.Contains("-pl", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "not elevated, no tasks: a note names gpu-task install, eco and off exit 0, the restore stays owed; an elevated off pays it")]
    public async Task Not_elevated_no_tasks()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, gpu: [250, 100, 250]);
        Res eco = await m.Run(["eco"], NotElevated);
        Eq(0, eco.Status, eco.Out);
        Match($@"^note: gpu cap not set \(not elevated and the task {CapTask} is not registered: run power-mode\.ps1 gpu-task install in an elevated shell\)$", eco.Out, RegexOptions.Multiline);
        Eq("has=false gpu=250 mode=eco", Obj(("has", m.Json().ContainsKey("prevGpuLimitW")), ("gpu", m.Gpu()), ("mode", m.Json().V("mode"))));
        Ok(Lines((await m.Run(["status"], NotElevated)).Out).Contains("gpu helper: not registered (an elevated shell: power-mode.ps1 gpu-task install)"));
        Machine n = NewMachine(t, active: Saver, gpu: [100, 100, 250]);
        WriteText(n.Flag, Amend(EcoFlag(Now() + 3600000, Balanced), Owed(250, 100)));
        Res off = await n.Run(["off", "-Source", "station"], NotElevated);
        Eq(0, off.Status, off.Out);
        Match(@"^note: the GPU power limit could not be set back to 250 W \(not elevated and the task BluettiWatcherEcoGpuRestore is not registered: .*gpu-task install in an elevated shell\): it stays owed in the flag, run off again$", off.Out, RegexOptions.Multiline);
        Eq($"mode=normal prev=250 gpu=100 plan={Balanced}", Obj(("mode", n.Json().V("mode")), ("prev", n.Json().V("prevGpuLimitW")), ("gpu", n.Gpu()), ("plan", n.Active())));
        Assert.DoesNotContain(m.GpuCalls().Concat(n.GpuCalls()), c => c.Contains("-pl", StringComparison.Ordinal));
        Eq(0, await n.Code(["off"]));
        Eq("prev=undefined gpu=250", Obj(("prev", n.Json().V("prevGpuLimitW")), ("gpu", n.Gpu())));
    }

    [Fact(DisplayName = "a task by that name whose action is not System32 nvidia-smi -pl <watts> is never run")]
    public async Task A_task_whose_action_is_not_the_registered_one_is_never_run()
    {
        await using Cleanup t = new();
        string[] xmls =
        [
            $"<Task><Actions><Exec><Command>C:\\Users\\Public\\conhost.exe</Command><Arguments>--headless &quot;{Sys32}\\nvidia-smi.exe&quot; -pl 100</Arguments></Exec></Actions></Task>",
            GpuTaskXml(100).Replace($"{Sys32}\\nvidia-smi.exe", "C:\\Users\\Public\\nvidia-smi.exe", StringComparison.Ordinal),
            GpuTaskXml(100).Replace("-pl 100", "-pl 100 -i 1", StringComparison.Ordinal),
            GpuTaskXml(100).Replace("HighestAvailable", "LeastPrivilege", StringComparison.Ordinal),
            Regex.Replace(GpuTaskXml(100), "<Principals>.*</Principals>", "", RegexOptions.None, TimeSpan.FromSeconds(5)),
        ];
        foreach (string xml in xmls)
        {
            Machine m = NewMachine(t, gpu: [250, 100, 250]);
            WriteText(Path.Combine(m.Sim, $"task-{CapTask}.xml"), xml);
            Res eco = await m.Run(["eco"], NotElevated);
            Match($@"^note: gpu cap not set \(not elevated and the task {CapTask} is not the one gpu-task install registers: ", eco.Out, RegexOptions.Multiline);
            Eq("runs=0 gpu=250", Obj(("runs", m.Calls().Count(c => c.Contains("/run", StringComparison.Ordinal))), ("gpu", m.Gpu())));
        }
    }

    [Fact(DisplayName = "the restore task's fixed value is not the recorded limit: the task runs, the debt stays, an elevated off pays it")]
    public async Task The_restore_tasks_fixed_value_is_not_the_recorded_limit()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, active: Saver, gpu: [100, 100, 250]);
        m.RegisterGpu(100, 250);
        WriteText(m.Flag, Amend(EcoFlag(Now() + 3600000, Balanced), Owed(200, 100)));
        Res off = await m.Run(["off"], NotElevated);
        Eq(0, off.Status, off.Out);
        Ok(Lines(off.Out).Contains($"note: the GPU power limit is 250 W (the fixed value of {RestoreTask}), not the recorded 200 W: it stays owed in the flag; an elevated shell pays it: power-mode.ps1 off"), off.Out);
        Eq("mode=normal prev=200 gpu=250", Obj(("mode", m.Json().V("mode")), ("prev", m.Json().V("prevGpuLimitW")), ("gpu", m.Gpu())));
        Eq(0, await m.Code(["off"]));
        Eq("prev=undefined gpu=200", Obj(("prev", m.Json().V("prevGpuLimitW")), ("gpu", m.Gpu())));
    }
}

public sealed class PowerModeGpuTaskBTests
{
    [Fact(DisplayName = "a read-back that times out keeps what is owed: eco keeps the limit to return to, off keeps the debt; a task that does not start owes nothing at eco")]
    public async Task A_read_back_that_times_out_keeps_what_is_owed()
    {
        await using Cleanup t = new();
        Dictionary<string, string> idle = Merge(NotElevated, Env(("POWER_MODE_GPU_WAIT_MS", "300"), ("FAKE_PM_HELPER_IDLE", "1")));
        Machine m = NewMachine(t, gpu: [250, 100, 250]);
        m.RegisterGpu(100, 250);
        Res eco = await m.Run(["eco"], idle);
        Eq(0, eco.Status, eco.Out);
        Match($@"^note: gpu cap not confirmed \(the task {CapTask} ran but the limit reads 250 W, not 100 W\): the limit to return to stays recorded, off puts it back$", eco.Out, RegexOptions.Multiline);
        Eq("prev=250 cap=100", Obj(("prev", m.Json().V("prevGpuLimitW")), ("cap", m.Json().V("gpuCapW"))));
        WriteText(Path.Combine(m.Sim, "gpu.txt"), "100,100,250"); // it did apply, late
        Res off = await m.Run(["off"], idle);
        Eq(0, off.Status, off.Out);
        Match($@"^note: the GPU power limit was not confirmed at 250 W \(the task {RestoreTask} ran but the limit reads 100 W, not 250 W\): it stays owed in the flag, run off again$", off.Out, RegexOptions.Multiline);
        Eq("mode=normal prev=250", Obj(("mode", m.Json().V("mode")), ("prev", m.Json().V("prevGpuLimitW"))));
        Eq(0, await m.Code(["off"], NotElevated));
        Eq("prev=undefined gpu=250", Obj(("prev", m.Json().V("prevGpuLimitW")), ("gpu", m.Gpu())));
        Machine n = NewMachine(t, gpu: [250, 100, 250]);
        n.RegisterGpu(100, 250);
        Res r = await n.Run(["eco"], Merge(NotElevated, Env(("FAKE_PM_RUNFAIL", "1"))));
        Match($@"^note: gpu cap not set \(the task {CapTask} could not be started: ", r.Out, RegexOptions.Multiline);
        Assert.False(n.Json().ContainsKey("prevGpuLimitW"));
    }

    [Fact(DisplayName = "a repeated eco re-applies the cap when the live limit drifted above it; no drift = one read, no set")]
    public async Task A_repeated_eco_re_applies_the_cap()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, gpu: [250, 100, 250]);
        Eq(0, await m.Code(["eco", "-Source", "station"]));
        WriteText(Path.Combine(m.Sim, "gpu.txt"), "250,100,250"); // a driver re-initialisation put the default back
        Res again = await m.Run(["eco", "-Source", "station"]);
        Eq(0, again.Status, again.Out);
        EqL(["note: gpu cap re-applied: the limit read 250 W, now 100 W"], Lines(again.Out).Skip(1));
        Eq("gpu=100 prev=250 cap=100", Obj(("gpu", m.Gpu()), ("prev", m.Json().V("prevGpuLimitW")), ("cap", m.Json().V("gpuCapW"))));
        int before = m.GpuCalls().Length;
        Eq(1, Lines((await m.Run(["eco", "-Source", "station"])).Out).Length);
        EqL([LiveQuery], m.GpuCalls().Skip(before));
        // the user's eco, and a caller that is not elevated (the cap task)
        Machine n = NewMachine(t, gpu: [250, 100, 250]);
        n.RegisterGpu(100, 250);
        Eq(0, await n.Code(["eco"], NotElevated));
        WriteText(Path.Combine(n.Sim, "gpu.txt"), "250,100,250");
        Res manual = await n.Run(["eco", "-For", "60"], NotElevated);
        Ok(Lines(manual.Out).Contains("note: gpu cap re-applied: the limit read 250 W, now 100 W"), manual.Out);
        Eq("gpu=100 prev=250 runs=2", Obj(("gpu", n.Gpu()), ("prev", n.Json().V("prevGpuLimitW")), ("runs", n.Calls().Count(c => c.Contains("/run", StringComparison.Ordinal)))));
    }

    [Fact(DisplayName = "the station's battery reading re-applies a drifted cap while eco is on: one read per call, a set only above the cap; never outside eco")]
    public async Task The_stations_battery_reading_re_applies_a_drifted_cap()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, gpu: [250, 100, 250]);
        Eq(0, await m.Code(["eco", "-Source", "station"]));
        string[] reading = ["battery", "-Soc", "60", "-Level", "ok", "-Mains", "off", "-HibernateAt", "5"];
        int before = m.GpuCalls().Length;
        Eq(new Res(0, "battery: 60 % (ok, mains off)", ""), await m.Run(reading));
        EqL([LiveQuery], m.GpuCalls().Skip(before));
        WriteText(Path.Combine(m.Sim, "gpu.txt"), "250,100,250"); // a driver re-initialisation put the default back
        before = m.GpuCalls().Length;
        Eq(new Res(0, "battery: 60 % (ok, mains off)\nnote: gpu cap re-applied: the limit read 250 W, now 100 W", ""), await m.Run(reading));
        Eq("gpu=100 prev=250 soc=60", Obj(("gpu", m.Gpu()), ("prev", m.Json().V("prevGpuLimitW")), ("soc", m.Json().V("battery", "soc"))));
        EqL([LiveQuery, "nvidia-smi -pl 100"], m.GpuCalls().Skip(before));
        // not elevated: the cap task
        Machine n = NewMachine(t, gpu: [250, 100, 250]);
        n.RegisterGpu(100, 250);
        Eq(0, await n.Code(["eco", "-Source", "station"], NotElevated));
        WriteText(Path.Combine(n.Sim, "gpu.txt"), "250,100,250");
        Ok(Lines((await n.Run(reading, NotElevated)).Out).Contains("note: gpu cap re-applied: the limit read 250 W, now 100 W"));
        Eq("gpu=100 runs=2", Obj(("gpu", n.Gpu()), ("runs", n.Calls().Count(c => c.Contains($"/run /tn {CapTask}", StringComparison.Ordinal)))));
        // an emergency over the eco keeps the cap: the reading re-applies it too
        Machine e = NewMachine(t, gpu: [250, 100, 250]);
        Eq(0, await e.Code(["eco", "-Source", "station"]));
        Eq(0, await e.Code(["on", "-Source", "station"]));
        WriteText(Path.Combine(e.Sim, "gpu.txt"), "250,100,250");
        EqL(["battery: 60 % (ok, mains off)", "note: gpu cap re-applied: the limit read 250 W, now 100 W"], Lines((await e.Run(reading)).Out));
        Eq("gpu=100 mode=emergency", Obj(("gpu", e.Gpu()), ("mode", e.Json().V("mode"))));
        // a normal flag still owing a restore (it failed) is never re-capped
        Machine f = NewMachine(t, gpu: [250, 100, 250]);
        WriteText(f.Flag, Amend(OldNormal, Owed(250, 100)));
        Eq(new Res(0, "battery: 60 % (ok, mains off)", ""), await f.Run(reading));
        Eq("gpu=250 calls=0", Obj(("gpu", f.Gpu()), ("calls", f.GpuCalls().Length)));
        // no eco (normal, emergency without a cap): the reading reads nothing from the GPU
        Machine o = NewMachine(t, gpu: [250, 100, 250]);
        Eq(0, await o.Code(reading));
        Eq(0, await o.Code(["on"]));
        Eq(0, await o.Code(reading));
        Assert.Empty(o.GpuCalls());
    }

    [Fact(DisplayName = "install registers from the XML text, never through a file; under a test it registers nothing")]
    public async Task Install_registers_from_the_XML_text()
    {
        await using Cleanup t = new();
        // gpu-task install lives in the exe (BluettiWatcher.exe power-mode gpu-task install).
        string src = ReadText(Path.Combine(ScriptDir, "..", "src", "BluettiWatcher", "PowerMode", "PowerModeSwitch.Commands.cs")).Replace("\r\n", "\n", StringComparison.Ordinal);
        string install = src[src.IndexOf("private int GpuTask(", StringComparison.Ordinal)..src.IndexOf("private int Battery(", StringComparison.Ordinal)];
        Match(@"_ = RealTool\(""schtasks\.exe"", \[""/delete"", ""/tn"", name, ""/f""\]\);\s*\n\s*var r = RegisterGpuTask\(name, GpuTaskXml\(MeSid\(\), name, watts\)\);", install); // a squatted name goes first
        NoMatch(@"WriteAllText|GetTempPath|""/create""", install);
        string dir = MkTemp(t, "power-mode-lib-");
        string file = Path.Combine(dir, "probe.ps1");
        WriteText(file, $". '{Lib}'\nRegister-GpuTask 'BluettiWatcherEcoGpuTestNever' (New-GpuTaskXml 'BluettiWatcherEcoGpuTestNever' 100)\n'not stopped'\n");
        Res r = await SpawnAsync("pwsh", ["-NoProfile", "-NonInteractive", "-File", file], Merge(BaseEnv(), Env(("POWER_MODE_TEST", "1"))));
        Eq(2, r.Status, r.Out + r.Err);
        Eq("power mode: COULD NOT RUN: POWER_MODE_TEST is set: a test never registers a task", r.Out.Trim());
    }
}

public sealed class PowerModeGpuTaskCTests
{
    [Fact(DisplayName = "status names the stuck state: the card at its minimum with nothing recorded to restore (eco or normal), never a guess at a set")]
    public async Task Status_names_the_stuck_state()
    {
        await using Cleanup t = new();
        const string stuck = "gpu: at minimum 100 W with nothing recorded to restore - run `nvidia-smi -pl 250` elevated";
        foreach (string? text in new string?[] { EcoFlag(Now() + 3600000, Balanced), OldNormal, null })
        {
            Machine m = NewMachine(t, gpu: [100, 100, 250]);
            if (text is not null) WriteText(m.Flag, text);
            Ok(Lines((await m.Run(["status"])).Out).Contains(stuck), text ?? "null");
            Eq("gpu=100 sets=0", Obj(("gpu", m.Gpu()), ("sets", m.GpuCalls().Count(c => c.Contains("-pl", StringComparison.Ordinal)))));
        }
        foreach ((int[] gpu, string? text) in new (int[], string?)[]
        {
            ([200, 100, 250], null),
            ([100, 100, 250], Amend(EcoFlag(Now() + 3600000, Balanced), Owed(250, 100))),
            ([100, 100, 250], OldEmergency),
        })
        {
            Machine m = NewMachine(t, gpu: gpu);
            if (text is not null) WriteText(m.Flag, text);
            NoMatch("at minimum", (await m.Run(["status"])).Out);
        }
    }

    [Fact(DisplayName = "status reads the live GPU limit beside the stored cap and flags a limit something else changed")]
    public async Task Status_reads_the_live_GPU_limit()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, gpu: [250, 100, 250]);
        Eq(0, await m.Code(["eco"]));
        string[] lines = Lines((await m.Run(["status"])).Out);
        Ok(lines.Contains("gpu cap: 100 W (returns to 250 W)") && lines.Contains("gpu live: 100 W"), string.Join('|', lines));
        WriteText(Path.Combine(m.Sim, "gpu.txt"), "250,100,250");
        lines = Lines((await m.Run(["status"])).Out);
        Ok(lines.Contains("gpu live: 250 W - MISMATCH, not the cap 100 W (something else set it)"), string.Join('|', lines));
        Eq("250", Fmt(m.Json().V("prevGpuLimitW"))); // status never writes
        Ok(Lines((await m.Run(["status"], Env(("FAKE_GPU_QUERYFAIL", "1")))).Out).Contains("gpu live: no answer from nvidia-smi"));
    }

    [Fact(DisplayName = "POWER_MODE_TEST: a call that names no stand-in stops the script, exit 2, the real tool is never started")]
    public async Task POWER_MODE_TEST_a_call_that_names_no_stand_in_stops_the_script()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, gpu: [250, 100, 250]);
        WriteText(m.Flag, Amend(EcoFlag(Now() + 3600000, Balanced), Owed(250, 100)));
        string mutex = UniqueMutex();
        Res r = await CliAsync(["status", "-Path", m.Flag], null, Env(("POWER_MODE_POWERCFG", ""), ("POWER_MODE_SCHTASKS", ""), ("POWER_MODE_NVIDIASMI", ""), ("POWER_MODE_MUTEX", mutex)));
        Eq(2, r.Status, r.Out);
        Match(@"^power mode: COULD NOT RUN: POWER_MODE_TEST is set and no stand-in names powercfg\.exe \(a test never starts the real one\)$", r.Out, RegexOptions.Multiline);
        Eq("free", await ProbeMutex(mutex));
    }

    [Fact(DisplayName = "the GPU task as registered: System32 conhost hosting System32 nvidia-smi -pl <fixed watts>, the user at the highest run level, on demand, no user-writable path")]
    public async Task The_GPU_task_as_registered()
    {
        await using Cleanup t = new();
        string xml = await LibAsync(t, "New-GpuTaskXml 'BluettiWatcherEcoGpuCap' 100");
        string sys = await LibAsync(t, "[Environment]::GetFolderPath('System')");
        string? Field(string name) { Match mm = Regex.Match(xml, $"<{name}>([^<]*)</{name}>"); return mm.Success ? mm.Groups[1].Value : null; }
        Eq(Path.Combine(sys, "conhost.exe").ToUpperInvariant(), Field("Command")!.ToUpperInvariant());
        Eq($"--headless &quot;{Path.Combine(sys, "nvidia-smi.exe")}&quot; -pl 100", Field("Arguments"));
        Eq(1, Regex.Count(xml, "<Exec>"));
        Match(@"<UserId>S-1-5-21-[\d-]+</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel>", xml);
        Eq("IgnoreNew", Field("MultipleInstancesPolicy"));
        Eq("PT1M", Field("ExecutionTimeLimit"));
        Eq("true", Field("Hidden"));
        NoMatch("<Triggers|<WorkingDirectory", xml, RegexOptions.IgnoreCase);
        string actions = xml[xml.IndexOf("<Actions", StringComparison.Ordinal)..];
        NoMatch(@"power-mode|\.ps1|Users\\|%", actions, RegexOptions.IgnoreCase);
        foreach (Match p in Regex.Matches(actions, """[A-Za-z]:\\[^&<"]*"""))
            Ok(p.Value.ToUpperInvariant().StartsWith(sys.ToUpperInvariant() + "\\", StringComparison.Ordinal), p.Value);
        Eq("ok 250", await LibAsync(t, "$x = New-GpuTaskXml 'BluettiWatcherEcoGpuRestore' 250; $r = ConvertFrom-GpuTaskXml $x; \"$($r.state) $($r.watts)\""));
        Eq("foreign", await LibAsync(t, "$x = (New-GpuTaskXml 'BluettiWatcherEcoGpuCap' 100).Replace('HighestAvailable', 'LeastPrivilege'); (ConvertFrom-GpuTaskXml $x).state"));
        Match(@"^D:P\(A;;FA;;;BA\)\(A;;FA;;;SY\)\(A;;FRFX;;;S-1-5-21-[\d-]+\)\z", await LibAsync(t, "Get-GpuTaskSddl"));
        Match(@"\.RegisterTask\(\$Name, \$Xml, 6, \$null, \$null, 3, \(Get-GpuTaskSddl\)\)", ReadText(Lib));
        Eq("ok 100", await LibAsync(t, "$x = (New-GpuTaskXml 'BluettiWatcherEcoGpuCap' 100).Replace('&quot;', '\"'); $r = ConvertFrom-GpuTaskXml $x; \"$($r.state) $($r.watts)\""));
    }
}

public sealed class PowerModeGpuTaskDTests
{
    [Fact(DisplayName = "install takes the restore limit from the card, or -RestoreWatts inside its min..max, nothing else")]
    public async Task Install_takes_the_restore_limit()
    {
        await using Cleanup t = new();
        string code = string.Join('\n',
            "$l = @{ min = 100.0; max = 250.0; default = 250.0 }",
            "foreach ($w in '', '200', '100', '250', '99', '251', 'abc', '300.5', '-5', '0300', '1e3') { $r = Resolve-GpuTaskWatts $l $w; if ($r.error) { \"$w=error\" } else { \"$w=$($r.cap)/$($r.restore)\" } }");
        string result = await LibAsync(t, code);
        EqL(["=100/250", "200=100/200", "100=100/100", "250=100/250", "99=error", "251=error", "abc=error", "300.5=error", "-5=error", "0300=error", "1e3=error"], Lines(result));
    }

    [Fact(DisplayName = "gpu-task install and remove: refused without real elevation, and refused while any test seam is set; nothing is registered or called")]
    public async Task Gpu_task_install_and_remove_are_refused()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, gpu: [250, 100, 250]);
        foreach (string verb in new[] { "install", "remove" })
        {
            Eq(new Res(2, $"power mode: COULD NOT RUN: gpu-task {verb} needs an elevated shell", ""), await m.Run(["gpu-task", verb], NotElevated));
            Res r = await m.Run(["gpu-task", verb, "-RestoreWatts", "200"]); // POWER_MODE_ELEVATED=1 claims elevation: never taken
            Eq(2, r.Status, r.Out);
            Eq(RunnerIsAdmin
                ? $"power mode: COULD NOT RUN: gpu-task {verb} takes no test seam, unset POWER_MODE_ELEVATED, POWER_MODE_NVIDIASMI, POWER_MODE_POWERCFG, POWER_MODE_SCHTASKS, POWER_MODE_TEST, POWER_MODE_TZ"
                : $"power mode: COULD NOT RUN: gpu-task {verb} needs an elevated shell", r.Out);
        }
        Eq("calls=0 files=0", Obj(("calls", m.Calls().Length), ("files", Ls(m.Sim).Count(f => f.StartsWith("task-", StringComparison.Ordinal)))));
        Eq(2, await m.Code(["gpu-task"]));
    }

    [Fact(DisplayName = "gpu-task status lists both tasks with their wattage and the live limit: 0 both registered, 1 not")]
    public async Task Gpu_task_status_lists_both_tasks()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, gpu: [100, 100, 250]);
        Eq(new Res(1, $"gpu helper: {CapTask} not registered (an elevated shell: power-mode.ps1 gpu-task install)\ngpu helper: {RestoreTask} not registered (an elevated shell: power-mode.ps1 gpu-task install)\ngpu live: 100 W", ""),
            await m.Run(["gpu-task", "status"], NotElevated));
        m.RegisterGpu(100, 250);
        Eq(new Res(0, $"gpu helper: {CapTask} sets 100 W\ngpu helper: {RestoreTask} sets 250 W\ngpu live: 100 W", ""), await m.Run(["gpu-task", "status"], NotElevated));
    }

    [Fact(DisplayName = "the neutral defaults: %ProgramData%\\BluettiWatcher\\power-mode.json, Local\\BluettiWatcherPowerMode, the BluettiWatcherEco tasks")]
    public async Task The_neutral_defaults()
    {
        await using Cleanup t = new();
        Res r = await NewFlagLib(t, Names, seams: false).Run();
        Eq(0, r.Status, r.Err);
        string[] lines = Lines(r.Out);
        string pd = lines[^1];
        EqL([Path.Combine(pd, "BluettiWatcher", "power-mode.json"), "Local\\BluettiWatcherPowerMode", "BluettiWatcherEco",
            "BluettiWatcherEcoEnd", "BluettiWatcherEcoGpuCap", "BluettiWatcherEcoGpuRestore", Path.Combine(pd, "BluettiWatcher", "power-mode.json")], lines[..^1]);
    }
}
