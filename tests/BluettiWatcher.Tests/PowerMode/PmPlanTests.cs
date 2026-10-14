// The plan to return to (power-mode.config.json beside the flag), prev-plan, -Adopt and the GPU power cap.
// A port of power-mode/power-mode.test.js, test for test.
using System.Text.RegularExpressions;
using static BluettiWatcher.Tests.PowerModeScript.Pm;

namespace BluettiWatcher.Tests.PowerModeScript;

public sealed class PowerModeAdoptTests
{
    [Fact(DisplayName = "the user's eco end time under the station's emergency is dropped from the status line once it has passed")]
    public async Task The_users_eco_end_time_is_dropped_once_it_has_passed()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t);
        string Flag(long untilMs) => $"{{ \"mode\": \"emergency\", \"since\": \"{Iso(Now() - 600000)}\", \"source\": \"station\", \"replaced\": {{ \"mode\": \"eco\", \"since\": \"{Iso(Now() - 7200000)}\", \"until\": \"{Iso(untilMs)}\" }} }}\n";
        WriteText(m.Flag, Flag(Now() + 3600000));
        Match("over the user's eco until", (await m.Run(["status"])).Out);
        WriteText(m.Flag, Flag(Now() - 60000));
        Match(@"^power mode: EMERGENCY since \d{2}\.\d{2}\.\d{4} \d{2}:\d{2}$", (await m.Run(["status"])).Out, RegexOptions.Multiline);
    }

    [Fact(DisplayName = "eco -Adopt: an eco that is on gets a plan to return to and its end task; since and until stay")]
    public async Task Eco_Adopt_gets_a_plan_and_its_end_task()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, plans: [Balanced, Saver, High], active: Saver);
        long untilMs = Now() + 3600000;
        WriteText(m.Flag, EcoFlag(untilMs));
        Res r = await m.Run(["eco", "-Adopt", "-ReturnPlan", "balanced"]);
        Eq(0, r.Status, r.Out);
        EqL([$"plan: returns to {Balanced}", "end task: BluettiWatcherEcoEnd registered"], Lines(r.Out).Skip(1));
        Eq(EcoFlag(untilMs, Balanced), ReadText(m.Flag));
        Ok(Has(m.TaskXml()!, $"-TaskUntil {Iso(untilMs)}</Arguments>"), m.TaskXml());
        Eq(Saver, m.Active()); // the plan itself is not touched
        EqL(["powercfg /list"], m.Calls().Where(c => c.StartsWith("powercfg", StringComparison.Ordinal)));
        Eq(0, await m.Code(["eco", "-Adopt", "-ReturnPlan", "high"]));
        Eq(High, (string?)m.Json().V("prevPlan"));
        Eq(0, await m.Code(["eco", "-Adopt", "-ReturnPlan", Balanced.ToUpperInvariant()]));
        Eq(Balanced, (string?)m.Json().V("prevPlan"));
        Eq(0, await m.Code(["off"]));
        Eq(Balanced, m.Active());
    }

    [Fact(DisplayName = "eco -Adopt refuses: no eco on, Power saver, a plan that is not there, no -ReturnPlan, an end time with it")]
    public async Task Eco_Adopt_refuses()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, plans: [Balanced, Saver], active: Saver);
        Eq(2, await m.Code(["eco", "-Adopt", "-ReturnPlan", "balanced"])); // no flag: no eco
        Assert.Empty(m.Files());
        string text = EcoFlag(Now() + 3600000);
        WriteText(m.Flag, text);
        string[][] cases = [["eco", "-Adopt"], ["eco", "-ReturnPlan", "balanced"], ["eco", "-Adopt", "-ReturnPlan", Saver], ["eco", "-Adopt", "-ReturnPlan", "high"],
            ["eco", "-Adopt", "-ReturnPlan", "fast"], ["eco", "-Adopt", "-ReturnPlan", "balanced", "-For", "30"], ["off", "-Adopt", "-ReturnPlan", "balanced"]];
        foreach (string[] args in cases)
        {
            Res r = await m.Run(args);
            Eq(2, r.Status, $"{string.Join(' ', args)}: {r.Out}");
            Match("^power mode: COULD NOT RUN: ", r.Out);
        }
        Eq(text, ReadText(m.Flag));
        Assert.Null(m.TaskXml());
    }
}

