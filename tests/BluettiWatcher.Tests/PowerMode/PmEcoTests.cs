// eco: end time, power plan, end task (powercfg, schtasks are stand-in scripts); the end script under Windows PowerShell 5.1;
// the station poller's side of the switch (source, by, battery); the user's task, what the station's emergency replaced, -Adopt.
// A port of power-mode/power-mode.test.js, test for test.
using System.Globalization;
using System.Text.RegularExpressions;
using static BluettiWatcher.Tests.PowerModeScript.Pm;

namespace BluettiWatcher.Tests.PowerModeScript;

public sealed class PowerModeEcoATests
{
    [Fact(DisplayName = "eco with no end time lasts 4 hours from now; status exits 1 and names the end")]
    public async Task Eco_with_no_end_time_lasts_4_hours()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t);
        Res r = await m.Run(["eco"]);
        Eq(0, r.Status, r.Out);
        var j = m.Json();
        Eq("eco", (string?)j.V("mode"));
        Ok(Math.Abs(MinutesAhead(j) - 240) < 2, (string?)j.V("until"));
        Match(@"^power mode: ECO since \d{2}\.\d{2}\.\d{4} \d{2}:\d{2} until \d{2}\.\d{2}\.\d{4} \d{2}:\d{2} \(", Lines(r.Out)[0]);
        Eq(1, (await m.Run(["status"])).Status);
    }

    [Fact(DisplayName = "eco -For <minutes> is the explicit length; -Until HH:mm stays the next local wall clock")]
    public async Task Eco_For_minutes_is_the_explicit_length()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t);
        Eq(0, await m.Code(["eco", "-For", "30"]));
        Ok(Math.Abs(MinutesAhead(m.Json()) - 30) < 2, (string?)m.Json().V("until"));
        Eq(0, await m.Code(["eco", "-Until", "24:00"]));
        DateTimeOffset until = DateTimeOffset.Parse((string)m.Json().V("until")!, CultureInfo.InvariantCulture);
        // POWER_MODE_TZ pins the PC's zone to Europe/Kyiv (CliAsync).
        string local = TimeZoneInfo.ConvertTime(until, TimeZoneInfo.FindSystemTimeZoneById("FLE Standard Time")).ToString("HH:mm", CultureInfo.InvariantCulture);
        Eq("00:00", local);
        Ok(MinutesAhead(m.Json()) > 0 && MinutesAhead(m.Json()) <= 1440, (string?)m.Json().V("until"));
    }

    [Fact(DisplayName = "eco: -Until with -For, and a -For that is not minutes 1-1440, exit 2 and write nothing")]
    public async Task Eco_bad_arguments_exit_2()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t);
        string[][] cases = [["eco", "-Until", "23:00", "-For", "30"], ["eco", "-For", "0"], ["eco", "-For", "1441"], ["eco", "-For", "soon"], ["eco", "-Until", "25:00"]];
        foreach (string[] args in cases)
        {
            Res r = await m.Run(args);
            Eq(2, r.Status, $"{string.Join(' ', args)}: {r.Out}");
            Match("^power mode: COULD NOT RUN: ", r.Out);
        }
        Assert.Empty(m.Files());
        Assert.Empty(m.Calls());
    }

    [Fact(DisplayName = "eco records the active plan, turns Power saver on and registers the end task; off goes to maximum performance and removes the task")]
    public async Task Eco_records_the_plan_and_registers_the_end_task()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t);
        Res r = await m.Run(["eco"]);
        Eq(0, r.Status, r.Out);
        var j = m.Json();
        Eq(Balanced, (string?)j.V("prevPlan"));
        Eq(Saver, m.Active());
        EqL([$"plan: Power saver on; returns to {Balanced}", "end task: BluettiWatcherEcoEnd registered"], Lines(r.Out).Skip(1));
        string? xml = m.TaskXml();
        Ok(xml is not null, "no task registered");
        string until = (string)j.V("until")!;
        Ok(Has(xml!, $"<StartBoundary>{until}</StartBoundary>"), xml);
        Ok(Has(xml!, "<StartWhenAvailable>true</StartWhenAvailable>"), xml);
        Ok(Has(xml!, "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>"), xml);
        Ok(Has(xml!, $"<Command>{Exe}</Command>"), xml);
        Ok(Has(xml!, $"<Arguments>power-mode end -Path &quot;{m.Flag}&quot; -FlagFile &quot;{Path.Combine(ProgramData, "BluettiWatcher", "power-mode.json")}&quot; -Mutex Local\\BluettiWatcherPowerMode -TaskPrefix BluettiWatcherEco -TaskUntil {until}</Arguments>"), xml);
        Res st = await m.Run(["status"]);
        Eq(1, st.Status);
        EqL([$"plan: returns to Balanced ({Balanced})", "end task: BluettiWatcherEcoEnd present"], Lines(st.Out).Skip(1));
        Res off = await m.Run(["off"]);
        Eq(new Res(0, "power mode: normal\nplan: Balanced, Best performance", ""), off);
        Eq(Balanced, m.Active());
        Eq(Best, m.Overlay());
        Assert.Null(m.TaskXml());
        Ok(FlagRe.IsMatch(ReadText(m.Flag)), ReadText(m.Flag)); // prevPlan is gone
        Eq(new Res(0, "power mode: normal", ""), await m.Run(["status"]));
    }
}

