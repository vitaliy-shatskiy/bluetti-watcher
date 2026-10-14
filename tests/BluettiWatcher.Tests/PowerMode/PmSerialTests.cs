// The five tests that wait on a wall-clock window (a reader holding the flag open, a lock held for a set time, a tool
// timeout, a process that must still be alive). They run alone, one by one, after every parallel collection: this class is in
// the one collection with parallelization off (the JS test.serial). A port of power-mode/power-mode.test.js, test for test.
using System.Diagnostics;
using static BluettiWatcher.Tests.PowerModeScript.Pm;

namespace BluettiWatcher.Tests.PowerModeScript;

[Collection(PowerModeSerialGroup.Name)]
public sealed class PowerModeSerialTests
{
    [Fact(DisplayName = "on while a reader holds the flag open: retried until the reader lets go, exit 0")]
    public async Task On_while_a_reader_holds_the_flag_open()
    {
        await using Cleanup t = new();
        World w = NewWorld(t);
        WriteText(w.Flag, OldNormal);
        using Holder h = new(w.Flag, 900);
        await h.Opened;
        Res r = await CliAsync(["on", "-Path", w.Flag]);
        await h.Done;
        Eq(0, r.Status, r.Out);
        Match(EmergencyRe, r.Out);
        Eq("emergency", FlagRe.Match(ReadText(w.Flag)).Groups[1].Value);
        EqL(["power-mode.json"], Ls(w.Dir));
    }

    [Fact(DisplayName = "on while a reader never lets go: exit 2 naming the cause, the temp file removed, the flag unchanged")]
    public async Task On_while_a_reader_never_lets_go()
    {
        await using Cleanup t = new();
        World w = NewWorld(t);
        WriteText(w.Flag, OldNormal);
        using Holder h = new(w.Flag, 6000);
        await h.Opened;
        Res r = await CliAsync(["on", "-Path", w.Flag]);
        h.Kill();
        await h.Done;
        Eq(2, r.Status, r.Out);
        Match(@"^power mode: COULD NOT RUN: the flag file could not be replaced in 10 tries \(held open by another process\?\): ", r.Out);
        EqL(["power-mode.json"], Ls(w.Dir));
        Eq(OldNormal, ReadText(w.Flag));
    }

