// The review's fix round on the exe switch: the CLI contract of 1.0.0's scripts for -Switch:$false, abbreviated names and an
// unexpected failure, through the exe and (PM_SWITCH_ENTRY=script) the forwarders. Every flag is a private temp file.
using static BluettiWatcher.Tests.PowerModeScript.Pm;

namespace BluettiWatcher.Tests.PowerModeScript;

public sealed class PowerModeFixRoundTests
{
    /// <summary>The call as a PowerShell host makes it (<c>&amp; power-mode.ps1 ...</c>): -Switch:$false reaches a script's $args as `-Switch:` then `False`.</summary>
    private static async Task<Res> HostCallAsync(Machine m, string argsText)
    {
        Dictionary<string, string> env = Merge(BaseEnv(), m.Env);
        env.Remove("POWER_MODE_FILE");
        env["POWER_MODE_TZ"] = PinTz;
        env["POWER_MODE_ELEVATED"] = "1";
        env["POWER_MODE_TEST"] = "1";
        string host = $"{CliCallInHost} {argsText} -Path '{m.Flag}'; exit $LASTEXITCODE";
        return Norm(await SpawnAsync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", host], env));
    }

    private static Machine EcoMachine(Cleanup t) => NewMachine(t, [Balanced, Saver, High], Balanced);

    [Fact(DisplayName = "eco -Adopt:$false -ReturnPlan high is refused as in 1.0.0 (exit 2, the flag unchanged), as a host call and as a -File argument")]
    public async Task Adopt_false_with_a_return_plan_is_refused()
    {
        await using Cleanup t = new();
        Machine m = EcoMachine(t);
        Eq(0, await m.Code(["eco"]));
        string before = ReadText(m.Flag);
        Res host = await HostCallAsync(m, "eco -Adopt:$false -ReturnPlan high");
        Eq(2, host.Status, host.Out + host.Err);
        Match("^power mode: COULD NOT RUN: pass -Adopt with -ReturnPlan", host.Out);
        Eq(before, ReadText(m.Flag));
        Res file = await m.Run(["eco", "-Adopt:$false", "-ReturnPlan", "high"]);
        Eq(2, file.Status, file.Out + file.Err);
        Match("^power mode: COULD NOT RUN: pass -Adopt with -ReturnPlan", file.Out);
        Eq(before, ReadText(m.Flag));
    }

    [Fact(DisplayName = "eco -Adopt:$true -ReturnPlan high adopts the plan, as a host call and as a -File argument; False never lands in Value")]
    public async Task Adopt_true_adopts()
    {
        await using Cleanup t = new();
        Machine m = EcoMachine(t);
        Eq(0, await m.Code(["eco"]));
        Eq(Balanced, (string?)m.Json().V("prevPlan"));
        Res host = await HostCallAsync(m, "eco -Adopt:$true -ReturnPlan high");
        Eq(0, host.Status, host.Out + host.Err);
        Eq(High, (string?)m.Json().V("prevPlan"));
        Res file = await m.Run(["eco", "-Adopt:$true", "-ReturnPlan", "balanced"]);
        Eq(0, file.Status, file.Out + file.Err);
        Eq(Balanced, (string?)m.Json().V("prevPlan"));
        // -Adopt:$false on a plain `off` is no switch given: the call is an off, exit 0.
        Res off = await HostCallAsync(m, "off -Adopt:$false");
        Eq(0, off.Status, off.Out + off.Err);
    }

    [Fact(DisplayName = "an abbreviation 1.0.0 accepted still resolves to the same name: -F, -M, -T, -Fo, -Ma, -Ta; a long name added since is spelled out")]
    public async Task Abbreviations_of_1_0_0_resolve_as_before()
    {
        await using Cleanup t = new();
        Machine m = EcoMachine(t);
        // -F = -For, -M = -Mains, -T = -TaskUntil: each ignored by status, none ambiguous.
        foreach (string[] call in new string[][] { ["status", "-F", "30"], ["status", "-M", "on"], ["status", "-T", "x"], ["status", "-Ta", "x"], ["status", "-Ma", "on"], ["status", "-Fo", "5"] })
        {
            Res r = await m.Run(call);
            Eq(0, r.Status, $"{string.Join(' ', call)}: {r.Out}");
            Eq("power mode: normal", r.Out);
        }
        Res eco = await m.Run(["eco", "-F", "30"]);
        Eq(0, eco.Status, eco.Out);
        Ok(Math.Abs(MinutesAhead(m.Json()) - 30) < 2, "eco -F 30 is -For 30");
        Res battery = await m.Run(["battery", "-Soc", "50", "-Le", "ok", "-M", "off"]);
        Eq(new Res(0, "battery: 50 % (ok, mains off)", ""), new Res(battery.Status, Lines(battery.Out)[0], ""));
        // The names added since 1.0.0 are exact (or a prefix no 1.0.0 name starts with) and go with end only.
        foreach (string name in new[] { "-FlagFile", "-Fl", "-Mutex", "-Mu", "-TaskPrefix", "-TaskP", "-ExtraReaders", "-E" })
        {
            Res r = await m.Run(["status", name, "x"]);
            Eq(2, r.Status, name + ": " + r.Out);
            Eq("power mode: COULD NOT RUN: -FlagFile, -Mutex, -TaskPrefix and -ExtraReaders go with end", r.Out);
        }
    }

    [Fact(DisplayName = "an unexpected failure ends the command with the scripts' COULD NOT RUN and exit 2, never a crash")]
    public async Task An_unexpected_failure_is_exit_2()
    {
        await using Cleanup t = new();
        Machine m = EcoMachine(t);
        Res r = await m.Run(["status"], Env(("POWER_MODE_TEST_FAULT", "unexpected")));
        Eq(2, r.Status, r.Out + r.Err);
        Match("^power mode: COULD NOT RUN: ", r.Out);
        Eq("", r.Err);
    }

    [Fact(DisplayName = "an extra reader whose name cannot be translated (the lookup throws) is left out as in the scripts: the command runs")]
    public async Task An_extra_reader_that_cannot_be_looked_up_is_left_out()
    {
        await using Cleanup t = new();
        World w = NewWorld(t);
        string flag = Path.Combine(w.Dir, "pd", "BluettiWatcher", "power-mode.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetDirectoryName(flag))!);
        t.After(() => Icacls(w.Dir, "/reset", "/T", "/Q"));
        Res r = await CliAsync(["on"], flag, Env(("POWER_MODE_EXTRA_READERS", "somebody"), ("POWER_MODE_TEST_FAULT", "translate")));
        Eq(0, r.Status, r.Out + r.Err);
        Match("^power mode: EMERGENCY since ", r.Out);
    }
}