public sealed class PowerModeEcoBTests
{
    [Fact(DisplayName = "end at the end time: the plan comes back, the flag is normal since `until`, the task removes itself")]
    public async Task End_at_the_end_time_the_plan_comes_back()
    {
        await using Cleanup t = new();
        foreach (long untilMs in new[] { Now() - 60000, Now() + 600000 }) // passed; fired early: equals the task's until
        {
            Machine m = NewMachine(t, active: Saver);
            WriteText(m.Flag, EcoFlag(untilMs, Balanced));
            WriteText(m.TaskFile, "registered");
            Res r = await m.Run(["end", "-TaskUntil", Iso(untilMs)]);
            Eq(new Res(0, "power mode: normal (eco ended)\nplan: Balanced, Best performance", ""), r);
            Eq(Balanced, m.Active());
            Eq(Best, m.Overlay());
            Eq($"{{ \"mode\": \"normal\", \"since\": \"{Iso(untilMs)}\" }}\n", ReadText(m.Flag));
            Assert.Null(m.TaskXml());
        }
    }

    [Fact(DisplayName = "a second eco while eco is on replaces the task and keeps the first plan to return to")]
    public async Task A_second_eco_replaces_the_task()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t);
        Eq(0, await m.Code(["eco", "-For", "30"]));
        var first = m.Json();
        Res r = await m.Run(["eco", "-For", "90"]);
        Eq(0, r.Status, r.Out);
        var second = m.Json();
        Eq(Balanced, (string?)second.V("prevPlan")); // never Power saver, the plan active at the second call
        Assert.NotEqual((string?)first.V("until"), (string?)second.V("until"));
        Ok(Has(m.TaskXml()!, $"-TaskUntil {second.V("until")}</Arguments>"), m.TaskXml());
        Eq(Saver, m.Active());
        Ok(Has(r.Out, $"plan: Power saver stays on; returns to {Balanced}"), r.Out);
        Eq(0, await m.Code(["off"]));
        Eq(Balanced, m.Active());
    }

    [Fact(DisplayName = "eco with Power saver already active and nothing recorded: the plan off returns to is recorded, never Power saver")]
    public async Task Eco_under_Power_saver_records_the_return_plan()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, active: Saver);
        Res r = await m.Run(["eco"]);
        Eq(0, r.Status, r.Out);
        Eq(Balanced, (string?)m.Json().V("prevPlan")); // returnPlan balanced
        Ok(m.TaskXml() is not null, "no end task");
        Eq(Saver, m.Active());
        EqL(["powercfg /list"], m.Calls().Where(c => c.StartsWith("powercfg", StringComparison.Ordinal)));
        EqL([$"plan: returns to Balanced ({Balanced})", "end task: BluettiWatcherEcoEnd present"], Lines((await m.Run(["status"])).Out).Skip(1));
        Eq(0, await m.Code(["off"]));
        Eq(Obj(("active", Balanced), ("overlay", Best)), Obj(("active", m.Active()), ("overlay", m.Overlay())));
    }

    [Fact(DisplayName = "an older task's end does not cut a newer eco short")]
    public async Task An_older_tasks_end_does_not_cut_a_newer_eco_short()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, active: Saver);
        string text = EcoFlag(Now() + 3600000, Balanced);
        WriteText(m.Flag, text);
        WriteText(m.TaskFile, "the newer task");
        string[][] cases = [["end", "-TaskUntil", Iso(Now() - 60000)], ["end"]];
        foreach (string[] args in cases)
        {
            Res r = await m.Run(args);
            Eq(0, r.Status, r.Out);
            Match(@"^power mode: ECO since .* \(end: not due, eco runs on\)\z", r.Out);
            Eq(text, ReadText(m.Flag));
            Eq(Saver, m.Active());
            Eq("the newer task", ReadText(m.TaskFile));
        }
        Assert.Empty(m.Calls());
    }
}