public sealed class PowerModeReturnPlanATests
{
    [Fact(DisplayName = "no config: every exit from eco puts Windows on High performance, whatever plan was active before")]
    public async Task No_config_every_exit_puts_Windows_on_High_performance()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, plans: All, active: Balanced, config: null);
        Eq(0, await m.Code(["eco"]));
        Eq(Obj(("prevPlan", Balanced), ("active", Saver)), Obj(("prevPlan", m.Json().V("prevPlan")), ("active", m.Active())));
        Eq($"plan: returns to High performance ({High}); before eco: Balanced ({Balanced})", Lines((await m.Run(["status"])).Out)[1]);
        Eq(new Res(0, "power mode: normal\nplan: High performance", ""), await m.Run(["off"]));
        Eq(Obj(("active", High), ("overlay", null), ("prevPlan", null)), Obj(("active", m.Active()), ("overlay", m.Overlay()), ("prevPlan", m.Json().V("prevPlan"))));
        // the station: eco, escalated to emergency, lifted by off -Source station (mains back, the page's Max)
        Machine s = NewMachine(t, plans: All, active: Balanced, config: null);
        string[][] seq = [["eco", "-Source", "station"], ["on", "-Source", "station"], ["off", "-Source", "station"]];
        foreach (string[] args in seq) Eq(0, await s.Code(args), string.Join(' ', args));
        Eq(Obj(("active", High), ("mode", "normal"), ("by", "station")), Obj(("active", s.Active()), ("mode", s.Json().V("mode")), ("by", s.Json().V("by"))));
        // the end task
        Machine e = NewMachine(t, plans: All, active: Saver, config: null);
        long untilMs = Now() - 60000;
        WriteText(e.Flag, EcoFlag(untilMs, Balanced));
        Eq(new Res(0, "power mode: normal (eco ended)\nplan: High performance", ""), await End51Async(e, ["-TaskUntil", Iso(untilMs)]));
        Eq(High, e.Active());
    }

    [Fact(DisplayName = "no config and no High performance plan on the machine: Balanced + Best performance, with a warn line")]
    public async Task No_config_and_no_High_performance_plan()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, plans: [Balanced, Saver], config: null);
        Eq(0, await m.Code(["eco"]));
        Eq(new Res(0, $"power mode: normal\nwarn: the power plan High performance ({High}) is not on this machine: Balanced is used instead\nplan: Balanced, Best performance", ""), await m.Run(["off"]));
        Eq(Obj(("active", Balanced), ("overlay", Best)), Obj(("active", m.Active()), ("overlay", m.Overlay())));
    }

    [Fact(DisplayName = "config returnPlan: balanced = Balanced + Best performance; a plan GUID = that plan; a GUID that is not here = Balanced, said so")]
    public async Task Config_returnPlan_variants()
    {
        await using Cleanup t = new();
        foreach ((string returnPlan, string want, string? overlay, bool warned) in new (string, string, string?, bool)[]
        {
            ("balanced", Balanced, Best, false), ("high", High, null, false), (Custom.ToUpperInvariant(), Custom, null, false), (Gone, Balanced, Best, true),
        })
        {
            Machine m = NewMachine(t, plans: All, active: High, config: Cfg(returnPlan));
            Eq(0, await m.Code(["eco"]));
            Res off = await m.Run(["off"]);
            Eq(0, off.Status, off.Out);
            Eq(warned, Regex.IsMatch(off.Out, "^warn: the power plan .* is not on this machine: Balanced is used instead$", RegexOptions.Multiline | RegexOptions.CultureInvariant), off.Out);
            Eq(Obj(("active", want), ("overlay", overlay), ("prevPlan", null)), Obj(("active", m.Active()), ("overlay", m.Overlay()), ("prevPlan", m.Json().V("prevPlan"))));
        }
    }

    [Fact(DisplayName = "config returnPlan previous: the plan active before eco comes back; none recorded, Power saver or a plan that is gone = High performance")]
    public async Task Config_returnPlan_previous()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, plans: All, active: Custom, config: Cfg("previous"));
        Eq(0, await m.Code(["eco"]));
        Eq(Custom, (string?)m.Json().V("prevPlan"));
        Eq($"plan: returns to a plan ({Custom})", Lines((await m.Run(["status"])).Out)[1]);
        Eq(0, await m.Code(["off"]));
        Eq(Obj(("active", Custom), ("overlay", null)), Obj(("active", m.Active()), ("overlay", m.Overlay())));
        foreach (string? prev in new string?[] { null, Saver, Gone })
        {
            Machine n = NewMachine(t, plans: All, active: Saver, config: Cfg("previous"));
            WriteText(n.Flag, EcoFlag(Now() + 3600000, prev));
            Match($@"^plan: returns to High performance \({High}\)", Lines((await n.Run(["status"])).Out)[1]);
            Eq(0, await n.Code(["off"]));
            Eq(High, n.Active(), prev ?? "undefined");
        }
    }
}

