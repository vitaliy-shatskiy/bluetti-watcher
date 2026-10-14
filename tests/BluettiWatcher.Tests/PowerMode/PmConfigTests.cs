// The configured flag's own folder: created with its own ACL, checked (never changed) before every write and read; the names an
// install configures; the end task's command line. A port of power-mode/power-mode.test.js, test for test.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static BluettiWatcher.Tests.PowerModeScript.Pm;

namespace BluettiWatcher.Tests.PowerModeScript;

public sealed class PowerModeNamesTests
{
    // Both tests below use the fixed mutex name Local\MyPowerModeFlag: one class, so one after the other (the JS test.sharing).
    [Fact(DisplayName = "names from the environment: POWER_MODE_FILE / _MUTEX / _TASK_PREFIX / _EXTRA_READERS set to an install's values give exactly its names")]
    public async Task Names_from_the_environment()
    {
        await using Cleanup t = new();
        // The lock probe (MutexProbe) under the configured name, for a process the library starts while it holds the lock.
        string probe = MutexProbe.Replace("Local\\BluettiWatcherPowerMode", "Local\\MyPowerModeFlag", StringComparison.Ordinal);
        Assert.NotEqual(MutexProbe, probe);
        FlagLib l = NewFlagLib(t, Names, false, Merge(Compat(), Env(("PROBE", probe))));
        Res r = await l.Run();
        Eq(0, r.Status, r.Err);
        EqL(["C:\\ProgramData\\MyPowerMode\\power-mode.json", "Local\\MyPowerModeFlag", "MyEcoMode", "MyEcoModeEnd", "MyEcoModeGpuCap", "MyEcoModeGpuRestore", "C:\\ProgramData\\MyPowerMode\\power-mode.json"], Lines(r.Out).Take(7));
        // The lock: held under the configured name while a writer holds it, free after (seen from another process).
        Res lck = await l.Run("$m = Enter-PowerModeFlagLock ''; & pwsh -NoProfile -NonInteractive -Command $env:PROBE; Exit-PowerModeFlagLock $m; & pwsh -NoProfile -NonInteractive -Command $env:PROBE");
        Eq(0, lck.Status, lck.Err);
        Eq("held\nfree", lck.Out);
        // The GPU task as gpu-task install would register it under the configured prefix.
        Match(@"<Description>power-mode\.ps1: GPU power limit 100 W \(MyEcoModeGpuCap\)</Description>", (await l.Run("New-GpuTaskXml $script:GpuCapTask 100")).Out);
        // The switch and the end script against a simulated machine (-Path keeps the configured flag untouched).
        Machine m = NewMachine(t);
        Eq(0, await m.Code(["eco"], Compat()));
        Match(@"^schtasks /create /tn MyEcoModeEnd /xml \S+ /f\z", m.Calls().First(c => c.StartsWith("schtasks /create", StringComparison.Ordinal)));
        Ok(m.TaskXml() is null && File.Exists(Path.Combine(m.Sim, "task-MyEcoModeEnd.xml")));
        // The task's command line carries every name: the Task Scheduler passes no environment (CliAsync drops POWER_MODE_FILE).
        string endXml = ReadUtf16(Path.Combine(m.Sim, "task-MyEcoModeEnd.xml"));
        Ok(Has(endXml, $"<Arguments>power-mode end -Path &quot;{m.Flag}&quot; -FlagFile &quot;{Path.Combine(ProgramData, "BluettiWatcher", "power-mode.json")}&quot; -Mutex Local\\MyPowerModeFlag -TaskPrefix MyEcoMode -ExtraReaders &quot;mypmreader&quot; -TaskUntil {m.Json().V("until")}</Arguments>"), endXml);
        Eq("end task: MyEcoModeEnd present", Lines((await m.Run(["status"], Compat())).Out)[^1]);
        long untilMs = Now() - 60000;
        WriteText(m.Flag, EcoFlag(untilMs, Balanced));
        Res end = await End51Async(m, ["-TaskUntil", Iso(untilMs)], Compat());
        Eq(0, end.Status, end.Out + end.Err);
        Ok(m.Calls().Contains("schtasks /delete /tn MyEcoModeEnd /f"), string.Join('\n', m.Calls()));
    }