public sealed class PowerModeEcoCTests
{
    [Fact(DisplayName = "end on a flag that is not eco changes nothing; called by a stale task, that task goes")]
    public async Task End_on_a_flag_that_is_not_eco_changes_nothing()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, active: Saver);
        string text = $"{{ \"mode\": \"emergency\", \"since\": \"{OldSince}\", \"prevPlan\": \"{Balanced}\" }}\n";
        WriteText(m.Flag, text);
        WriteText(m.TaskFile, "stale");
        Res r = await m.Run(["end", "-TaskUntil", Iso(Now() - 60000)]);
        Eq(0, r.Status, r.Out);
        Match(@"^power mode: EMERGENCY since .* \(end: no eco to end\)\z", r.Out);
        Eq(text, ReadText(m.Flag));
        Eq(Saver, m.Active());
        Assert.Null(m.TaskXml());
    }

    [Fact(DisplayName = "the Task Scheduler refuses: the task cannot be registered, eco is still set, exit 0, a warn line names what will not come back")]
    public async Task The_Task_Scheduler_refuses()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t);
        Res r = await m.Run(["eco"], Env(("FAKE_PM_DENY", "1")));
        Eq(0, r.Status, r.Out);
        Eq("eco", (string?)m.Json().V("mode"));
        Eq(Balanced, (string?)m.Json().V("prevPlan"));
        Eq(Saver, m.Active());
        Assert.Null(m.TaskXml());
        Eq($"warn: the end task BluettiWatcherEcoEnd could not be registered (ERROR: Access is denied.): the power plan {Balanced} will not come back at the end time, only on power-mode.ps1 off", Lines(r.Out)[2]);
        EqL([$"plan: returns to Balanced ({Balanced})", "end task: BluettiWatcherEcoEnd absent"], Lines((await m.Run(["status"])).Out).Skip(1));
        Eq(0, await m.Code(["off"]));
        Eq(Balanced, m.Active());
    }

    [Fact(DisplayName = "no Power saver plan on the machine: said so, the plan is left alone, eco is set, exit 0")]
    public async Task No_Power_saver_plan_on_the_machine()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, plans: [Balanced]);
        Res r = await m.Run(["eco"]);
        Eq(0, r.Status, r.Out);
        Eq("eco", (string?)m.Json().V("mode"));
        Assert.False(m.Json().ContainsKey("prevPlan"));
        Eq($"warn: the Power saver plan ({Saver}) is not on this machine: the power plan is unchanged", Lines(r.Out)[1]);
        Eq(Balanced, m.Active());
        Assert.Null(m.TaskXml());
        EqL(["powercfg /list"], m.Calls());
    }

    [Fact(DisplayName = "on over eco keeps Power saver and the plan to return to, removes the end task; off brings the plan back")]
    public async Task On_over_eco_keeps_Power_saver()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t);
        Eq(0, (await m.Run(["eco"])).Status);
        Res on = await m.Run(["on"]);
        Eq(0, on.Status, on.Out);
        Eq($"plan: Power saver stays on; returns to {Balanced} on off", Lines(on.Out)[1]);
        Eq(Obj(("mode", "emergency"), ("prevPlan", Balanced), ("until", null)), Pick(m.Json(), "mode", "prevPlan", "until"));
        Eq(Saver, m.Active());
        Assert.Null(m.TaskXml());
        Eq(2, await m.Code(["eco"])); // emergency is stricter
        Eq(0, await m.Code(["off"]));
        Eq(Balanced, m.Active());
        Ok(FlagRe.IsMatch(ReadText(m.Flag)));
    }
}

