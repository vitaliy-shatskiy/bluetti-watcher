using System.Globalization;
using System.Text.RegularExpressions;

namespace BluettiWatcher.PowerMode;

/// <summary>The command line of power-mode.ps1 (and power-mode-end.ps1's four names), bound the way PowerShell bound it.</summary>
internal sealed class PmArgs
{
    public string Action { get; set; } = "";
    public string Value { get; set; } = "";
    public string Path { get; set; } = "";
    public string Until { get; set; } = "";
    public string For { get; set; } = "";
    public string TaskUntil { get; set; } = "";
    public string Source { get; set; } = "manual";
    public string Soc { get; set; } = "";
    public string Level { get; set; } = "";
    public string Mains { get; set; } = "";
    public string HibernateAt { get; set; } = "";
    public bool Adopt { get; set; }
    public string ReturnPlan { get; set; } = "";
    public string RestoreWatts { get; set; } = "";
    public string? FlagFile { get; set; }
    public string? Mutex { get; set; }
    public string? TaskPrefix { get; set; }
    public string? ExtraReaders { get; set; }

    // power-mode.ps1 1.0.0's parameters: any prefix of one that no other shares binds to it, exactly as in 1.0.0.
    private static readonly string[] Names =
        ["Path", "Until", "For", "TaskUntil", "Source", "Soc", "Level", "Mains", "HibernateAt", "Adopt", "ReturnPlan", "Value", "RestoreWatts", "Action"];

    // The names power-mode-end.ps1 1.0.0 added (the end task's command line). A prefix that a name above starts with is that
    // name's, so -F, -M and -T stay -For, -Mains and -TaskUntil; these bind exactly, or by a prefix no name above starts with.
    private static readonly string[] EndNames = ["FlagFile", "Mutex", "TaskPrefix", "ExtraReaders"];

    private static bool LooksLikeName(string a) => a.Length > 1 && a[0] == '-' && (char.IsLetter(a[1]) || a[1] == '_');

    /// <summary>Binds <paramref name="args"/>: names without case, a unique prefix, <c>-Name:value</c>, Action and Value by position. Throws on a bad line.</summary>
    public static PmArgs Parse(IReadOnlyList<string> args)
    {
        var a = new PmArgs();
        int position = 0;
        for (int i = 0; i < args.Count; i++)
        {
            string token = args[i];
            if (!LooksLikeName(token))
            {
                if (position == 0) a.Action = token;
                else if (position == 1) a.Value = token;
                else throw new ArgumentException($"A positional parameter cannot be found that accepts argument '{token}'.");
                position++;
                continue;
            }
            string name = token[1..];
            string? inline = null;
            int colon = name.IndexOf(':', StringComparison.Ordinal);
            if (colon >= 0) { inline = name[(colon + 1)..]; name = name[..colon]; }
            string param = Resolve(name);
            if (param == "Adopt")
            {
                // A switch: `-Adopt` = on; `-Adopt:$false` arrives as one argument from -File, as `-Adopt:` then `False` from a host call.
                if (inline is null) a.Adopt = true;
                else if (inline.Length > 0) a.Adopt = SwitchValue(inline) ?? throw BadSwitch(param, inline);
                else if (i + 1 < args.Count && SwitchValue(args[i + 1]) is { } next) { a.Adopt = next; i++; }
                else throw new ArgumentException($"Missing an argument for parameter '{param}'. Specify a parameter of type 'System.Management.Automation.SwitchParameter' and try again.");
                continue;
            }
            string value;
            // `-Name:` then the value as the next argument is how PowerShell hands `-Name:value` on in $args.
            if (inline is { Length: > 0 } || (inline is not null && i + 1 >= args.Count)) value = inline;
            else if (i + 1 < args.Count && !LooksLikeName(args[i + 1])) value = args[++i];
            else throw new ArgumentException($"Missing an argument for parameter '{param}'. Specify a parameter of type 'System.String' and try again.");
            a.Set(param, value);
            if (param == "Action") position = Math.Max(position, 1);
            if (param == "Value") position = Math.Max(position, 2);
        }
        return a;
    }

    private static bool? SwitchValue(string text) => text.ToLowerInvariant() switch
    {
        "$true" or "true" or "1" => true,
        "$false" or "false" or "0" => false,
        _ => null,
    };

    private static ArgumentException BadSwitch(string param, string value) =>
        new($"Cannot process argument transformation on parameter '{param}'. Cannot convert value \"{value}\" to type \"System.Management.Automation.SwitchParameter\". Boolean parameters accept only Boolean values and numbers, such as $True, $False, 1 or 0.");