    [Fact(DisplayName = "the end task under 5.1 with no POWER_MODE_* names in its environment takes them from its command line")]
    public async Task The_end_task_under_5_1_takes_the_names_from_its_command_line()
    {
        await using Cleanup t = new();
        foreach (string k in NameVarList) Assert.False(BaseEnv().ContainsKey(k), k);
        Machine m = NewMachine(t, active: Balanced, gpu: [250, 100, 250]);
        string guarded = Path.Combine(m.Dir, "guarded");
        Directory.CreateDirectory(guarded);
        Res acl = await SpawnAsync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", $"{AclPs}Set-TestAcl $env:GUARDED 'Modify' @('S-1-5-32-545=ReadAndExecute')"], Merge(BaseEnv(), Env(("GUARDED", guarded))));
        Eq(0, acl.Status, acl.Out + acl.Err);
        string flag = Path.Combine(guarded, "power-mode.json");
        WriteText(Path.Combine(guarded, "power-mode.config.json"), Cfg("balanced"));
        Dictionary<string, string> names = Env(("POWER_MODE_MUTEX", "Local\\MyPowerModeFlag"), ("POWER_MODE_TASK_PREFIX", "MyEcoMode"), ("POWER_MODE_EXTRA_READERS", "mypmreader, Users"));
        // The switch, configured: the guarded folder passes its check only with the extra reader named.
        Res eco = await CliAsync(["eco"], flag, Merge(m.Env, names));
        Eq(0, eco.Status, eco.Out);
        Eq(2, (await CliAsync(["status"], flag, Merge(Merge(m.Env, names), Env(("POWER_MODE_EXTRA_READERS", ""))))).Status);
        string[] argv = TaskArgv(ReadUtf16(Path.Combine(m.Sim, "task-MyEcoModeEnd.xml")));
        EqL(["-Path", flag, "-FlagFile", flag, "-Mutex", "Local\\MyPowerModeFlag", "-TaskPrefix", "MyEcoMode", "-ExtraReaders", "mypmreader,Users"], argv.Take(10));
        // The eco is due; the end task's own and the default-named tasks are both there, the GPU restore task under the prefix.
        JsonObject j = ParseObject(ReadText(flag));
        Eq("250", Fmt(j.V("prevGpuLimitW")));
        long untilMs = Now() - 60000;
        j["since"] = Iso(untilMs - 3600000);
        j["until"] = Iso(untilMs);
        WriteText(flag, ToJson(j));
        argv[Array.IndexOf(argv, "-TaskUntil") + 1] = Iso(untilMs);
        WriteText(Path.Combine(m.Sim, "task-BluettiWatcherEcoEnd.xml"), "not this one");
        WriteText(Path.Combine(m.Sim, "task-MyEcoModeGpuRestore.xml"), GpuTaskXml(250));
        WriteText(Path.Combine(m.Sim, "calls.log"), "");
        Dictionary<string, string> env = Merge(m.Env, Env(("POWER_MODE_ELEVATED", "0"), ("FAKE_PM_MUTEX", "Local\\MyPowerModeFlag")));
        // Control: without the extra reader the configured flag's folder is not the user's - the flag is untrusted, nothing changes.
        string[] noReaders = argv.Where((a, i) => a != "-ExtraReaders" && (i == 0 || argv[i - 1] != "-ExtraReaders")).ToArray();
        Res refused = await End51Async(m, [], env, noReaders);
        Eq(2, refused.Status, refused.Out + refused.Err);
        Match("^power mode: COULD NOT READ ", refused.Out);
        // A reader the user's saved environment names is not the task's: with -FlagFile given and no -ExtraReaders, none are trusted.
        Res stray = await End51Async(m, [], Merge(env, Env(("POWER_MODE_EXTRA_READERS", "Users"))), noReaders);
        Eq(2, stray.Status, stray.Out + stray.Err);
        Match("^power mode: COULD NOT READ ", stray.Out);
        Res r = await End51Async(m, [], env, argv);
        Eq(0, r.Status, r.Out + r.Err);
        Eq("power mode: normal (eco ended)", Lines(r.Out)[0]);
        Match("^gpu: power limit back to 250 W$", r.Out, RegexOptions.Multiline);
        string[] calls = m.Calls();
        string all = string.Join('\n', calls);
        Ok(calls.Contains("schtasks /delete /tn MyEcoModeEnd /f"), all);
        Ok(calls.Contains("schtasks /run /tn MyEcoModeGpuRestore"), all);
        Ok(!calls.Any(c => c.Contains("BluettiWatcherEco", StringComparison.Ordinal)), all);
        Ok(calls.Contains("mutex Local\\MyPowerModeFlag held") && !calls.Any(c => c.EndsWith(" free", StringComparison.Ordinal)), all);
        Assert.False(File.Exists(Path.Combine(m.Sim, "task-MyEcoModeEnd.xml")));
        Eq("not this one", ReadText(Path.Combine(m.Sim, "task-BluettiWatcherEcoEnd.xml")));
        Eq(250, m.Gpu());
        Eq("normal", (string?)ParseObject(ReadText(flag)).V("mode"));
    }