public sealed class PowerModeEcoDTests
{
    [Fact(DisplayName = "an eco that ended while the PC was off: status is normal and shows the plan still owed; off brings it back and keeps since")]
    public async Task An_eco_that_ended_while_the_PC_was_off()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, active: Saver);
        long untilMs = Now() - 600000;
        WriteText(m.Flag, EcoFlag(untilMs, Balanced));
        Res st = await m.Run(["status"]);
        Eq(0, st.Status, st.Out);
        EqL([$"plan: returns to Balanced ({Balanced})", "end task: BluettiWatcherEcoEnd absent"], Lines(st.Out).Skip(1));
        Eq(0, await m.Code(["off"]));
        Eq(Balanced, m.Active());
        Eq($"{{ \"mode\": \"normal\", \"since\": \"{Iso(untilMs)}\", \"by\": \"manual\" }}\n", ReadText(m.Flag));
    }

    [Fact(DisplayName = "a plan that cannot be set stays in the flag for the next off")]
    public async Task A_plan_that_cannot_be_set_stays_in_the_flag()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, active: Saver);
        WriteText(m.Flag, EcoFlag(Now() + 3600000, Balanced));
        Res r = await m.Run(["off"], Env(("FAKE_PM_SETFAIL", "1")));
        Eq(0, r.Status, r.Out);
        Match(@"^warn: the Balanced plan could not be set \(", Lines(r.Out)[1]);
        Eq(Obj(("mode", "normal"), ("prevPlan", Balanced)), Pick(m.Json(), "mode", "prevPlan"));
        Res o = await m.Run(["off"], Env(("FAKE_PM_OVERLAYFAIL", "1")));
        Match(@"^warn: the Best performance power mode could not be set \(", Lines(o.Out)[1]);
        Eq(Balanced, (string?)m.Json().V("prevPlan"));
        Eq(0, await m.Code(["off"]));
        Eq(Balanced, m.Active());
        Eq(Best, m.Overlay());
        Assert.False(m.Json().ContainsKey("prevPlan"));
    }

    [Fact(DisplayName = "returnPlan previous: off brings back the plan that was active before eco, by name; only Balanced gets the Best performance overlay")]
    public async Task ReturnPlan_previous_brings_back_the_previous_plan()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, plans: [Balanced, Saver, High], active: High, config: Cfg("previous"));
        Eq(0, await m.Code(["eco"]));
        Eq(Obj(("prevPlan", High), ("active", Saver)), Obj(("prevPlan", m.Json().V("prevPlan")), ("active", m.Active())));
        Eq($"plan: returns to High performance ({High})", Lines((await m.Run(["status"])).Out)[1]);
        Eq(new Res(0, "power mode: normal\nplan: High performance", ""), await m.Run(["off"]));
        Eq(Obj(("active", High), ("overlay", null)), Obj(("active", m.Active()), ("overlay", m.Overlay())));
    }

    [Fact(DisplayName = "on, off and status on flags without a plan never call powercfg or the Task Scheduler")]
    public async Task On_off_and_status_without_a_plan_never_call_powercfg()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t);
        foreach (string action in new[] { "status", "off", "on", "on", "status", "off", "off", "end" })
            Assert.NotEqual(2, await m.Code([action]));
        Assert.Empty(m.Calls());
    }
}