    [Fact(DisplayName = "the flag lock: a manual on made while the battery write sits between its read and its write survives")]
    public async Task The_flag_lock_a_manual_on_survives_a_battery_write()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t);
        Dictionary<string, string> env = Merge(BaseEnv(), Env(("FAKE_PM_DIR", m.Sim), ("POWER_MODE_POWERCFG", Path.Combine(m.Sim, "fake-powercfg.ps1")),
            ("POWER_MODE_SCHTASKS", Path.Combine(m.Sim, "fake-schtasks.ps1")), ("POWER_MODE_TEST_HOLD_MS", "1500"), ("POWER_MODE_TEST", "1")));
        env.Remove("POWER_MODE_FILE");
        using Process child = StartCli(["battery", "-Soc", "50", "-Level", "ok", "-Mains", "off", "-Path", m.Flag], env);
        string held = $"{m.Flag}.held";
        for (int i = 0; i < 200 && !File.Exists(held); i++) await Task.Delay(50, TestContext.Current.CancellationToken);
        Ok(File.Exists(held), "the battery write never reached its hold");
        Res on = await m.Run(["on"]); // the user's on, while the battery write has read the flag and not written it yet
        await child.WaitForExitAsync(TestContext.Current.CancellationToken);
        Eq(0, child.ExitCode);
        Eq(0, on.Status, on.Out);
        var j = m.Json();
        Eq("mode=emergency soc=50", Obj(("mode", j.V("mode")), ("soc", j.V("battery", "soc"))));
    }

    [Fact(DisplayName = "the flag mutex is released on every exit of power-mode.ps1, seen from another process")]
    public async Task The_flag_mutex_is_released_on_every_exit()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t);
        string mutex = UniqueMutex();
        Dictionary<string, string> env = Merge(BaseEnv(), Env(("FAKE_PM_DIR", m.Sim), ("POWER_MODE_POWERCFG", Path.Combine(m.Sim, "fake-powercfg.ps1")),
            ("POWER_MODE_SCHTASKS", Path.Combine(m.Sim, "fake-schtasks.ps1")), ("POWER_MODE_TEST", "1"), ("POWER_MODE_MUTEX", mutex)));
        env.Remove("POWER_MODE_FILE");
        // pwsh -File: an exit 2 inside the locked region (a bad -Soc), then an exit 0 at the end
        string[][] calls = [["battery", "-Soc", "bad", "-Level", "ok", "-Mains", "off"], ["on"]];
        foreach (string[] args in calls)
        {
            Res r = await SpawnCliAsync([.. args, "-Path", m.Flag], env);
            Eq(args[0] == "on" ? 0 : 2, r.Status, r.Out);
            Eq("free", await ProbeMutex(mutex), $"after pwsh -File {args[0]}");
        }
        // & power-mode.ps1 in a process that lives on afterwards (as the poller calls it): released before that process ends
        string marker = Path.Combine(m.Dir, "called");
        string host = $"{CliCallInHost} battery -Soc bad -Level ok -Mains off -Path '{m.Flag}' | Out-Null; {CliCallInHost} off -Path '{m.Flag}' | Out-Null; [IO.File]::WriteAllText('{marker}', 'x'); Start-Sleep -Seconds 6";
        using Process child = StartDrained("pwsh", ["-NoProfile", "-NonInteractive", "-Command", host], env);
        for (int i = 0; i < 200 && !File.Exists(marker); i++) await Task.Delay(50, TestContext.Current.CancellationToken);
        Ok(File.Exists(marker), "the host never got past its calls");
        Eq("free", await ProbeMutex(mutex), "held by a live process after & power-mode.ps1 returned");
        await child.WaitForExitAsync(TestContext.Current.CancellationToken);
    }

    [Fact(DisplayName = "a tool that hangs is killed after POWER_MODE_TOOL_TIMEOUT_MS: a note, exit 0, the flag lock released; a child-process stand-in works as the tool")]
    public async Task A_tool_that_hangs_is_killed_after_the_timeout()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t, gpu: [250, 100, 250]);
        string cmd = Path.Combine(m.Sim, "fake-nvidia-smi.cmd");
        WriteText(cmd, string.Join("\r\n", ["@echo off", """if "%FAKE_SLEEP%"=="1" ping -n 11 127.0.0.1 >nul""", """if "%1"=="-pl" (echo %2>"%FAKE_PM_DIR%\cmd-pl.txt" & exit /b 0)""", "echo 250.00, 100.00, 250.00", ""]));
        string mutex = UniqueMutex();
        Res ok = await m.Run(["eco"], Env(("POWER_MODE_NVIDIASMI", cmd), ("POWER_MODE_MUTEX", mutex)));
        Eq(0, ok.Status, ok.Out);
        Ok(Lines(ok.Out).Contains("gpu cap: 100 W (returns to 250 W)"), ok.Out);
        Eq("100", ReadText(Path.Combine(m.Sim, "cmd-pl.txt")).Trim());
        Machine n = NewMachine(t, gpu: [250, 100, 250]);
        long started = Now();
        Res r = await n.Run(["eco"], Env(("POWER_MODE_NVIDIASMI", cmd), ("FAKE_SLEEP", "1"), ("POWER_MODE_TOOL_TIMEOUT_MS", "500"), ("POWER_MODE_MUTEX", mutex)));
        long took = Now() - started;
        Eq(0, r.Status, r.Out);
        Match(@"^note: gpu cap not set \(fake-nvidia-smi\.cmd timed out after 500 ms \(stopped\)\)$", r.Out, System.Text.RegularExpressions.RegexOptions.Multiline);
        Ok(took < 8000, $"took {took} ms");
        Eq("eco", (string?)n.Json().V("mode"));
        Eq("free", await ProbeMutex(mutex));
    }
}