    [Fact(DisplayName = "a POWER_MODE_TASK_PREFIX that is not a plain name: exit 2, nothing written")]
    public async Task A_task_prefix_that_is_not_a_plain_name()
    {
        await using Cleanup t = new();
        World w = NewWorld(t);
        foreach (string prefix in new[] { "a\\b", "x y", "\"q" })
        {
            Res r = await CliAsync(["on", "-Path", w.Flag], null, Env(("POWER_MODE_TASK_PREFIX", prefix)));
            Eq(2, r.Status, r.Out);
            Match("^power mode: COULD NOT RUN: POWER_MODE_TASK_PREFIX ", r.Out);
        }
        Assert.Empty(Ls(w.Dir));
    }
}

public sealed class PowerModeFlagLibATests
{
    [Fact(DisplayName = "a caller under Set-StrictMode with no seam set gets the default path, no unset-variable error")]
    public async Task A_caller_under_StrictMode_gets_the_default_path()
    {
        await using Cleanup t = new();
        FlagLib l = NewFlagLib(t, "Set-StrictMode -Version Latest; Get-PowerModeDefaultFlagPath; [Environment]::GetFolderPath('CommonApplicationData'); Get-PowerModeMeSid; [void](Get-PowerModeExtraReaderSids)", false);
        Res r = await l.Run();
        Eq(0, r.Status, r.Err);
        string[] lines = Lines(r.Out);
        Match("^S-1-5-21-", lines[2]);
        Eq(Path.Combine(lines[1], "BluettiWatcher", "power-mode.json"), lines[0]);
    }

    [Fact(DisplayName = "POWER_MODE_EXTRA_READERS: account names, comma-separated; one that does not resolve is left out")]
    public async Task POWER_MODE_EXTRA_READERS_account_names()
    {
        await using Cleanup t = new();
        string me = Environment.GetEnvironmentVariable("USERNAME") ?? "";
        FlagLib l = NewFlagLib(t, "(Get-PowerModeExtraReaderSids) -join ','; [Security.Principal.WindowsIdentity]::GetCurrent().User.Value", true,
            Env(("POWER_MODE_EXTRA_READERS", $" no-such-account-bw-test , {me} ,")));
        string[] lines = Lines((await l.Run()).Out);
        Eq(lines[1], lines[0]);
        Res none = await NewFlagLib(t, "@(Get-PowerModeExtraReaderSids).Count").Run();
        Eq("0", none.Out);
    }