public sealed class PowerModeEcoEndTests
{
    [Fact(DisplayName = "power-mode-end.ps1 under Windows PowerShell 5.1: a due eco gets its plan back, the flag is normal, the task is gone")]
    public async Task End_script_under_5_1_a_due_eco()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, active: Saver);
        long untilMs = Now() - 60000;
        WriteText(m.Flag, Amend(EcoFlag(untilMs, Balanced), Batt61));
        WriteText(m.TaskFile, "registered");
        Res r = await End51Async(m, ["-TaskUntil", Iso(untilMs)]);
        Eq(new Res(0, "power mode: normal (eco ended)\nplan: Balanced, Best performance", ""), r);
        Eq(Balanced, m.Active());
        Eq(Best, m.Overlay());
        Eq($"{{ \"mode\": \"normal\", \"since\": \"{Iso(untilMs)}\"" + Batt61, ReadText(m.Flag));
        Assert.Null(m.TaskXml());
        EqL(["power-mode.json"], m.Files());
    }

    [Fact(DisplayName = "power-mode-end.ps1 under 5.1: a newer eco, an eco from the station and a corrupt flag are left alone")]
    public async Task End_script_under_5_1_leaves_other_flags_alone()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, active: Saver);
        string newer = EcoFlag(Now() + 3600000, Balanced);
        WriteText(m.Flag, newer);
        WriteText(m.TaskFile, "the newer task");
        Res r = await End51Async(m, ["-TaskUntil", Iso(Now() - 60000)]);
        Eq(0, r.Status, r.Out + r.Err);
        Match(@"^power mode: ECO since .* \(end: not due, eco runs on\)\z", r.Out);
        Eq(newer, ReadText(m.Flag));
        Eq("the newer task", ReadText(m.TaskFile));
        string station = $"{{ \"mode\": \"eco\", \"since\": \"{Iso(Now() - 3600000)}\", \"source\": \"station\", \"prevPlan\": \"{Balanced}\" }}\n";
        WriteText(m.Flag, station);
        r = await End51Async(m, []);
        Eq(0, r.Status, r.Out + r.Err);
        Match(@"\(end: no eco to end\)\z", r.Out);
        Eq(station, ReadText(m.Flag));
        Eq(Saver, m.Active());
        WriteText(m.Flag, "{not json");
        r = await End51Async(m, ["-TaskUntil", Iso(Now() - 60000)]);
        Eq(2, r.Status, r.Out + r.Err);
        Match("^power mode: COULD NOT READ ", r.Out);
        Eq("{not json", ReadText(m.Flag));
    }

    [Fact(DisplayName = "power-mode-end.ps1 and the flag reader carry no PowerShell 7 syntax")]
    public async Task End_script_and_flag_reader_carry_no_PowerShell_7_syntax()
    {
        foreach (string file in new[] { End, Path.Combine(ScriptDir, "power-mode-flag.ps1") })
        {
            Res r = await SpawnAsync(Ps51, ["-NoProfile", "-NonInteractive", "-Command",
                "$e = $null; $null = [Management.Automation.Language.Parser]::ParseFile($env:PARSE_FILE, [ref]$null, [ref]$e); $e | ForEach-Object { $_.Message }; exit @($e).Count"],
                Merge(BaseEnv(), Env(("PARSE_FILE", file))));
            Eq(0, r.Status, $"{file}: {r.Out}{r.Err}");
        }
    }

    [Fact(DisplayName = "eco -Source station: no end time, no end task, never expires on read; off -Source station records who")]
    public async Task Eco_Source_station()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t);
        Res r = await m.Run(["eco", "-Source", "station"]);
        Eq(0, r.Status, r.Out);
        var j = m.Json();
        Eq(Obj(("mode", "eco"), ("source", "station"), ("until", null), ("prevPlan", Balanced)), Pick(j, "mode", "source", "until", "prevPlan"));
        Match(@"^power mode: ECO since \d{2}\.\d{2}\.\d{4} \d{2}:\d{2} while mains is off \(station; ", Lines(r.Out)[0]);
        Eq(Saver, m.Active());
        Assert.Null(m.TaskXml());
        WriteText(m.Flag, Regex.Replace(ReadText(m.Flag), "\"since\": \"[^\"]+\"", "\"since\": \"2020-01-01T00:00:00+00:00\"", RegexOptions.None, TimeSpan.FromSeconds(5)));
        Eq(1, (await m.Run(["status"])).Status); // years later: still eco
        Res again = await m.Run(["eco", "-Source", "station"]);
        Eq(0, again.Status, again.Out);
        Match(@"\(already on, kept\)\z", again.Out);
        Eq("2020-01-01T00:00:00+00:00", (string?)m.Json().V("since"));
        string[][] bad = [["eco", "-Source", "station", "-For", "30"], ["eco", "-Source", "station", "-Until", "23:00"], ["eco", "-Source", "phone"]];
        foreach (string[] args in bad) Eq(2, await m.Code(args), string.Join(' ', args));
        Eq(0, await m.Code(["off", "-Source", "station"]));
        Eq(Obj(("mode", "normal"), ("by", "station"), ("prevPlan", null)), Pick(m.Json(), "mode", "by", "prevPlan"));
        Eq(Balanced, m.Active());
    }
}