public sealed class PowerModeReturnPlanBTests
{
    [Fact(DisplayName = "eco started under Power saver never records Power saver: the plan to return to is recorded instead")]
    public async Task Eco_under_Power_saver_never_records_Power_saver()
    {
        await using Cleanup t = new();
        foreach ((string? config, string want) in new (string?, string)[] { (null, High), (Cfg("previous"), High), (Cfg(Custom), Custom) })
        {
            Machine m = NewMachine(t, plans: All, active: Saver, config: config);
            Res r = await m.Run(["eco"]);
            Eq(0, r.Status, r.Out);
            Eq(want, (string?)m.Json().V("prevPlan"));
            Ok(Has(r.Out, $"plan: Power saver was already on; returns to {want}"), r.Out);
            Ok(m.TaskXml() is not null, "no end task");
            Eq(0, await m.Code(["off"]));
            Eq(want, m.Active());
        }
    }

    [Fact(DisplayName = "an eco flag with no plan recorded (written before): off under Power saver sets the plan to return to; under another plan it changes nothing")]
    public async Task An_eco_flag_with_no_plan_recorded()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, plans: All, active: Saver, config: null);
        WriteText(m.Flag, EcoFlag(Now() + 3600000));
        Eq($"plan: returns to High performance ({High})", Lines((await m.Run(["status"])).Out)[1]);
        Eq(0, await m.Code(["off"]));
        Eq(High, m.Active());
        Machine n = NewMachine(t, plans: All, active: Balanced, config: null);
        WriteText(n.Flag, EcoFlag(Now() + 3600000));
        Eq("plan: nothing to return to", Lines((await n.Run(["status"])).Out)[1]);
        Eq(new Res(0, "power mode: normal", ""), await n.Run(["off"]));
        Eq(Balanced, n.Active());
        Assert.DoesNotContain(n.Calls(), c => c.Contains("setactive", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "a config that cannot be read, or a returnPlan that is no plan: a warn line, the defaults apply (High performance, GPU cap on), exit 0")]
    public async Task A_config_that_cannot_be_read()
    {
        await using Cleanup t = new();
        foreach (string config in new[] { "{not json", """{"returnPlan":"fast"}""", """{"returnPlan":"saver"}""" })
        {
            Machine m = NewMachine(t, plans: All, active: Balanced, config: config, gpu: [250, 100, 250]);
            Res r = await m.Run(["eco"]);
            Eq(0, r.Status, r.Out);
            Match(@"^warn: power-mode\.config\.json", r.Out, RegexOptions.Multiline);
            Eq("prev=250 gpu=100", Obj(("prev", m.Json().V("prevGpuLimitW")), ("gpu", m.Gpu())));
            Eq(0, await m.Code(["off"]));
            Eq(Obj(("active", High), ("gpu", 250)), Obj(("active", m.Active()), ("gpu", m.Gpu())));
        }
    }

    [Fact(DisplayName = "prev-plan rewrites the plan an eco that is on remembers; since, until and the active plan stay")]
    public async Task Prev_plan_rewrites_the_plan()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, plans: All, active: Saver, config: Cfg("previous"));
        long untilMs = Now() + 3600000;
        WriteText(m.Flag, Amend(EcoFlag(untilMs, Balanced), Owed(250, 100)));
        Res r = await m.Run(["prev-plan", "high"]);
        Eq(0, r.Status, r.Out);
        Eq($"plan: returns to High performance ({High})", Lines(r.Out)[1]);
        Eq(Amend(EcoFlag(untilMs, High), Owed(250, 100)), ReadText(m.Flag));
        Eq(Saver, m.Active());
        Eq($"plan: returns to High performance ({High})", Lines((await m.Run(["status"])).Out)[1]);
        foreach ((string value, string want) in new[] { ("balanced", Balanced), (Custom.ToUpperInvariant(), Custom) })
        {
            Eq(0, await m.Code(["prev-plan", value]), value);
            Eq(want, (string?)m.Json().V("prevPlan"));
        }
        Assert.DoesNotContain(m.Calls(), c => c.Contains("setactive", StringComparison.Ordinal));
        Eq(0, await m.Code(["off"]));
        Eq(Custom, m.Active());
    }
}