    [Fact(DisplayName = "the configured folder is created with its own ACL (the user Modify, SYSTEM and Administrators Full, nothing inherited)")]
    public async Task The_configured_folder_is_created_with_its_own_ACL()
    {
        await using Cleanup t = new();
        const string body = "Initialize-PowerModeFlagDir (Get-PowerModeFlagPath); $acl = Get-Acl -LiteralPath (Split-Path (Get-PowerModeFlagPath)); \"protected=$($acl.AreAccessRulesProtected)\"; " +
            "$me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value; " +
            "$acl.Access | ForEach-Object { $s = $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value; if ($s -ceq $me) { $s = 'me' }; \"$s|$($_.FileSystemRights)|$($_.InheritanceFlags)|$($_.IsInherited)\" } | Sort-Object";
        FlagLib l = NewFlagLib(t, body);
        Eq(new Res(0, string.Join('\n',
            "protected=True",
            "me|Modify, Synchronize|ContainerInherit, ObjectInherit|False",
            "S-1-5-18|FullControl|ContainerInherit, ObjectInherit|False",
            "S-1-5-32-544|FullControl|ContainerInherit, ObjectInherit|False"), ""), await l.Run());
    }

    [Fact(DisplayName = "a missing folder that is not the configured one is never created")]
    public async Task A_missing_folder_that_is_not_the_configured_one_is_never_created()
    {
        await using Cleanup t = new();
        FlagLib l = NewFlagLib(t, "Initialize-PowerModeFlagDir (Join-Path (Split-Path (Split-Path (Get-PowerModeFlagPath))) 'other\\power-mode.json')");
        Res r = await l.Run();
        Assert.NotEqual(0, r.Status);
        Match("directory missing", r.Err + r.Out);
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(l.Fresh))!, "other")));
    }
}

public sealed class PowerModeFlagLibBTests
{
    [Fact(DisplayName = "the folder Initialize created passes its own check")]
    public async Task The_folder_Initialize_created_passes_its_own_check()
    {
        await using Cleanup t = new();
        FlagLib l = NewFlagLib(t, "Initialize-PowerModeFlagDir (Get-PowerModeFlagPath); Initialize-PowerModeFlagDir (Get-PowerModeFlagPath); 'written'");
        Eq(new Res(0, "written", ""), await l.Run());
    }

    [Fact(DisplayName = "a folder that inherits its ACL refuses the write and keeps its ACL")]
    public async Task A_folder_that_inherits_its_ACL_refuses_the_write()
    {
        await using Cleanup t = new();
        Res r = await AclCaseAsync(t, "$null");
        Eq(0, r.Status, r.Err);
        Match(@"^refused: the flag folder .* is not the user's: it inherits its ACL \(inheritance must be off\)", r.Out);
    }

    [Fact(DisplayName = "a folder where Users hold read refuses the write")]
    public async Task A_folder_where_Users_hold_read_refuses_the_write()
    {
        await using Cleanup t = new();
        Res r = await AclCaseAsync(t, "Set-TestAcl $d 'Modify' @('S-1-5-32-545=ReadAndExecute')");
        Match("^refused: .*S-1-5-32-545 has rights there", r.Out);
    }

    [Fact(DisplayName = "an extra reader may read, never write")]
    public async Task An_extra_reader_may_read_never_write()
    {
        await using Cleanup t = new();
        Res read = await AclCaseAsync(t, "$script:PowerModeExtraReaderSidsTest = @('S-1-5-32-545'); Set-TestAcl $d 'Modify' @('S-1-5-32-545=ReadAndExecute')");
        Eq("written", read.Out);
        Res write = await AclCaseAsync(t, "$script:PowerModeExtraReaderSidsTest = @('S-1-5-32-545'); Set-TestAcl $d 'Modify' @('S-1-5-32-545=Modify')");
        Match("^refused: .*the extra reader S-1-5-32-545 may write there", write.Out);
    }
}

public sealed class PowerModeFlagLibCTests
{
    [Fact(DisplayName = "a folder where the user holds only read refuses the write")]
    public async Task A_folder_where_the_user_holds_only_read_refuses_the_write()
    {
        await using Cleanup t = new();
        Res r = await AclCaseAsync(t, "Set-TestAcl $d 'ReadAndExecute' @()");
        Match("^refused: .*holds no Modify there", r.Out);
    }