public sealed class PowerModeStationTests
{
    [Fact(DisplayName = "the station never replaces or shortens a manual eco; a manual off records by: manual")]
    public async Task The_station_never_replaces_a_manual_eco()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t);
        Eq(0, await m.Code(["eco", "-For", "90"]));
        string before = ReadText(m.Flag);
        Res r = await m.Run(["eco", "-Source", "station"]);
        Eq(0, r.Status, r.Out);
        Match(@"\(already on, kept\)\z", r.Out);
        Eq(before, ReadText(m.Flag));
        Ok(m.TaskXml() is not null, "the manual end task stays");
        Eq(0, await m.Code(["off"]));
        Eq("manual", (string?)m.Json().V("by"));
    }

    [Fact(DisplayName = "an eco whose plan and GPU cap failed: the failures are said, and the next station eco repairs both, keeping since")]
    public async Task A_station_eco_over_an_eco_repairs_what_was_not_applied()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, gpu: [250, 100, 250]);
        Res first = await m.Run(["eco", "-Source", "station"], Env(("FAKE_PM_LISTFAIL", "1"), ("FAKE_GPU_QUERYFAIL", "1")));
        Eq(0, first.Status, first.Out);
        Ok(Lines(first.Out).Contains("warn: powercfg did not list the power plans: the power plan is unchanged"), first.Out);
        Ok(Lines(first.Out).Any(l => l.StartsWith("note: gpu cap not set (", StringComparison.Ordinal)), first.Out);
        Eq(Obj(("prevPlan", null), ("prevGpuLimitW", null)), Pick(m.Json(), "prevPlan", "prevGpuLimitW"));
        Eq(Balanced, m.Active());
        string since = (string)m.Json().V("since")!;
        // The page's Eco and the station's both send `eco -Source station`.
        Res again = await m.Run(["eco", "-Source", "station"]);
        Eq(0, again.Status, again.Out);
        Eq(Obj(("mode", "eco"), ("source", "station"), ("since", since), ("until", null), ("prevPlan", Balanced)), Pick(m.Json(), "mode", "source", "since", "until", "prevPlan"));
        Eq(Saver, m.Active());
        Eq("gpu=100 prev=250 cap=100", Obj(("gpu", m.Gpu()), ("prev", m.Json().V("prevGpuLimitW")), ("cap", m.Json().V("gpuCapW"))));
        Assert.Null(m.TaskXml());
        // Repaired: a third call changes nothing and says it kept the eco.
        string before = ReadText(m.Flag);
        Res third = await m.Run(["eco", "-Source", "station"]);
        Match(@"\(already on, kept\)\z", third.Out);
        Eq(before, ReadText(m.Flag));
    }

    [Fact(DisplayName = "the station repairs a manual eco's missing plan without touching its end time; the end task is registered")]
    public async Task A_station_eco_repairs_a_manual_eco_and_keeps_its_end_time()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t);
        Eq(0, await m.Code(["eco", "-For", "90"], Env(("FAKE_PM_LISTFAIL", "1"))));
        Eq(Obj(("prevPlan", null)), Pick(m.Json(), "prevPlan"));
        Assert.Null(m.TaskXml());
        var j = m.Json();
        Res r = await m.Run(["eco", "-Source", "station"]);
        Eq(0, r.Status, r.Out);
        Eq(Obj(("source", null), ("since", (string?)j.V("since")), ("until", (string?)j.V("until")), ("prevPlan", Balanced)),
            Pick(m.Json(), "source", "since", "until", "prevPlan"));
        Eq(Saver, m.Active());
        Ok(m.TaskXml() is not null, "the end task of the manual eco");
    }

    [Fact(DisplayName = "a kept eco that owes a plan sets Power saver again when it is not active; prevPlan stays; a list failure is a warn")]
    public async Task A_station_eco_sets_Power_saver_again_when_a_plan_is_owed()
    {
        await using Cleanup t = new();
        foreach (string[] first in new[] { new[] { "eco", "-Source", "station" }, new[] { "eco", "-For", "90" } })
        {
            Machine m = NewMachine(t);
            Res failed = await m.Run(first, Env(("FAKE_PM_SETFAIL", "1")));
            Ok(Lines(failed.Out).Any(l => l.StartsWith("warn: the Power saver plan could not be set", StringComparison.Ordinal)), failed.Out);
            Eq(Balanced, (string?)m.Json().V("prevPlan"));
            Eq(Balanced, m.Active());
            string before = ReadText(m.Flag);
            Res listFail = await m.Run(["eco", "-Source", "station"], Env(("FAKE_PM_LISTFAIL", "1")));
            Ok(Lines(listFail.Out).Contains("warn: powercfg did not list the power plans: the power plan is unchanged"), listFail.Out);
            Res again = await m.Run(["eco", "-Source", "station"]);
            Eq(0, again.Status, again.Out);
            Eq(Saver, m.Active());
            Ok(Lines(again.Out).Contains($"plan: Power saver on; returns to {Balanced}"), again.Out);
            Eq(before, ReadText(m.Flag));
        }
    }

    [Fact(DisplayName = "a station eco over an eco never records Power saver: Power saver on by hand records the return plan")]
    public async Task A_station_eco_repair_never_records_Power_saver()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t);
        Eq(0, await m.Code(["eco", "-Source", "station"], Env(("FAKE_PM_LISTFAIL", "1"))));
        WriteText(Path.Combine(m.Sim, "active.txt"), Saver);
        Eq(0, await m.Code(["eco", "-Source", "station"]));
        Eq(Balanced, (string?)m.Json().V("prevPlan"));
        Eq(Saver, m.Active());
    }

    [Fact(DisplayName = "on -Source station marks the emergency as the station's; a manual on does not")]
    public async Task On_Source_station_marks_the_emergency()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t);
        Eq(0, await m.Code(["eco", "-Source", "station"]));
        Eq(0, await m.Code(["on", "-Source", "station"]));
        Eq(Obj(("mode", "emergency"), ("source", "station"), ("prevPlan", Balanced)), Pick(m.Json(), "mode", "source", "prevPlan"));
        Eq(0, await m.Code(["off", "-Source", "station"]));
        Eq(0, await m.Code(["on"]));
        Assert.False(m.Json().ContainsKey("source"));
    }

    [Fact(DisplayName = "battery writes the reading into the flag and keeps everything else; every later write keeps the reading")]
    public async Task Battery_writes_the_reading_into_the_flag()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t);
        Eq(new Res(0, "battery: 87 % (ok, mains off)", ""), await m.Run(["battery", "-Soc", "87", "-Level", "ok", "-Mains", "off"]));
        var j = m.Json();
        Eq("mode=normal soc=87 level=ok mains=false", Obj(("mode", j.V("mode")), ("soc", j.V("battery", "soc")), ("level", j.V("battery", "level")), ("mains", j.V("battery", "mains"))));
        Ok(Math.Abs(ParseMs((string)j.V("battery", "at")!) - Now()) < 120000, (string?)j.V("battery", "at"));
        Eq(0, await m.Code(["eco", "-For", "30"]));
        var eco = m.Json();
        Eq("87", Fmt(eco.V("battery", "soc")));
        Eq(0, await m.Code(["battery", "-Soc", "18.5", "-Level", "low", "-Mains", "off"]));
        j = m.Json();
        Eq(Obj(("mode", "eco"), ("since", eco.V("since")), ("until", eco.V("until")), ("prevPlan", Balanced), ("soc", 18.5), ("level", "low")),
            Obj(("mode", j.V("mode")), ("since", j.V("since")), ("until", j.V("until")), ("prevPlan", j.V("prevPlan")), ("soc", j.V("battery", "soc")), ("level", j.V("battery", "level"))));
        Res st = await m.Run(["status"]);
        Match(@"^battery: 18\.5 % \(low, mains off\) at \d{2}\.\d{2}\.\d{4} \d{2}:\d{2}\z", Lines(st.Out)[^1]);
        Eq(0, await m.Code(["on"]));
        Eq(0, await m.Code(["off"]));
        Eq("18.5", Fmt(m.Json().V("battery", "soc")));
        string[][] bad = [["battery", "-Soc", "101", "-Level", "ok", "-Mains", "on"], ["battery", "-Soc", "50", "-Level", "fine", "-Mains", "on"], ["battery", "-Soc", "50", "-Level", "ok", "-Mains", "maybe"], ["battery"]];
        foreach (string[] args in bad) Eq(2, await m.Code(args), string.Join(' ', args));
        WriteText(m.Flag, "{not json");
        Eq(2, await m.Code(["battery", "-Soc", "50", "-Level", "ok", "-Mains", "on"]));
        Eq("{not json", ReadText(m.Flag));
    }

    [Fact(DisplayName = "a flag without the new fields reads as before; a malformed battery is no battery, never a corrupt flag")]
    public async Task A_flag_without_the_new_fields_reads_as_before()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t);
        WriteText(m.Flag, "{ \"mode\": \"emergency\", \"since\": \"2026-10-02T22:20:00Z\", \"battery\": { \"soc\": \"lots\", \"at\": \"x\", \"level\": \"ok\" } }\n");
        Eq(new Res(1, "power mode: EMERGENCY since 03.10.2026 01:20", ""), await m.Run(["status"]));
        WriteText(m.Flag, "{ \"mode\": \"eco\", \"since\": \"2026-10-02T22:20:00Z\" }\n"); // no until and not the station's: not a flag
        Eq(2, await m.Code(["status"]));
    }
}