public sealed class PowerModeGpuATests
{
    [Fact(DisplayName = "prev-plan with no eco on exits 1 and changes nothing; a value that is no plan, Power saver, or a plan that is not here, exits 2")]
    public async Task Prev_plan_with_no_eco_on()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, plans: [Balanced, Saver, High], active: Saver);
        Eq(new Res(1, "power mode: prev-plan: no eco is on, nothing changed", ""), await m.Run(["prev-plan", "high"]));
        Assert.Empty(m.Files());
        foreach (string text in new[] { OldNormal, $"{{ \"mode\": \"emergency\", \"since\": \"{OldSince}\", \"prevPlan\": \"{Balanced}\" }}\n", EcoFlag(Now() - 60000, Balanced) })
        {
            WriteText(m.Flag, text);
            Eq(1, await m.Code(["prev-plan", "high"]), text);
            Eq(text, ReadText(m.Flag));
        }
        string keep = EcoFlag(Now() + 3600000, Balanced);
        WriteText(m.Flag, keep);
        string[][] cases = [["prev-plan"], ["prev-plan", "fast"], ["prev-plan", "saver"], ["prev-plan", Saver], ["prev-plan", Gone]];
        foreach (string[] args in cases)
        {
            Res r = await m.Run(args);
            Eq(2, r.Status, $"{string.Join(' ', args)}: {r.Out}");
            Match("^power mode: COULD NOT RUN: ", r.Out);
        }
        Eq(keep, ReadText(m.Flag));
    }

    [Fact(DisplayName = "config gpuCap false: eco and off never call nvidia-smi")]
    public async Task Config_gpuCap_false()
    {
        await using Cleanup t = new();
        foreach (string config in new[] { """{"gpuCap":false}""", """{"gpuCap":false,"returnPlan":"balanced"}""" })
        {
            Machine m = NewMachine(t, plans: All, gpu: [250, 100, 250], config: config);
            Eq(0, await m.Code(["eco"]));
            Assert.False(m.Json().ContainsKey("prevGpuLimitW"));
            Eq(0, await m.Code(["off"]));
            Assert.Empty(m.GpuCalls());
            Eq(250, m.Gpu());
        }
    }

    [Fact(DisplayName = "the GPU cap, on unless the config says no: eco sets the minimum power limit and remembers the old one; a second eco, on and battery keep it; off puts it back and forgets it")]
    public async Task The_GPU_cap_on_unless_the_config_says_no()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, gpu: [250, 100, 250]);
        Res r = await m.Run(["eco", "-For", "30"]);
        Eq(0, r.Status, r.Out);
        Ok(Lines(r.Out).Contains("gpu cap: 100 W (returns to 250 W)"), r.Out);
        Eq("prev=250 cap=100 gpu=100", Obj(("prev", m.Json().V("prevGpuLimitW")), ("cap", m.Json().V("gpuCapW")), ("gpu", m.Gpu())));
        EqL([GpuQuery, "nvidia-smi -pl 100"], m.GpuCalls());
        Ok(Lines((await m.Run(["status"])).Out).Contains("gpu cap: 100 W (returns to 250 W)"));
        Eq(0, await m.Code(["eco", "-For", "90"])); // the limit now reads 100: it must not become the one to return to
        Eq($"prev=250 prevPlan={Balanced}", Obj(("prev", m.Json().V("prevGpuLimitW")), ("prevPlan", m.Json().V("prevPlan"))));
        Eq(0, await m.Code(["battery", "-Soc", "60", "-Level", "ok", "-Mains", "off"]));
        Eq(0, await m.Code(["on"])); // eco escalated to emergency: the cap and the debt stay
        Eq("mode=emergency prev=250 cap=100 gpu=100", Obj(("mode", m.Json().V("mode")), ("prev", m.Json().V("prevGpuLimitW")), ("cap", m.Json().V("gpuCapW")), ("gpu", m.Gpu())));
        Res off = await m.Run(["off"]);
        Eq(new Res(0, "power mode: normal\nplan: Balanced, Best performance\ngpu: power limit back to 250 W", ""), off);
        Eq(250, m.Gpu());
        Eq("prev=undefined cap=undefined", Obj(("prev", m.Json().V("prevGpuLimitW")), ("cap", m.Json().V("gpuCapW"))));
        // LiveQuery: the status above, then the drift checks of the second eco and of the battery reading (the limit read the cap: nothing set)
        EqL([GpuQuery, "nvidia-smi -pl 100", LiveQuery, LiveQuery, LiveQuery, "nvidia-smi -pl 250"], m.GpuCalls());
        Eq(new Res(0, "power mode: normal (already off, since kept)", ""), await m.Run(["off"]));
        Eq(6, m.GpuCalls().Length);
    }

    [Fact(DisplayName = "the GPU cap, the station: eco -Source station caps; escalated to emergency and lifted by off -Source station, the limit is back")]
    public async Task The_GPU_cap_the_station()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, gpu: [200, 100, 250]);
        Eq(0, await m.Code(["eco", "-Source", "station"]));
        Eq("prev=200 gpu=100", Obj(("prev", m.Json().V("prevGpuLimitW")), ("gpu", m.Gpu())));
        Match(@"\(already on, kept\)\z", (await m.Run(["eco", "-Source", "station"])).Out);
        Eq("200", Fmt(m.Json().V("prevGpuLimitW")));
        Eq(0, await m.Code(["on", "-Source", "station"]));
        Eq("prev=200 gpu=100", Obj(("prev", m.Json().V("prevGpuLimitW")), ("gpu", m.Gpu())));
        Res off = await m.Run(["off", "-Source", "station"]);
        Eq(0, off.Status, off.Out);
        Eq("gpu=200 by=station prev=undefined", Obj(("gpu", m.Gpu()), ("by", m.Json().V("by")), ("prev", m.Json().V("prevGpuLimitW"))));
    }
}