    [Fact(DisplayName = "a configured flag that cannot be told present (access denied) is untrusted")]
    public async Task A_configured_flag_that_cannot_be_told_present_is_untrusted()
    {
        await using Cleanup t = new();
        // A deny entry for the running user on the folder (deny wins even elevated); removed before the temp world goes.
        FlagLib l = NewFlagLib(t, "$f = Read-PowerModeFlag (Get-PowerModeFlagPath); if ($null -eq $f) { 'untrusted' } else { $f.mode }");
        string dir = Path.GetDirectoryName(l.Fresh)!;
        Eq(0, (await l.Run("Initialize-PowerModeFlagDir (Get-PowerModeFlagPath)")).Status);
        WriteText(l.Fresh, FlagText("normal"));
        Eq(new Res(0, "normal", ""), await l.Run()); // the positive control: readable before the deny
        string me = (await SpawnAsync("pwsh", ["-NoProfile", "-Command", "[Security.Principal.WindowsIdentity]::GetCurrent().User.Value"])).Out.Trim();
        Eq(0, Icacls(dir, "/deny", $"*{me}:(OI)(CI)(RD,RA,REA,X)"));
        Res r;
        try { r = await l.Run(); }
        finally { Icacls(dir, "/remove:d", $"*{me}"); } // before the world's own cleanup, which the deny would stop
        Eq(new Res(0, "untrusted", ""), r);
    }

    [Fact(DisplayName = "a folder owned by someone else refuses the write, however good its DACL")]
    public async Task A_folder_owned_by_someone_else_refuses_the_write()
    {
        await using Cleanup t = new();
        // The folder's owner set to the real user (allowed without privilege), then the seam makes the running user someone else.
        Res r = await AclCaseAsync(t, "Set-TestAcl $d 'Modify' @(); $o = Get-Acl -LiteralPath $d; $o.SetOwner([Security.Principal.WindowsIdentity]::GetCurrent().User); Set-Acl -LiteralPath $d -AclObject $o; $script:PowerModeMeSidTest = 'S-1-5-21-1-2-3-1001'");
        Match("^refused: .* is owned by S-1-5-21-", r.Out);
    }

    [Fact(DisplayName = "a junction in place of the folder refuses the write")]
    public async Task A_junction_in_place_of_the_folder_refuses_the_write()
    {
        await using Cleanup t = new();
        Res r = await AclCaseAsync(t, "$tgt = Join-Path (Split-Path $d) 'target'; New-Item -ItemType Directory $tgt | Out-Null; Set-TestAcl $tgt 'Modify' @(); New-Item -ItemType Junction -Path $d -Target $tgt | Out-Null");
        Match("^refused: .* is a reparse point", r.Out);
    }
}

public sealed class PowerModeEndArgsTests
{
    [Fact(DisplayName = "a flag file carrying an extra entry refuses the write, and readers treat it as untrusted")]
    public async Task A_flag_file_carrying_an_extra_entry_refuses_the_write()
    {
        await using Cleanup t = new();
        const string extra = "$fa = Get-Acl -LiteralPath (Get-PowerModeFlagPath); $fa.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-545'), 'Read', 'Allow'))); Set-Acl -LiteralPath (Get-PowerModeFlagPath) -AclObject $fa";
        FlagLib l = NewFlagLib(t, "try { Initialize-PowerModeFlagDir (Get-PowerModeFlagPath); 'written' } catch { \"refused: $($_.Exception.Message)\" }; $f = Read-PowerModeFlag (Get-PowerModeFlagPath); if ($null -eq $f) { 'untrusted' } else { $f.mode }");
        Eq(0, (await l.Run("Initialize-PowerModeFlagDir (Get-PowerModeFlagPath)")).Status);
        WriteText(l.Fresh, FlagText("normal"));
        Eq(0, (await l.Run(extra)).Status);
        string[] lines = Lines((await l.Run()).Out);
        Match("^refused: .*S-1-5-32-545 has rights there", lines[0]);
        EqL(["untrusted"], lines.Skip(1));
    }