    private static string Resolve(string name)
    {
        var exact = Names.Concat(EndNames).FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;
        // 1.0.0's names first, then the added ones: a new name never takes an abbreviation an old one had.
        foreach (var set in new[] { Names, EndNames })
        {
            var prefixed = set.Where(n => n.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (prefixed.Count == 1) return prefixed[0];
            if (prefixed.Count > 1) throw new ArgumentException($"Parameter cannot be processed because the parameter name '{name}' is ambiguous. Possible matches include: {string.Join(' ', prefixed.Select(p => "-" + p))}.");
        }
        throw new ArgumentException($"A parameter cannot be found that matches parameter name '{name}'.");
    }

    private void Set(string param, string value)
    {
        switch (param)
        {
            case "Path": Path = value; break;
            case "Until": Until = value; break;
            case "For": For = value; break;
            case "TaskUntil": TaskUntil = value; break;
            case "Source": Source = value; break;
            case "Soc": Soc = value; break;
            case "Level": Level = value; break;
            case "Mains": Mains = value; break;
            case "HibernateAt": HibernateAt = value; break;
            case "ReturnPlan": ReturnPlan = value; break;
            case "Value": Value = value; break;
            case "RestoreWatts": RestoreWatts = value; break;
            case "Action": Action = value; break;
            case "FlagFile": FlagFile = value; break;
            case "Mutex": Mutex = value; break;
            case "TaskPrefix": TaskPrefix = value; break;
            default: ExtraReaders = value; break;
        }
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed partial class PowerModeSwitch
{
    internal const string Usage = "usage: power-mode.ps1 on|off|status|eco|battery|prev-plan <balanced|high|plan GUID>|gpu-task install [-RestoreWatts <W>]|remove|status [-Until HH:mm | -For <minutes>] [-Source manual|station] [-Path <flag file>]";
    private const int DefaultEcoMinutes = 240;
    private static readonly string[] Actions = ["on", "off", "status", "eco", "end", "battery", "prev-plan", "gpu-task"];
    private static readonly string[] Levels = ["ok", "low", "critical", "hibernating"];

    [GeneratedRegex(@"^(\d{1,2}):(\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex ClockPattern();

    [GeneratedRegex(@"^\d{1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex MinutesPattern();

    private static bool Fails(Exception e) => e is not PowerModeExit;

    /// <summary><c>BluettiWatcher.exe power-mode &lt;args&gt;</c>: the exit code (0 · 1 · 2, as power-mode.ps1).</summary>
    public int Run(IReadOnlyList<string> args)
    {
        try
        {
            StartStandInHost();
            return RunCore(args);
        }
        catch (PowerModeExit e) { return e.Code; }
        // Whatever else goes wrong ends the command as the scripts' own catch did: the line, exit 2; nothing escapes to the
        // process (a crash code) or, in the service, to the poller.
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Say($"power mode: COULD NOT RUN: {e.Message}");
            return 2;
        }
        finally { StopStandInHost(); }
    }

    private int RunCore(IReadOnlyList<string> args)
    {
        PmArgs a;
        try { a = PmArgs.Parse(args); }
        catch (ArgumentException e)
        {
            Say($"power mode: COULD NOT RUN: {e.Message}");
            return 2;
        }
        bool names = a.FlagFile is not null || a.Mutex is not null || a.TaskPrefix is not null || a.ExtraReaders is not null;
        if (names && a.Action != "end")
        {
            Say("power mode: COULD NOT RUN: -FlagFile, -Mutex, -TaskPrefix and -ExtraReaders go with end");
            return 2;
        }
        if (a.FlagFile is { Length: > 0 })
        {
            // As the end task: the four names come from the arguments alone; an absent one is empty (the default), never a
            // value the user's saved environment carries, which would widen the trusted readers.
            SetEnv("POWER_MODE_FILE", a.FlagFile);
            SetEnv("POWER_MODE_MUTEX", a.Mutex ?? "");
            SetEnv("POWER_MODE_TASK_PREFIX", a.TaskPrefix ?? "");
            SetEnv("POWER_MODE_EXTRA_READERS", a.ExtraReaders ?? "");
        }
        else
        {
            if (a.Mutex is { Length: > 0 }) SetEnv("POWER_MODE_MUTEX", a.Mutex);
            if (a.TaskPrefix is { Length: > 0 }) SetEnv("POWER_MODE_TASK_PREFIX", a.TaskPrefix);
            if (a.ExtraReaders is { Length: > 0 }) SetEnv("POWER_MODE_EXTRA_READERS", a.ExtraReaders);
        }
        string path;
        try
        {
            LoadNames();
            path = FlagPathOf(a.Path);
        }
        catch (Exception e) when (Fails(e))
        {
            Say($"power mode: COULD NOT RUN: {e.Message}");
            return 2;
        }
        // Test seam (POWER_MODE_TEST=1): a failure no command line expects.
        if (Env("POWER_MODE_TEST") == "1" && Env("POWER_MODE_TEST_FAULT") == "unexpected") throw new NotSupportedException("test fault");
        var cfg = ReadConfig(path);
        if (!Actions.Contains(a.Action)) { Say(Usage); return 2; }
        if (a.Source is not ("manual" or "station")) { Say($"power mode: COULD NOT RUN: -Source '{a.Source}' is not manual or station"); return 2; }
        if (a.Action == "status") return Status(path, cfg);
        if (a.Action == "gpu-task") return GpuTask(a);
        if (a.Action == "end") return End(path, a.TaskUntil);

        // Every write from here on holds the flag's lock: read, decide and write happen under it.
        using var flagLock = TryEnterLock(out string lockError);
        if (flagLock is null)
        {
            Say($"power mode: COULD NOT RUN: {lockError}");
            return 2;
        }
        if (a.Action == "battery") return Battery(path, a, cfg);
        if (a.Action != "eco" && (a.Adopt || a.ReturnPlan.Length > 0)) { Say("power mode: COULD NOT RUN: -Adopt / -ReturnPlan go with eco"); return 2; }
        if (a.Action == "eco" && (a.Adopt || a.ReturnPlan.Length > 0)) return Adopt(path, a);
        if (a.Action == "prev-plan") return PrevPlan(path, a, cfg);
        if (a.Action == "eco") return Eco(path, a, cfg);
        return OnOff(path, a, cfg);
    }

    private int Status(string path, PmConfig cfg)
    {
        var flag = ReadFlag(path);
        if (flag is null) { Say($"power mode: COULD NOT READ {path} (not a valid flag file)"); return 2; }
        SayAll(StatusLines(flag));
        SayAll(cfg.Warn);
        if (flag.PrevPlan.Length > 0 || flag.Raw == "eco")
        {
            // The plan `off` will set, by name, and the plan that was active before eco when it is another.
            var plans = Plans();
            string owed = OwedPlan(flag, plans);
            if (owed.Length == 0) Say("plan: nothing to return to");
            else
            {
                var to = ResolveReturnPlan(owed, cfg, plans);
                string before = flag.PrevPlan.Length > 0 && flag.PrevPlan != to.Target ? $"; before eco: {PlanLabel(flag.PrevPlan, plans)}" : "";
                Say($"plan: returns to {PlanLabel(to.Target, plans)}{before}");
            }
            Say($"end task: {EndTask} {(EndTaskPresent() ? "present" : "absent")}");
        }
        bool owesGpu = Truthy(flag.PrevGpuLimitW);
        if (owesGpu) Say($"gpu cap: {(Truthy(flag.GpuCapW) ? $"{Watts(flag.GpuCapW!.Value)} W" : "on")} (returns to {Watts(flag.PrevGpuLimitW!.Value)} W)");
        // The limit in force now, read from the card: a driver or display power transition can put the default back.
        if (owesGpu && GpuSeam)
        {
            var live = GpuLimitNow();
            Say(live is not { } l ? "gpu live: no answer from nvidia-smi"
                : Truthy(flag.GpuCapW) && Math.Abs(l - flag.GpuCapW!.Value) >= 0.5 ? $"gpu live: {Watts(l)} W - MISMATCH, not the cap {Watts(flag.GpuCapW.Value)} W (something else set it)"
                : $"gpu live: {Watts(l)} W");
        }
        // The stuck state: the card at its minimum and no limit recorded, so no off will ever lift it. Named, never guessed.
        if (!owesGpu && flag.Mode is "eco" or "normal" && GpuSeam)
        {
            var q = GpuLimits();
            if (q.Error.Length == 0 && q.Limit <= q.Min + 0.5 && q.Default > q.Min + 0.5)
                Say($"gpu: at minimum {Watts(q.Min)} W with nothing recorded to restore - run `nvidia-smi -pl {Watts(q.Default)}` elevated");
        }
        // The tasks a caller that is not elevated needs to set the limit, while eco owes or may set one.
        if (cfg.GpuCap && GpuSeam && (flag.Raw == "eco" || owesGpu))
        {
            var c = GpuTaskWatts(GpuCapTask);
            var re = GpuTaskWatts(GpuRestoreTask);
            Say(c.State == "ok" && re.State == "ok"
                ? $"gpu helper: {GpuCapTask} {Num(c.Watts ?? 0)} W, {GpuRestoreTask} {Num(re.Watts ?? 0)} W"
                : "gpu helper: not registered (an elevated shell: power-mode.ps1 gpu-task install)");
        }
        if (flag.Battery is { } b)
        {
            string when;
            try { when = FormatTime(b.AtAt!.Value); }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException) { Skip(e); when = b.At; }
            string grid = b.Mains is { } on ? (on ? ", mains on" : ", mains off") : "";
            Say($"battery: {Watts(b.Soc)} % ({b.Level}{grid}) at {when}");
        }
        return flag.Mode != "normal" ? 1 : 0;
    }

    private int GpuTask(PmArgs a)
    {
        string[] tasks = [GpuCapTask, GpuRestoreTask];
        if (a.Value is not ("install" or "remove" or "status")) { Say("usage: power-mode.ps1 gpu-task install [-RestoreWatts <W>]|remove|status"); return 2; }
        if (a.Value == "status")
        {
            bool all = true;
            foreach (var name in tasks)
            {
                var t = GpuTaskWatts(name);
                if (t.State == "ok") { Say($"gpu helper: {name} sets {Num(t.Watts ?? 0)} W"); continue; }
                all = false;
                Say(t.State == "missing" ? $"gpu helper: {name} not registered (an elevated shell: power-mode.ps1 gpu-task install)"
                    : $"gpu helper: {name} is registered but its action is not System32 nvidia-smi -pl <watts>: run gpu-task install again");
            }
            if (GpuSeam)
            {
                var live = GpuLimitNow();
                Say($"gpu live: {(live is { } l ? $"{Watts(l)} W" : "no answer from nvidia-smi")}");
            }
            return all ? 0 : 1;
        }
        if (Env("POWER_MODE_ELEVATED") == "0" || !RealElevation()) { Say($"power mode: COULD NOT RUN: gpu-task {a.Value} needs an elevated shell"); return 2; }
        string[] keep = ["POWER_MODE_FILE", "POWER_MODE_MUTEX", "POWER_MODE_TASK_PREFIX", "POWER_MODE_EXTRA_READERS"];
        var seams = _env.Keys.Where(k => k.StartsWith("POWER_MODE_", StringComparison.OrdinalIgnoreCase) && !keep.Contains(k, StringComparer.Ordinal))
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (seams.Count > 0) { Say($"power mode: COULD NOT RUN: gpu-task {a.Value} takes no test seam, unset {string.Join(", ", seams)}"); return 2; }
        if (a.Value == "remove")
        {
            foreach (var name in tasks)
            {
                var r = RealTool("schtasks.exe", ["/delete", "/tn", name, "/f"]);
                Say($"gpu helper: {name} {(r.Code == 0 ? "removed" : $"not removed ({r.LastLine})")}");
            }
            return 0;
        }
        var q = RealTool("nvidia-smi.exe", ["--query-gpu=power.min_limit,power.max_limit,power.default_limit", "--format=csv,noheader,nounits"]);
        var nums = new List<double>();
        foreach (var cell in (q.Out.Count > 0 ? q.Out[0] : "").Split(',').Select(c => c.Trim()))
        {
            if (double.TryParse(cell, NumberStyles.Float, Inv, out var n)) nums.Add(n);
        }
        if (q.Code != 0 || nums.Count != 3) { Say($"power mode: COULD NOT RUN: nvidia-smi did not report the power limit range ({q.LastLine})"); return 2; }
        var plan = ResolveGpuTaskWatts(nums[0], nums[1], nums[2], a.RestoreWatts);
        if (plan.Error.Length > 0) { Say($"power mode: COULD NOT RUN: gpu-task install: {plan.Error}"); return 2; }
        bool failed = false;
        foreach (var (name, watts) in new[] { (GpuCapTask, plan.Cap), (GpuRestoreTask, plan.Restore) })
        {
            // A task of that name registered before (by anyone) goes first: its security and principal are never inherited.
            _ = RealTool("schtasks.exe", ["/delete", "/tn", name, "/f"]);
            var r = RegisterGpuTask(name, GpuTaskXml(MeSid(), name, watts));
            // What was registered is read back: a task whose action is not exactly the one written is deleted again.
            var back = RealTool("schtasks.exe", ["/query", "/tn", name, "/xml"]);
            var t = back.Code == 0 ? ParseGpuTaskXml(string.Join('\n', back.Out)) : new PmGpuTask("missing", null);
            if (r.Code == 0 && t.State == "ok" && t.Watts == watts) { Say($"gpu helper: {name} registered: nvidia-smi -pl {Num(watts)} (highest run level, on demand)"); continue; }
            failed = true;
            if (t.State == "foreign") _ = RealTool("schtasks.exe", ["/delete", "/tn", name, "/f"]);
            Say($"power mode: gpu-task install: the task {name} was not registered as written ({r.LastLine})");
        }
        return failed ? 1 : 0;
    }

    private int Battery(string path, PmArgs a, PmConfig cfg)
    {
        PmFlag flag;
        double soc;
        try
        {
            if (!double.TryParse(a.Soc, NumberStyles.Float, Inv, out soc) || soc < 0 || soc > 100) { Say($"power mode: COULD NOT RUN: -Soc '{a.Soc}' is not 0-100"); return 2; }
            if (!Levels.Contains(a.Level)) { Say($"power mode: COULD NOT RUN: -Level '{a.Level}' is not ok, low, critical or hibernating"); return 2; }
            if (a.Mains is not ("on" or "off")) { Say($"power mode: COULD NOT RUN: -Mains '{a.Mains}' is not on or off"); return 2; }
            double? hib = null;
            if (a.HibernateAt.Length > 0)
            {
                if (!double.TryParse(a.HibernateAt, NumberStyles.Float, Inv, out var h) || h < 0 || h > 100) { Say($"power mode: COULD NOT RUN: -HibernateAt '{a.HibernateAt}' is not 0-100"); return 2; }
                hib = h;
            }
            var read = ReadFlag(path);
            if (read is null) { Say($"power mode: COULD NOT RUN: {path} is not a valid flag file (run on or off to replace it)"); return 2; }
            flag = read;
            // Test seam: hold between the read and the write, so a test can prove another writer waits for the lock.
            if (ShortNumberPattern().IsMatch(Env("POWER_MODE_TEST_HOLD_MS")))
            {
                File.WriteAllText(path + ".held", "");
                Thread.Sleep(int.Parse(Env("POWER_MODE_TEST_HOLD_MS"), Inv));
            }
            var battery = new PmBattery { Soc = soc, At = Iso(DateTimeOffset.Now), Level = a.Level, Mains = a.Mains == "on", HibernateAt = hib };
            // Everything else exactly as written: the raw mode and since, so an eco past its end stays that flag.
            _ = WriteFlag(path, new PmWrite
            {
                Mode = flag.Raw, Since = flag.RawSince, Until = flag.Until, Source = flag.Source, By = flag.By, PrevPlan = flag.PrevPlan,
                Replaced = flag.Replaced, Battery = battery, PrevGpuLimitW = flag.PrevGpuLimitW, GpuCapW = flag.GpuCapW,
            });
        }
        catch (Exception e) when (Fails(e)) { Say($"power mode: COULD NOT RUN: {e.Message}"); return 2; }
        Say($"battery: {Watts(soc)} % ({a.Level}, mains {a.Mains})");
        // The drift check rides on the station's reading: eco, or an emergency over it; never a normal flag.
        if (flag.Mode is "eco" or "emergency")
        {
            var recap = new List<string>();
            GpuRecap(flag, cfg, recap);
            SayAll(recap);
        }
        return 0;
    }

    private int Adopt(string path, PmArgs a)
    {
        var notes = new List<string>();
        PmFlag written;
        string plan;
        try
        {
            if (!a.Adopt || a.ReturnPlan.Length == 0) { Say("power mode: COULD NOT RUN: pass -Adopt with -ReturnPlan balanced|high|<plan GUID>"); return 2; }
            if (a.Until.Length > 0 || a.For.Length > 0) { Say("power mode: COULD NOT RUN: -Adopt keeps the end time (-Until / -For do not go with it)"); return 2; }
            plan = a.ReturnPlan == "balanced" ? BalancedPlan : a.ReturnPlan == "high" ? HighPlan : a.ReturnPlan.ToLowerInvariant();
            if (!GuidPattern().IsMatch(plan)) { Say($"power mode: COULD NOT RUN: -ReturnPlan '{a.ReturnPlan}' is not balanced, high or a plan GUID"); return 2; }
            if (plan == SaverPlan) { Say("power mode: COULD NOT RUN: Power saver is never the plan to return to"); return 2; }
            var plans = Plans();
            if (plans is null || !plans.All.Contains(plan)) { Say($"power mode: COULD NOT RUN: the power plan {plan} is not on this machine"); return 2; }
            var flag = ReadFlag(path);
            if (flag is null || flag.Mode != "eco") { Say("power mode: COULD NOT RUN: -Adopt needs an eco that is on"); return 2; }
            written = WriteFlag(path, new PmWrite
            {
                Mode = "eco", Since = flag.RawSince, Until = flag.Until, UntilAt = flag.UntilAt, Source = flag.Source, PrevPlan = plan,
                Battery = flag.Battery, PrevGpuLimitW = flag.PrevGpuLimitW, GpuCapW = flag.GpuCapW,
            });
        }
        catch (Exception e) when (Fails(e)) { Say($"power mode: COULD NOT RUN: {e.Message}"); return 2; }
        notes.Add($"plan: returns to {plan}");
        if (written.UntilAt is { } untilAt)
        {
            var r = RegisterEndTask(path, untilAt, written.Until);
            notes.Add(r.Code == 0 ? $"end task: {EndTask} registered"
                : $"warn: the end task {EndTask} could not be registered ({r.LastLine}): the power plan {plan} will not come back at the end time, only on power-mode.ps1 off");
        }
        SayAll(StatusLines(written));
        SayAll(notes);
        return 0;
    }

    private int PrevPlan(string path, PmArgs a, PmConfig cfg)
    {
        PmFlag written;
        PmPlans? plans;
        string newPlan;
        try
        {
            newPlan = ResolvePlanAlias(a.Value);
            if (newPlan.Length == 0 || a.Value == "saver") { Say($"power mode: COULD NOT RUN: prev-plan '{a.Value}' is not balanced, high or a plan GUID"); return 2; }
            if (newPlan == SaverPlan) { Say("power mode: COULD NOT RUN: Power saver is never the plan to return to"); return 2; }
            var flag = ReadFlag(path);
            if (flag is null) { Say($"power mode: COULD NOT RUN: {path} is not a valid flag file (run on or off to replace it)"); return 2; }
            if (flag.Mode != "eco") { Say("power mode: prev-plan: no eco is on, nothing changed"); return 1; }
            plans = Plans();
            if (plans is null || !plans.All.Contains(newPlan)) { Say($"power mode: COULD NOT RUN: the power plan {newPlan} is not on this machine"); return 2; }
            written = WriteFlag(path, new PmWrite
            {
                Mode = "eco", Since = flag.RawSince, Until = flag.Until, UntilAt = flag.UntilAt, Source = flag.Source, By = flag.By,
                Replaced = flag.Replaced, Battery = flag.Battery, PrevGpuLimitW = flag.PrevGpuLimitW, GpuCapW = flag.GpuCapW, PrevPlan = newPlan,
            });
        }
        catch (Exception e) when (Fails(e)) { Say($"power mode: COULD NOT RUN: {e.Message}"); return 2; }
        // The recorded plan decides only with returnPlan previous; otherwise say what off will set.
        var to = ResolveReturnPlan(newPlan, cfg, plans);
        var notes = new List<string>(cfg.Warn)
        {
            to.Target == newPlan ? $"plan: returns to {PlanLabel(newPlan, plans)}"
                : $"plan: remembered {PlanLabel(newPlan, plans)}; off returns to {PlanLabel(to.Target, plans)} (config returnPlan)",
        };
        if (written.UntilAt is { } untilAt && !EndTaskPresent())
        {
            var r = RegisterEndTask(path, untilAt, written.Until);
            notes.Add(r.Code == 0 ? $"end task: {EndTask} registered"
                : $"warn: the end task {EndTask} could not be registered ({r.LastLine}): the power plan will not come back at the end time, only on power-mode.ps1 off");
        }
        SayAll(StatusLines(written));
        SayAll(notes);
        return 0;
    }

    /// <summary>`HH:mm` local wall clock (the PC's zone) -> the next such instant; `24:00` = the coming midnight. null = not a time.</summary>
    private DateTimeOffset? ResolveUntil(string text)
    {
        var m = ClockPattern().Match(text);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, NumberStyles.None, Inv, out int h) || !int.TryParse(m.Groups[2].Value, NumberStyles.None, Inv, out int mi)) return null;
        if (mi > 59 || h > 24 || (h == 24 && mi != 0)) return null;
        var zone = Zone();
        var nowK = TimeZoneInfo.ConvertTime(DateTimeOffset.Now, zone);
        var day = nowK.Date;
        var wall = h == 24 ? day.AddDays(1) : day.AddHours(h).AddMinutes(mi);
        if (wall <= nowK.DateTime) wall = wall.AddDays(1);
        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }

    private int Eco(string path, PmArgs a, PmConfig cfg)
    {
        var notes = new List<string>(cfg.Warn);
        bool station = a.Source == "station";
        bool activate = false, keep = false;
        string prev, ret, keptPlan = "";
        PmFlag written;
        try
        {
            if (station && (a.Until.Length > 0 || a.For.Length > 0)) { Say("power mode: COULD NOT RUN: an eco from the station has no end time (-Until / -For)"); return 2; }
            if (a.Until.Length > 0 && a.For.Length > 0) { Say("power mode: COULD NOT RUN: pass -Until or -For, not both"); return 2; }
            DateTimeOffset? untilAt = null;
            if (a.Until.Length > 0)
            {
                untilAt = ResolveUntil(a.Until);
                if (untilAt is null) { Say($"power mode: COULD NOT RUN: -Until '{a.Until}' is not HH:mm"); return 2; }
            }
            else if (!station)
            {
                int minutes = DefaultEcoMinutes;
                if (a.For.Length > 0)
                {
                    if (!MinutesPattern().IsMatch(a.For) || !int.TryParse(a.For, NumberStyles.None, Inv, out minutes) || minutes < 1 || minutes > 1440)
                    { Say($"power mode: COULD NOT RUN: -For '{a.For}' is not minutes 1-1440"); return 2; }
                }
                untilAt = DateTimeOffset.Now.AddMinutes(minutes);
            }
            var flag = ReadFlag(path);
            if (flag is not null && flag.Mode == "emergency") { Say($"{StatusLine(flag)} (eco not set: emergency is stricter; run off first)"); return 2; }
            // The station never replaces an eco that is on: a manual one keeps its end time, its own keeps its since. It only
            // applies what that eco left unapplied (a plan or a GPU cap whose step failed), so the page's Eco repairs it.
            keep = station && flag is not null && flag.Mode == "eco";
            // A plan already recorded (eco on, or ended and not restored yet) stays the plan to return to.
            prev = keptPlan = flag?.PrevPlan ?? "";
            // A kept eco that owes a plan: Power saver is set again when it is not the active plan (its switch failed or
            // something switched it off); the plan recorded stays the plan to return to.
            bool planOwed = keep && prev.Length > 0;
            var plans = Plans();
            if (plans is null) notes.Add("warn: powercfg did not list the power plans: the power plan is unchanged");
            else if (!plans.All.Contains(SaverPlan)) notes.Add($"warn: the Power saver plan ({SaverPlan}) is not on this machine: the power plan is unchanged");
            else if (plans.Active == SaverPlan)
            {
                // Power saver is never recorded: switched on by hand before eco, the plan off returns to is recorded instead.
                if (prev.Length > 0) { if (!planOwed) notes.Add($"plan: Power saver stays on; returns to {ResolveReturnPlan(prev, cfg, plans).Target}"); }
                else
                {
                    prev = ResolveReturnPlan("", cfg, plans).Target;
                    notes.Add($"plan: Power saver was already on; returns to {prev}");
                }
            }
            else if (prev.Length == 0 && plans.Active.Length == 0) notes.Add("warn: the active power plan could not be read: the power plan is unchanged");
            else
            {
                if (prev.Length == 0) prev = plans.Active;
                activate = true;
            }
            ret = prev.Length > 0 ? ResolveReturnPlan(prev, cfg, plans).Target : "";
            // The GPU power cap: the limit in force goes into the flag before the minimum is set, so a crash in between leaves
            // a debt `off` pays. A limit already recorded (eco is on) stays.
            double? gpuPrev = flag?.PrevGpuLimitW, gpuCapW = flag?.GpuCapW, gpuSet = null;
            if (Truthy(gpuPrev))
            {
                if (!keep) notes.Add($"gpu cap: stays on (returns to {Watts(gpuPrev!.Value)} W)");
                GpuRecap(flag!, cfg, notes);
            }
            else if (cfg.GpuCap && GpuSeam)
            {
                var q = GpuLimits();
                if (q.Error.Length > 0) notes.Add($"note: gpu cap not set ({q.Error})");
                else if (q.Limit <= q.Min) notes.Add($"gpu cap: the power limit is already the minimum ({Watts(q.Min)} W)");
                else { gpuPrev = q.Limit; gpuCapW = q.Min; gpuSet = q.Min; }
            }
            var ecoFlag = keep
                ? new PmWrite
                {
                    Mode = "eco", Since = flag!.RawSince, Until = flag.Until, UntilAt = flag.UntilAt, Source = flag.Source, By = flag.By,
                    Replaced = flag.Replaced, PrevPlan = prev, Battery = flag.Battery, PrevGpuLimitW = gpuPrev, GpuCapW = gpuCapW,
                }
                : new PmWrite { Mode = "eco", UntilAt = untilAt, Source = a.Source, PrevPlan = prev, Battery = flag?.Battery, PrevGpuLimitW = gpuPrev, GpuCapW = gpuCapW };
            // A kept eco with nothing to repair is not rewritten. A debt goes into the flag before its setting changes.
            written = keep && prev == flag!.PrevPlan && gpuSet is null ? flag : WriteFlag(path, ecoFlag);
            if (gpuSet is { } cap)
            {
                var r = SetGpuLimit(cap, "cap");
                ecoFlag.Since = written.Since;
                ecoFlag.Until = written.Until;
                if (r.Reached)
                {
                    double applied = r.Applied ?? cap;
                    notes.Add($"gpu cap: {Watts(applied)} W (returns to {Watts(gpuPrev!.Value)} W)");
                    // The cap task sets its own fixed value: that is the cap in force.
                    if (Math.Abs(applied - cap) >= 0.5) { ecoFlag.GpuCapW = applied; written = WriteFlag(path, ecoFlag); }
                }
                else if (r.TimedOut) notes.Add($"note: gpu cap not confirmed ({r.Why}): the limit to return to stays recorded, off puts it back");
                else
                {
                    // Refused: nothing changed, nothing is owed.
                    notes.Add($"note: gpu cap not set ({r.Why})");
                    ecoFlag.PrevGpuLimitW = null;
                    ecoFlag.GpuCapW = null;
                    written = WriteFlag(path, ecoFlag);
                }
            }
        }
        catch (Exception e) when (Fails(e)) { Say($"power mode: COULD NOT RUN: {e.Message}"); return 2; }
        if (activate)
        {
            var r = PowerCfg("/setactive", SaverPlan);
            notes.Add(r.Code == 0 ? $"plan: Power saver on; returns to {ret}" : $"warn: the Power saver plan could not be set ({r.LastLine}): the power plan is unchanged");
        }
        if (keep)
        {
            // A kept manual eco that now owes a plan gets the end task it could not register without one.
            if (prev.Length > 0 && prev != keptPlan && written.UntilAt is { } keptUntil && !EndTaskPresent())
            {
                var r = RegisterEndTask(path, keptUntil, written.Until);
                notes.Add(r.Code == 0 ? $"end task: {EndTask} registered"
                    : $"warn: the end task {EndTask} could not be registered ({r.LastLine}): the power plan {ret} will not come back at the end time, only on power-mode.ps1 off");
            }
        }
        else if (prev.Length > 0 && station) RemoveEndTask();   // no end time, so no end task; one a manual eco left behind goes
        else if (prev.Length > 0)
        {
            var r = written.UntilAt is { } untilAt ? RegisterEndTask(path, untilAt, written.Until) : new PmToolResult(-1, ["no end time"]);
            notes.Add(r.Code == 0 ? $"end task: {EndTask} registered"
                : $"warn: the end task {EndTask} could not be registered ({r.LastLine}): the power plan {ret} will not come back at the end time, only on power-mode.ps1 off");
        }
        SayAll(StatusLines(written, keep ? " (already on, kept)" : ""));
        SayAll(notes);
        return 0;
    }

    private int OnOff(string path, PmArgs a, PmConfig cfg)
    {
        bool on = a.Action == "on";
        string want = on ? "emergency" : "normal";
        var notes = new List<string>(cfg.Warn);
        PmFlag? flag, written = null;
        try
        {
            flag = ReadFlag(path);
            string prev = flag?.PrevPlan ?? "";
            var battery = flag?.Battery;
            double? gpuPrev = flag?.PrevGpuLimitW, gpuCapW = flag?.GpuCapW;
            // Off pays what eco left owed, an emergency over it included: the plan and the GPU power limit.
            string owed = !on && flag is not null ? OwedPlan(flag, null) : "";
            if (owed.Length > 0 || (!on && Truthy(gpuPrev)))
            {
                // Off lifts every mode, the station's (mains back) and the user's alike. An eco that ended keeps its `since`.
                string left = RestorePlan(owed, notes, cfg);
                double? gpuLeft = RestoreGpuLimit(flag!, cfg, notes);
                RemoveEndTask();
                written = WriteFlag(path, new PmWrite
                {
                    Mode = "normal", By = a.Source, PrevPlan = left, Battery = battery, Since = flag!.Mode == "normal" ? flag.Since : "",
                    PrevGpuLimitW = gpuLeft, GpuCapW = Truthy(gpuLeft) ? gpuCapW : null,
                });
            }
            else if (!(flag is not null && flag.Mode == want))
            {
                // The station's emergency over the user's eco remembers that eco, so the status line still names its end time.
                PmReplaced? keep = on && a.Source == "station" && flag is not null && flag.Mode == "eco" && flag.Source == "manual" && flag.UntilAt is { } ua
                    ? new PmReplaced(flag.RawSince, flag.Until, ua) : null;
                // An eco that owes no plan and recorded no limit: the GPU is left alone (RestoreGpuLimit only says so).
                if (!on && flag is not null) _ = RestoreGpuLimit(flag, cfg, notes);
                written = WriteFlag(path, new PmWrite
                {
                    Mode = want, PrevPlan = prev, Battery = battery, Replaced = keep, Source = on ? a.Source : "", By = on ? "" : a.Source,
                    PrevGpuLimitW = gpuPrev, GpuCapW = gpuCapW,
                });
                if (prev.Length > 0)
                {
                    // Emergency over eco: Power saver stays, the plan to return to stays, the end task goes (no end time).
                    RemoveEndTask();
                    notes.Add($"plan: Power saver stays on; returns to {ResolveReturnPlan(prev, cfg, Plans()).Target} on off");
                }
            }
        }
        catch (Exception e) when (Fails(e)) { Say($"power mode: COULD NOT RUN: {e.Message}"); return 2; }
        if (written is not null) SayAll(StatusLines(written, flag is not null ? "" : " (replaced an unreadable flag file)"));
        else SayAll(StatusLines(flag!, on ? " (already on, since kept)" : " (already off, since kept)"));
        SayAll(notes);
        return 0;
    }

    /// <summary>
    /// The end of an eco (power-mode-end.ps1, what the end task runs): an eco flag whose `until` has passed, or equals
    /// <paramref name="taskUntil"/>, pays its owed plan and GPU limit, is rewritten to normal since that `until`, and the
    /// task goes. A flag that is not eco is left alone (a task that still calls is stale and goes). Exit 0 · 2.
    /// </summary>
    private int End(string path, string taskUntil)
    {
        var notes = new List<string>();
        using (var flagLock = TryEnterLock(out string lockError))
        {
            if (flagLock is null) { Say($"power mode: COULD NOT RUN: {lockError}"); return 2; }
            if (EndUnderLock(path, taskUntil, notes) is { } code) return code;
        }
        Say("power mode: normal (eco ended)");
        SayAll(notes);
        return 0;
    }

    /// <summary>The end's read, decision and write, under the lock · null = ended (the caller prints it) · else the exit code.</summary>
    private int? EndUnderLock(string path, string taskUntil, List<string> notes)
    {
        try
        {
            var flag = ReadFlag(path);
            if (flag is null) { Say($"power mode: COULD NOT READ {path} (not a valid flag file)"); return 2; }
            bool fromTask = DateTimeOffset.TryParse(taskUntil, Inv, DateTimeStyles.None, out var taskAt);
            if (flag.Raw != "eco" || flag.UntilAt is null)
            {
                if (fromTask) RemoveEndTask();
                SayAll(StatusLines(flag, " (end: no eco to end)"));
                return 0;
            }
            // A later eco replaced the task this call came from, or it is a manual call ahead of time: not due.
            bool due = flag.Expired || (fromTask && taskAt == flag.UntilAt.Value);
            if (!due) { SayAll(StatusLines(flag, " (end: not due, eco runs on)")); return 0; }
            var cfg = ReadConfig(path);
            notes.AddRange(cfg.Warn);
            string left = RestorePlan(OwedPlan(flag, null), notes, cfg);
            double? gpuLeft = RestoreGpuLimit(flag, cfg, notes);
            double? capLeft = Truthy(gpuLeft) ? flag.GpuCapW : null;
            string text = "{ " + FormatFields("normal", flag.Until, "", "", "", left, gpuLeft, capLeft, null, flag.Battery) + " }\n";
            InitFlagDir(path);
            ReplaceFile(path, text, (tmp, target) =>
            {
                if (File.Exists(target)) File.Replace(tmp, target, null);
                else File.Move(tmp, target);
            }, "the flag file could not be replaced in 10 tries: ");
            RemoveEndTask();
            return null;
        }
        catch (Exception e) when (Fails(e)) { Say($"power mode: COULD NOT RUN: {e.Message}"); return 2; }
    }
}