public sealed class PowerModeGpuBTests
{
    [Fact(DisplayName = "the end task under Windows PowerShell 5.1 puts the GPU limit back and drops the keys")]
    public async Task The_end_task_under_5_1_puts_the_GPU_limit_back()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, active: Saver, gpu: [100, 100, 250]);
        long untilMs = Now() - 60000;
        WriteText(m.Flag, Amend(EcoFlag(untilMs, Balanced), Owed(250, 100)));
        Res r = await End51Async(m, ["-TaskUntil", Iso(untilMs)], m.Env);
        Eq(new Res(0, "power mode: normal (eco ended)\nplan: Balanced, Best performance\ngpu: power limit back to 250 W", ""), r);
        Eq(250, m.Gpu());
        Eq($"{{ \"mode\": \"normal\", \"since\": \"{Iso(untilMs)}\" }}\n", ReadText(m.Flag));
    }

    [Fact(DisplayName = "the GPU never fails eco or off: no nvidia-smi, a failing query, a refused set are notes, exit 0; a refused restore stays owed for the next off")]
    public async Task The_GPU_never_fails_eco_or_off()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, gpu: [250, 100, 250]);
        Res r = await m.Run(["eco"], Env(("POWER_MODE_NVIDIASMI", Path.Combine(m.Sim, "no-such-nvidia-smi.ps1"))));
        Eq(0, r.Status, r.Out);
        Match(@"^note: gpu cap not set \(", r.Out, RegexOptions.Multiline);
        Assert.False(m.Json().ContainsKey("prevGpuLimitW"));
        m = NewMachine(t, gpu: [250, 100, 250]);
        r = await m.Run(["eco"], Env(("FAKE_GPU_QUERYFAIL", "1")));
        Eq(0, r.Status, r.Out);
        Match(@"^note: gpu cap not set \(NVIDIA-SMI has failed", r.Out, RegexOptions.Multiline);
        Eq($"has=false gpu=250 mode=eco plan={Saver}", Obj(("has", m.Json().ContainsKey("prevGpuLimitW")), ("gpu", m.Gpu()), ("mode", m.Json().V("mode")), ("plan", m.Active())));
        m = NewMachine(t, gpu: [250, 100, 250]);
        r = await m.Run(["eco"], Env(("FAKE_GPU_SETFAIL", "1")));
        Eq(0, r.Status, r.Out);
        Match(@"^note: gpu cap not set \(Changing power management limit is not permitted: Insufficient Permissions\)$", r.Out, RegexOptions.Multiline);
        Eq("has=false cap=false gpu=250 mode=eco", Obj(("has", m.Json().ContainsKey("prevGpuLimitW")), ("cap", m.Json().ContainsKey("gpuCapW")), ("gpu", m.Gpu()), ("mode", m.Json().V("mode"))));
        Eq(0, await m.Code(["off"]));
        m = NewMachine(t, gpu: [250, 100, 250]);
        Eq(0, await m.Code(["eco"]));
        r = await m.Run(["off"], Env(("FAKE_GPU_SETFAIL", "1")));
        Eq(0, r.Status, r.Out);
        Match(@"^note: the GPU power limit could not be set back to 250 W \(.*\): it stays owed in the flag, run off again$", r.Out, RegexOptions.Multiline);
        Eq($"mode=normal prev=250 gpu=100 plan={Balanced}", Obj(("mode", m.Json().V("mode")), ("prev", m.Json().V("prevGpuLimitW")), ("gpu", m.Gpu()), ("plan", m.Active())));
        Eq(0, await m.Code(["off"]));
        Eq("prev=undefined gpu=250", Obj(("prev", m.Json().V("prevGpuLimitW")), ("gpu", m.Gpu())));
    }

    [Fact(DisplayName = "an eco flag with no GPU limit recorded (written before): off never touches the GPU, one note says so")]
    public async Task An_eco_flag_with_no_GPU_limit_recorded()
    {
        await using Cleanup t = new();
        foreach (int[] gpu in new int[][] { [100, 100, 250], [200, 100, 250] })
        {
            Machine m = NewMachine(t, active: Saver, gpu: gpu);
            WriteText(m.Flag, EcoFlag(Now() + 3600000, Balanced));
            Eq(new Res(0, "power mode: normal\nplan: Balanced, Best performance\nnote: gpu: this eco recorded no power limit to return to: the GPU is left as it is", ""), await m.Run(["off"]));
            Eq($"gpu={gpu[0]} calls=0", Obj(("gpu", m.Gpu()), ("calls", m.GpuCalls().Length)));
        }
    }

    [Fact(DisplayName = "off when eco is not on changes neither the plan nor the GPU")]
    public async Task Off_when_eco_is_not_on_changes_neither_the_plan_nor_the_GPU()
    {
        await using Cleanup t = new();
        foreach (string? text in new string?[] { null, OldNormal, OldEmergency })
        {
            Machine m = NewMachine(t, plans: All, active: Saver, gpu: [100, 100, 250], config: null);
            if (text is not null) WriteText(m.Flag, text);
            Eq(0, await m.Code(["off"]));
            Assert.Empty(m.Calls());
            Eq($"active={Saver} gpu=100", Obj(("active", m.Active()), ("gpu", m.Gpu())));
        }
    }
}
