using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace BluettiWatcher.PowerMode;

/// <summary>A tool's answer: exit code (-1 could not start, -2 timed out) and its non-blank lines.</summary>
internal sealed record PmToolResult(int Code, IReadOnlyList<string> Out)
{
    public string LastLine => Out.Count > 0 ? Out[^1] : $"exit {Code.ToString(CultureInfo.InvariantCulture)}";
}

internal sealed record PmPlans(IReadOnlyList<string> All, string Active, IReadOnlyDictionary<string, string> Names);

internal sealed class PmConfig
{
    public string ReturnPlan { get; set; } = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";   // High performance
    public bool GpuCap { get; set; } = true;
    public List<string> Warn { get; } = [];
}

internal sealed record PmGpuLimits(double Limit, double Min, double Default, string Error);

internal sealed record PmGpuSet(bool Reached, double? Applied, bool TimedOut, string Why);

internal sealed record PmGpuTask(string State, int? Watts);

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed partial class PowerModeSwitch
{
    // ---- the seams: nothing reaches powercfg, the Task Scheduler or nvidia-smi any other way ----------------------------

    private static string QuoteArgument(string a) =>
        a.Length > 0 && !a.Any(c => char.IsWhiteSpace(c) || c == '"') ? a : "\"" + a.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private int ToolTimeoutMs() =>
        ToolTimeoutPattern().IsMatch(Env("POWER_MODE_TOOL_TIMEOUT_MS")) ? int.Parse(Env("POWER_MODE_TOOL_TIMEOUT_MS"), Inv) : 5000;

    [GeneratedRegex(@"^\d{1,6}$", RegexOptions.CultureInvariant)]
    private static partial Regex ToolTimeoutPattern();

    [GeneratedRegex(@"^\d{1,5}$", RegexOptions.CultureInvariant)]
    private static partial Regex ShortNumberPattern();

    /// <summary>A child process with no window, killed (whole tree) after <paramref name="timeoutMs"/>: code -2 = timed out.</summary>
    internal static PmToolResult RunTimed(string file, IEnumerable<string> list, int timeoutMs)
    {
        string name = Path.GetFileName(file);
        var psi = new ProcessStartInfo(file)
        {
            Arguments = string.Join(' ', list.Select(QuoteArgument)),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var stdout = new List<string>();
        var stderr = new List<string>();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.Add(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.Add(e.Data); };
        try { p.Start(); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException) { return new(-1, [$"{name} could not start: {e.Message}"]); }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(timeoutMs))
        {
            // The whole tree goes: a tool's own children must not outlive the call either.
            try { p.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or AggregateException) { Skip(e); }
            return new(-2, [$"{name} timed out after {Num(timeoutMs)} ms (stopped)"]);
        }
        p.WaitForExit();   // the output events are drained
        lock (stdout) lock (stderr) return new(p.ExitCode, [.. stdout.Concat(stderr).Where(l => l.Trim().Length > 0)]);
    }

    /// <summary>A real System32 tool: under POWER_MODE_TEST=1 the command stops (exit 2) and the tool is never started.</summary>
    private PmToolResult RealTool(string exeName, IEnumerable<string> list)
    {
        if (Env("POWER_MODE_TEST") == "1")
        {
            Say($"power mode: COULD NOT RUN: POWER_MODE_TEST is set and no stand-in names {exeName} (a test never starts the real one)");
            throw new PowerModeExit(2);
        }
        return RunTimed(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exeName), list, ToolTimeoutMs());
    }

    private static string PsLiteral(string s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";

    private ScriptStandInHost? _standInHost;

    /// <summary>The stand-in seams that are scripts (*.ps1): they need a PowerShell host, started before the lock is taken.</summary>
    private static readonly string[] SeamNames = ["POWER_MODE_POWERCFG", "POWER_MODE_SCHTASKS", "POWER_MODE_NVIDIASMI"];

    private bool ScriptSeams => SeamNames.Any(n => Env(n).EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) && File.Exists(Env(n)));

    private void StartStandInHost()
    {
        if (_standInHost is null && ScriptSeams) _standInHost = new ScriptStandInHost(_env);
    }

    /// <summary>The stand-in host, when one was started, goes.</summary>
    public void Dispose() => StopStandInHost();

    private void StopStandInHost()
    {
        _standInHost?.Dispose();
        _standInHost = null;
    }

    /// <summary>A stand-in script (*.ps1, tests): run as power-mode.ps1 ran it in its own process (<see cref="ScriptStandInHost"/>).</summary>
    private PmToolResult RunScriptStandIn(string script, IReadOnlyList<string> list)
    {
        if (!File.Exists(script))
            return new(-1, [$"The term '{script}' is not recognized as a name of a cmdlet, function, script file, or executable program.{Environment.NewLine}Check the spelling of the name, or if a path was included, verify that the path is correct and try again."]);
        StartStandInHost();
        return _standInHost!.Run(script, list, Math.Max(ToolTimeoutMs(), 120000));
    }
    private PmToolResult Seam(string standIn, string exe, IReadOnlyList<string> list)
    {
        if (standIn.Length == 0) return RealTool(exe, list);
        if (!standIn.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)) return RunTimed(standIn, list, ToolTimeoutMs());
        return RunScriptStandIn(standIn, list);
    }

    private PmToolResult PowerCfg(params string[] list) => Seam(Env("POWER_MODE_POWERCFG"), "powercfg.exe", list);
    private PmToolResult SchTasks(params string[] list) => Seam(Env("POWER_MODE_SCHTASKS"), "schtasks.exe", list);

    /// <summary>A simulated machine (a powercfg or schtasks stand-in) that names no nvidia-smi stand-in never reaches the GPU.</summary>
    private bool GpuSeam => Env("POWER_MODE_NVIDIASMI").Length > 0 || (Env("POWER_MODE_POWERCFG").Length == 0 && Env("POWER_MODE_SCHTASKS").Length == 0);

    private PmToolResult NvidiaSmi(params string[] list) =>
        GpuSeam ? Seam(Env("POWER_MODE_NVIDIASMI"), "nvidia-smi.exe", list) : new(-1, ["nvidia-smi is not reached from a simulated machine"]);

    // ---- elevation and the GPU ---------------------------------------------------------------------------------------------

    private static bool RealElevation()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private bool Elevated => Env("POWER_MODE_ELEVATED") switch { "1" => true, "0" => false, _ => RealElevation() };

    private PmGpuTask GpuTaskWatts(string name)
    {
        var r = SchTasks("/query", "/tn", name, "/xml");
        return r.Code != 0 ? new("missing", null) : ParseGpuTaskXml(string.Join('\n', r.Out));
    }

    /// <summary>The power limit in force, watts · null = no answer.</summary>
    private double? GpuLimitNow()
    {
        var r = NvidiaSmi("--query-gpu=power.limit", "--format=csv,noheader,nounits");
        if (r.Code != 0) return null;
        return double.TryParse((r.Out.Count > 0 ? r.Out[0] : "").Trim(), NumberStyles.Float, Inv, out var n) ? n : null;
    }

    /// <summary>
    /// cap | restore. Elevated: <c>nvidia-smi -pl</c>. Not elevated: the cap or restore task runs its own fixed wattage,
    /// confirmed by a read for up to 5 s (POWER_MODE_GPU_WAIT_MS). TimedOut = it may or may not have happened.
    /// </summary>
    private PmGpuSet SetGpuLimit(double watts, string kind)
    {
        if (Elevated)
        {
            var set = NvidiaSmi("-pl", Watts(watts));
            return set.Code == 0 ? new(true, watts, false, "") : new(false, null, set.Code == -2, set.LastLine);
        }
        string task = kind == "cap" ? GpuCapTask : GpuRestoreTask;
        var t = GpuTaskWatts(task);
        if (t.State != "ok")
        {
            string what = t.State == "missing" ? "is not registered" : "is not the one gpu-task install registers";
            return new(false, null, false, $"not elevated and the task {task} {what}: run power-mode.ps1 gpu-task install in an elevated shell");
        }
        int fixedWatts = t.Watts ?? 0;
        var r = SchTasks("/run", "/tn", task);
        if (r.Code != 0) return new(false, null, r.Code == -2, $"the task {task} could not be started: {r.LastLine}");
        int waitMs = ShortNumberPattern().IsMatch(Env("POWER_MODE_GPU_WAIT_MS")) ? int.Parse(Env("POWER_MODE_GPU_WAIT_MS"), Inv) : 5000;
        var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
        double? seen;
        do
        {
            seen = GpuLimitNow();
            if (seen is { } s && Math.Abs(s - fixedWatts) < 0.5) return new(true, fixedWatts, false, "");
            Thread.Sleep(250);
        } while (DateTime.UtcNow < deadline);
        string reads = seen is { } w ? $"{Watts(w)} W" : "nothing";
        return new(false, null, true, $"the task {task} ran but the limit reads {reads}, not {Num(fixedWatts)} W");
    }

    /// <summary>The first GPU's limit, minimum and default · Error set when nvidia-smi is missing or fails.</summary>
    private PmGpuLimits GpuLimits()
    {
        var r = NvidiaSmi("--query-gpu=power.limit,power.min_limit,power.default_limit", "--format=csv,noheader,nounits");
        if (r.Code != 0) return new(0, 0, 0, r.LastLine);
        string first = r.Out.Count > 0 ? r.Out[0] : "";
        var v = new List<double>();
        foreach (var cell in first.Split(',').Select(c => c.Trim()))
        {
            if (!double.TryParse(cell, NumberStyles.Float, Inv, out var n) || n <= 0) return new(0, 0, 0, $"nvidia-smi did not report the power limits: {first}");
            v.Add(n);
        }
        return v.Count != 3 ? new(0, 0, 0, $"nvidia-smi did not report the power limits: {first}") : new(v[0], v[1], v[2], "");
    }

    /// <summary>The recorded limit back when eco ends · null = nothing owed any more · the watts = still owed.</summary>
    private double? RestoreGpuLimit(PmFlag flag, PmConfig cfg, List<string> notes)
    {
        if (flag.PrevGpuLimitW is not { } target || target == 0)
        {
            if (flag.Raw == "eco" && cfg.GpuCap && GpuSeam) notes.Add("note: gpu: this eco recorded no power limit to return to: the GPU is left as it is");
            return null;
        }
        if (!GpuSeam) return target;
        var r = SetGpuLimit(target, "restore");
        double applied = r.Applied ?? 0;
        if (r.Reached && Math.Abs(applied - target) < 0.5) { notes.Add($"gpu: power limit back to {Watts(target)} W"); return null; }
        if (r.Reached)
        {
            notes.Add($"note: the GPU power limit is {Watts(applied)} W (the fixed value of {GpuRestoreTask}), not the recorded {Watts(target)} W: it stays owed in the flag; an elevated shell pays it: power-mode.ps1 off");
            return target;
        }
        if (r.TimedOut) { notes.Add($"note: the GPU power limit was not confirmed at {Watts(target)} W ({r.Why}): it stays owed in the flag, run off again"); return target; }
        notes.Add($"note: the GPU power limit could not be set back to {Watts(target)} W ({r.Why}): it stays owed in the flag, run off again");
        return target;
    }

    /// <summary>The cap again when the live limit drifted above it while eco is on; the flag is not written.</summary>
    private void GpuRecap(PmFlag flag, PmConfig cfg, List<string> notes)
    {
        if (!Truthy(flag.PrevGpuLimitW) || flag.GpuCapW is not { } cap || cap == 0 || !cfg.GpuCap || !GpuSeam) return;
        if (GpuLimitNow() is not { } live || live <= cap + 0.5) return;
        var r = SetGpuLimit(cap, "cap");
        notes.Add(r.Reached
            ? $"note: gpu cap re-applied: the limit read {Watts(live)} W, now {Watts(r.Applied ?? 0)} W"
            : $"note: gpu cap: the limit reads {Watts(live)} W, above the cap {Watts(cap)} W, and could not be set again ({r.Why})");
    }

    // ---- power plans and the config --------------------------------------------------------------------------------------

    [GeneratedRegex(@"([0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})\s*(\(([^)]*)\))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlanLinePattern();

    /// <summary>`balanced` | `high` | `saver` | a plan GUID -> the GUID in lower case · "" = neither.</summary>
    internal static string ResolvePlanAlias(string text)
    {
        if (text == "balanced") return BalancedPlan;
        if (text == "high") return HighPlan;
        if (text == "saver") return SaverPlan;
        string g = text.ToLowerInvariant();
        return GuidPattern().IsMatch(g) ? g : "";
    }

    private static string PlanName(string plan, PmPlans? plans) => plan switch
    {
        BalancedPlan => "Balanced",
        HighPlan => "High performance",
        SaverPlan => "Power saver",
        _ => plans is not null && plans.Names.TryGetValue(plan, out var n) && n.Length > 0 ? n : plan,
    };

    private static string PlanLabel(string plan, PmPlans? plans)
    {
        string name = PlanName(plan, plans);
        return name == plan ? plan : $"{name} ({plan})";
    }

    /// <summary>The plans from <c>powercfg /list</c> (the active one ends with <c>*</c> in every UI language) · null = no answer.</summary>
    private PmPlans? Plans()
    {
        var r = PowerCfg("/list");
        if (r.Code != 0) return null;
        var all = new List<string>();
        string active = "";
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in r.Out)
        {
            var m = PlanLinePattern().Match(line);
            if (!m.Success) continue;
            string g = m.Groups[1].Value.ToLowerInvariant();
            all.Add(g);
            if (m.Groups[4].Value.Length > 0) names[g] = m.Groups[4].Value.Trim();
            if (line.TrimEnd().EndsWith('*')) active = g;
        }
        return new(all, active, names);
    }

    /// <summary>power-mode.config.json beside the flag: returnPlan and gpuCap; a file or key that cannot be read = the default plus a warn: line.</summary>
    internal static PmConfig ReadConfig(string flagPath)
    {
        var cfg = new PmConfig();
        string file = Path.Combine(Path.GetDirectoryName(flagPath) ?? "", "power-mode.config.json");
        if (!File.Exists(file)) return cfg;
        Dictionary<string, object?>? j;
        try { j = ParseObject(ReadText(file)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Skip(e); j = null; }
        if (j is null)
        {
            cfg.Warn.Add("warn: power-mode.config.json is not a JSON object: the defaults apply (High performance, GPU cap on)");
            return cfg;
        }
        if (j.TryGetValue("returnPlan", out var rp))
        {
            string plan = rp is string s ? (s == "previous" ? "previous" : ResolvePlanAlias(s)) : "";
            if (plan.Length > 0 && plan != SaverPlan) cfg.ReturnPlan = plan;
            else cfg.Warn.Add("warn: power-mode.config.json: returnPlan is not high, balanced, previous or a plan GUID (never Power saver): High performance applies");
        }
        if (j.TryGetValue("gpuCap", out var gc))
        {
            if (gc is bool on) cfg.GpuCap = on;
            else cfg.Warn.Add("warn: power-mode.config.json: gpuCap is not true or false: the GPU cap stays on");
        }
        return cfg;
    }

    /// <summary>The plan the end of eco sets: (target, warn). A target not on this machine = Balanced with a warn: line.</summary>
    private static (string Target, string Warn) ResolveReturnPlan(string prev, PmConfig cfg, PmPlans? plans)
    {
        string want = cfg.ReturnPlan;
        if (want == "previous")
            want = prev.Length > 0 && prev != SaverPlan && (plans is null || plans.All.Contains(prev)) ? prev : HighPlan;
        if (plans is null || plans.All.Contains(want)) return (want, "");
        return (BalancedPlan, $"warn: the power plan {PlanLabel(want, plans)} is not on this machine: Balanced is used instead");
    }

    /// <summary>The plan a flag owes: prevPlan; an eco flag without one owes High performance only while Power saver is active.</summary>
    private string OwedPlan(PmFlag flag, PmPlans? plans)
    {
        if (flag.PrevPlan.Length > 0) return flag.PrevPlan;
        if (flag.Raw != "eco") return "";
        plans ??= Plans();
        return plans is not null && plans.Active == SaverPlan ? HighPlan : "";
    }

    /// <summary>The owed plan paid · "" = done · the plan = a step failed, the debt stays in the flag.</summary>
    private string RestorePlan(string plan, List<string> notes, PmConfig cfg)
    {
        if (plan.Length == 0) return "";
        var plans = Plans();
        var to = ResolveReturnPlan(plan, cfg, plans);
        if (to.Warn.Length > 0) notes.Add(to.Warn);
        string name = PlanName(to.Target, plans);
        var r = PowerCfg("/setactive", to.Target);
        if (r.Code != 0) { notes.Add($"warn: the {name} plan could not be set ({r.LastLine}): it stays owed in the flag, run off again"); return plan; }
        if (to.Target != BalancedPlan) { notes.Add($"plan: {name}"); return ""; }
        r = PowerCfg("/overlaysetactive", BestPerformanceOverlay);
        if (r.Code != 0) { notes.Add($"warn: the Best performance power mode could not be set ({r.LastLine}): it stays owed in the flag, run off again"); return plan; }
        notes.Add("plan: Balanced, Best performance");
        return "";
    }

    // ---- scheduled tasks -------------------------------------------------------------------------------------------------

    internal static string XmlEscape(string s) => s.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal).Replace("'", "&apos;", StringComparison.Ordinal);

    private static string XmlUnescape(string s) => s.Replace("&quot;", "\"", StringComparison.Ordinal).Replace("&apos;", "'", StringComparison.Ordinal)
        .Replace("&lt;", "<", StringComparison.Ordinal).Replace("&gt;", ">", StringComparison.Ordinal).Replace("&amp;", "&", StringComparison.Ordinal);

    /// <summary>
    /// The eco end task: this exe (a WinExe: no window) with <paramref name="arguments"/>, as the interactive user, least
    /// privilege, only while they are logged on - never SYSTEM; a run missed while they were logged off starts at the next logon.
    /// </summary>
    internal static string EndTaskXml(string userSid, string exe, string arguments, DateTimeOffset at) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>BluettiWatcher.exe power-mode: ends eco mode and brings the power plan back</Description></RegistrationInfo>
          <Triggers><TimeTrigger><StartBoundary>{Iso(at)}</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>
          <Principals><Principal id="Author"><UserId>{userSid}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <StartWhenAvailable>true</StartWhenAvailable>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>true</Hidden>
            <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
          </Settings>
          <Actions Context="Author"><Exec><Command>{XmlEscape(exe)}</Command><Arguments>{XmlEscape(arguments)}</Arguments></Exec></Actions>
        </Task>

        """;

    /// <summary>The end task's arguments: the flag, then every live name (the Task Scheduler passes no environment), then the `until`.</summary>
    internal string EndTaskArguments(string path, string untilText)
    {
        string readers = ExtraReaders.Count > 0 ? $" -ExtraReaders \"{string.Join(',', ExtraReaders)}\"" : "";
        return $"power-mode end -Path \"{path}\" -FlagFile \"{DefaultFlagPath}\" -Mutex {MutexName} -TaskPrefix {TaskPrefix}{readers} -TaskUntil {untilText}";
    }

    private PmToolResult RegisterEndTask(string path, DateTimeOffset untilAt, string untilText)
    {
        string xmlFile = Path.Combine(Path.GetTempPath(), $"power-mode-end-task-{Num(Environment.ProcessId)}-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(xmlFile, EndTaskXml(MeSid(), ExePath, EndTaskArguments(path, untilText), untilAt), Encoding.Unicode);
            return SchTasks("/create", "/tn", EndTask, "/xml", xmlFile, "/f");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(-1, [e.Message]); }
        finally
        {
            if (File.Exists(xmlFile)) File.Delete(xmlFile);
        }
    }

    private void RemoveEndTask() => _ = SchTasks("/delete", "/tn", EndTask, "/f");

    private bool EndTaskPresent() => SchTasks("/query", "/tn", EndTask).Code == 0;

    /// <summary>
    /// A GPU task: System32 nvidia-smi with a wattage fixed at install, hosted by System32 conhost.exe --headless (no window),
    /// the user's account, highest run level, no trigger (started by <c>schtasks /run</c>), one minute at most. Never this exe:
    /// it lives where the user writes, and what runs elevated takes nothing a process that is not elevated can write.
    /// </summary>
    internal static string GpuTaskXml(string userSid, string name, int watts)
    {
        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string arguments = $"--headless \"{Path.Combine(sys, "nvidia-smi.exe")}\" -pl {Num(watts)}";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>{XmlEscape($"power-mode.ps1: GPU power limit {Num(watts)} W ({name})")}</Description></RegistrationInfo>
              <Principals><Principal id="Author"><UserId>{userSid}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>true</Hidden>
                <ExecutionTimeLimit>PT1M</ExecutionTimeLimit>
              </Settings>
              <Actions Context="Author"><Exec><Command>{XmlEscape(Path.Combine(sys, "conhost.exe"))}</Command><Arguments>{XmlEscape(arguments)}</Arguments></Exec></Actions>
            </Task>

            """;
    }

    /// <summary>Administrators and SYSTEM full control, the user read and run - never change or delete it unelevated.</summary>
    internal static string GpuTaskSddl(string userSid) => $"D:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;FRFX;;;{userSid})";

    [GeneratedRegex("<Command>([^<]*)</Command>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommandPattern();

    [GeneratedRegex("<Arguments>([^<]*)</Arguments>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArgumentsPattern();

    [GeneratedRegex("<RunLevel>HighestAvailable</RunLevel>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HighestPattern();

    [GeneratedRegex("^--headless \"([^\"]+)\" -pl ([1-9][0-9]{0,3})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GpuActionPattern();

    /// <summary>A GPU task's XML (as written, or as <c>schtasks /query /xml</c> prints it): ok only for exactly the action install writes.</summary>
    internal static PmGpuTask ParseGpuTaskXml(string text)
    {
        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var c = CommandPattern().Match(text);
        if (!c.Success) return new("foreign", null);
        string cmd = XmlUnescape(c.Groups[1].Value).Trim();
        var a = ArgumentsPattern().Match(text);
        if (!a.Success) return new("foreign", null);
        string arg = XmlUnescape(a.Groups[1].Value).Trim();
        // A task that would run without elevation (a name squatted before install) is not ours either.
        if (!HighestPattern().IsMatch(text)) return new("foreign", null);
        var m = GpuActionPattern().Match(arg);
        if (!string.Equals(cmd, Path.Combine(sys, "conhost.exe"), StringComparison.OrdinalIgnoreCase) || !m.Success
            || !string.Equals(m.Groups[1].Value, Path.Combine(sys, "nvidia-smi.exe"), StringComparison.OrdinalIgnoreCase))
            return new("foreign", null);
        return new("ok", int.Parse(m.Groups[2].Value, Inv));
    }

    [GeneratedRegex("^[1-9][0-9]{0,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex WholeWattsPattern();

    /// <summary>What install registers: (cap, restore) from the card's min, max and default and an optional -RestoreWatts · Error.</summary>
    internal static (int Cap, int Restore, string Error) ResolveGpuTaskWatts(double min, double max, double def, string restoreWatts)
    {
        int lo = (int)Math.Ceiling(min), hi = (int)Math.Floor(max);
        int restore = (int)Math.Round(def);
        if (restoreWatts.Length > 0)
        {
            if (!WholeWattsPattern().IsMatch(restoreWatts)) return (0, 0, $"-RestoreWatts '{restoreWatts}' is not a whole number of watts");
            restore = int.Parse(restoreWatts, Inv);
        }
        if (lo <= 0 || hi < lo) return (0, 0, $"the card reported no usable power limit range ({Num(min)}..{Num(max)} W)");
        if (restore < lo || restore > hi) return (0, 0, $"the restore limit {Num(restore)} W is outside the card's {Num(lo)}..{Num(hi)} W");
        return (lo, restore, "");
    }

    /// <summary>
    /// Registers a GPU task from its XML text through the Task Scheduler's COM API (RegisterTask, create or update,
    /// interactive token, the task's own SDDL), hosted by System32 Windows PowerShell 5.1: no file on disk. Never under a test.
    /// </summary>
    private PmToolResult RegisterGpuTask(string name, string xml)
    {
        if (Env("POWER_MODE_TEST") == "1")
        {
            Say("power mode: COULD NOT RUN: POWER_MODE_TEST is set: a test never registers a task");
            throw new PowerModeExit(2);
        }
        // 6 = TASK_CREATE_OR_UPDATE, 3 = TASK_LOGON_INTERACTIVE_TOKEN
        string body = "$ErrorActionPreference = 'Stop'; $svc = New-Object -ComObject 'Schedule.Service'; $svc.Connect(); "
            + $"$null = $svc.GetFolder('\\').RegisterTask({PsLiteral(name)}, {PsLiteral(xml)}, 6, $null, $null, 3, {PsLiteral(GpuTaskSddl(MeSid()))}); 'registered'";
        string ps51 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var r = RunTimed(ps51, ["-NoProfile", "-NonInteractive", "-EncodedCommand", System.Convert.ToBase64String(Encoding.Unicode.GetBytes(body))], Math.Max(ToolTimeoutMs(), 60000));
        return r.Code == 0 ? new(0, [$"registered {name}"]) : new(-1, r.Out.Count > 0 ? r.Out : [$"exit {Num(r.Code)}"]);
    }
}