public sealed class PowerModeUserTaskTests
{
    [Fact(DisplayName = "the end task is the user's, never SYSTEM: interactive, least privilege, headless, made up at the next logon")]
    public async Task The_end_task_is_the_users()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t);
        Eq(0, await m.Code(["eco"]));
        string? xml = m.TaskXml();
        Ok(xml is not null, "no task registered");
        NoMatch(@"S-1-5-18|HighestAvailable|<LogonType>(Password|S4U|ServiceAccount)", xml!);
        Match(@"<UserId>S-1-5-21-[\d-]+</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel>", xml!);
        Ok(Has(xml!, $"<Command>{Exe}</Command>"), xml); // the exe itself (a WinExe: no window), no PowerShell
        Ok(Has(xml!, $"<Arguments>power-mode end -Path &quot;{m.Flag}&quot;"), xml);
        Ok(Has(xml!, "<StartWhenAvailable>true</StartWhenAvailable>"), xml);
        // registered with a plain /create /xml: no /ru, no password, nothing that asks for elevation
        string create = m.Calls().First(c => c.StartsWith("schtasks /create", StringComparison.Ordinal));
        Match(@"^schtasks /create /tn BluettiWatcherEcoEnd /xml \S+ /f\z", create);
    }

    [Fact(DisplayName = "the station's emergency over a manual eco remembers it and names its end time; mains back (off -Source station) lifts everything to maximum performance")]
    public async Task The_stations_emergency_over_a_manual_eco_remembers_it()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t);
        Eq(0, await m.Code(["eco", "-For", "120"]));
        var eco = m.Json();
        Res on = await m.Run(["on", "-Source", "station"]);
        Eq(0, on.Status, on.Out);
        var j = m.Json();
        Eq(Obj(("mode", "emergency"), ("source", "station"), ("prevPlan", Balanced)), Pick(j, "mode", "source", "prevPlan"));
        Eq(Obj(("mode", "eco"), ("since", eco.V("since")), ("until", eco.V("until"))), Obj(("mode", j.V("replaced", "mode")), ("since", j.V("replaced", "since")), ("until", j.V("replaced", "until"))));
        Match(@"^power mode: EMERGENCY since \d{2}\.\d{2}\.\d{4} \d{2}:\d{2} \(over the user's eco until \d{2}\.\d{2}\.\d{4} \d{2}:\d{2}\)\z", Lines(on.Out)[0]);
        Assert.Null(m.TaskXml());
        Eq(0, await m.Code(["battery", "-Soc", "19", "-Level", "low", "-Mains", "off", "-HibernateAt", "5"]));
        Eq(j["replaced"]!.ToJsonString(), m.Json()["replaced"]!.ToJsonString()); // the reading keeps it
        Eq("5", Fmt(m.Json().V("battery", "hibernateAt")));
        Match("over the user's eco until", (await m.Run(["status"])).Out);
        Res off = await m.Run(["off", "-Source", "station"]);
        Eq(0, off.Status, off.Out);
        j = m.Json();
        Eq(Obj(("mode", "normal"), ("by", "station"), ("prevPlan", null), ("replaced", null)), Pick(j, "mode", "by", "prevPlan", "replaced"));
        Eq(Obj(("active", Balanced), ("overlay", Best), ("task", null)), Obj(("active", m.Active()), ("overlay", m.Overlay()), ("task", m.TaskXml())));
        Eq("power mode: normal\nplan: Balanced, Best performance", off.Out);
    }

    [Fact(DisplayName = "a replaced eco is never put back: an off by hand drops it too, end time passed or not")]
    public async Task A_replaced_eco_is_never_put_back()
    {
        await using Cleanup t = new();
        foreach (long untilMs in new[] { Now() - 60000, Now() + 3600000 })
        {
            Machine m = NewMachine(t, active: Saver);
            WriteText(m.Flag, $"{{ \"mode\": \"emergency\", \"since\": \"{Iso(Now() - 600000)}\", \"source\": \"station\", \"prevPlan\": \"{Balanced}\", \"replaced\": {{ \"mode\": \"eco\", \"since\": \"{Iso(Now() - 7200000)}\", \"until\": \"{Iso(untilMs)}\" }} }}\n");
            Eq(0, await m.Code(["off"]));
            Eq(Obj(("mode", "normal"), ("by", "manual"), ("replaced", null)), Pick(m.Json(), "mode", "by", "replaced"));
            Eq(Obj(("active", Balanced), ("overlay", Best)), Obj(("active", m.Active()), ("overlay", m.Overlay())));
        }
    }

    [Fact(DisplayName = "the station's emergency over its own eco, and a manual on, remember nothing")]
    public async Task The_stations_emergency_over_its_own_eco_remembers_nothing()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t);
        Eq(0, await m.Code(["eco", "-Source", "station"]));
        Eq(0, await m.Code(["on", "-Source", "station"]));
        Assert.False(m.Json().ContainsKey("replaced"));
        Eq(0, await m.Code(["off", "-Source", "station"]));
        Eq("normal", (string?)m.Json().V("mode"));
        Eq(0, await m.Code(["eco"]));
        Eq(0, await m.Code(["on"]));
        Assert.False(m.Json().ContainsKey("replaced"));
    }
}