    [Fact(DisplayName = "a reader refuses a configured folder that is not the user's (inheritance on): untrusted")]
    public async Task A_reader_refuses_a_configured_folder_that_is_not_the_users()
    {
        await using Cleanup t = new();
        FlagLib l = NewFlagLib(t, "$f = Read-PowerModeFlag (Get-PowerModeFlagPath); if ($null -eq $f) { 'untrusted' } else { $f.mode }");
        Directory.CreateDirectory(Path.GetDirectoryName(l.Fresh)!); // a plain mkdir: inherits the temp folder's ACL
        WriteText(l.Fresh, FlagText("normal"));
        Eq(new Res(0, "untrusted", ""), await l.Run());
    }

    [Fact(DisplayName = "the end script refuses a name on its command line that is not plain: exit 2, nothing changed")]
    public async Task The_end_script_refuses_a_name_that_is_not_plain()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t);
        long untilMs = Now() - 60000;
        WriteText(m.Flag, EcoFlag(untilMs, Balanced));
        WriteText(m.TaskFile, "registered");
        string[][] bads = [["-Mutex", "MyPowerModeFlag"], ["-Mutex", "Local\\a b"], ["-Mutex", "Local\\a\\b"], ["-TaskPrefix", "a\\b"], ["-ExtraReaders", "ok,dom\\user"], ["-ExtraReaders", "a\"b"]];
        foreach (string[] bad in bads)
        {
            Res r = await End51Async(m, [.. bad, "-TaskUntil", Iso(untilMs)]);
            Eq(2, r.Status, $"{string.Join(',', bad)}: {r.Out}{r.Err}");
            Match(@"^power mode: COULD NOT RUN: POWER_MODE_(MUTEX|TASK_PREFIX|EXTRA_READERS) '", r.Out, RegexOptions.None, string.Join(' ', bad));
        }
        Eq(EcoFlag(untilMs, Balanced), ReadText(m.Flag));
        Eq("registered", ReadText(m.TaskFile));
        Assert.Empty(m.Calls());
    }

    [Fact(DisplayName = "a task prefix or a reader that starts with \"-\" is refused: under powershell.exe -File it would bind as a parameter name")]
    public async Task A_task_prefix_or_a_reader_that_starts_with_a_dash_is_refused()
    {
        await using Cleanup t = new();
        World w = NewWorld(t);
        Res pfx = await CliAsync(["on", "-Path", w.Flag], null, Env(("POWER_MODE_TASK_PREFIX", "-Mutex")));
        Eq(2, pfx.Status, pfx.Out);
        Match("^power mode: COULD NOT RUN: POWER_MODE_TASK_PREFIX ", pfx.Out);
        foreach (string readers in new[] { "-TaskUntil", "ok,-x" })
        {
            Res r = await CliAsync(["on", "-Path", w.Flag], null, Env(("POWER_MODE_EXTRA_READERS", readers)));
            Eq(2, r.Status, r.Out);
            Match("^power mode: COULD NOT RUN: POWER_MODE_EXTRA_READERS ", r.Out);
        }
        Assert.Empty(Ls(w.Dir));
    }

    [Fact(DisplayName = "a flag whose local time cannot be formatted: the fallback names the flag's own mode (eco, not EMERGENCY)")]
    public async Task A_flag_whose_local_time_cannot_be_formatted_names_its_own_mode()
    {
        await using Cleanup t = new();
        World w = NewWorld(t);
        Dictionary<string, string> tz = Env(("POWER_MODE_TZ", "No/Such_Zone"));
        string untilIso = IsoMs(Now() + 3600000);
        string sinceIso = IsoMs(Now() - 60000);
        WriteText(w.Flag, EcoJson(sinceIso, untilIso));
        Res st = await CliAsync(["status", "-Path", w.Flag], null, tz);
        string[] lines = Lines(st.Out);
        Eq($"power mode: ECO since {sinceIso} until {untilIso}", lines[0]);
        Match(@"^warn: local time unavailable \(", lines[1]);
        WriteText(w.Flag, NormalJson(sinceIso));
        Res nrm = await CliAsync(["status", "-Path", w.Flag], null, tz);
        Eq("power mode: normal", Lines(nrm.Out)[0]);
    }
}
