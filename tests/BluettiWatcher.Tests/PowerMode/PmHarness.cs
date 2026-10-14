// A port of the harness of power-mode/power-mode.test.js: world(), machine(), cli(), the stand-in powercfg / schtasks /
// nvidia-smi scripts, FLAG_RE, iso(), ecoFlag() and the process helpers. Every flag lives in a private temp directory; the
// live flag, the real power plan and the real Task Scheduler are never touched.
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit.Sdk;

namespace BluettiWatcher.Tests.PowerModeScript;

/// <summary>An exit status and the two streams, as the JS harness's { status, out, err }.</summary>
internal sealed record Res(int Status, string Out, string Err);

/// <summary>t.after(): actions run in reverse order when the test ends.</summary>
internal sealed class Cleanup : IAsyncDisposable
{
    private readonly List<Action> _actions = [];

    public void After(Action action) => _actions.Add(action);

    public ValueTask DisposeAsync()
    {
        List<Exception> errors = [];
        for (int i = _actions.Count - 1; i >= 0; i--)
        {
            try { _actions[i](); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { errors.Add(e); }
        }
        return errors.Count > 0 ? ValueTask.FromException(new AggregateException("cleanup failed", errors)) : ValueTask.CompletedTask;
    }
}

/// <summary>A flag directory (the JS world()).</summary>
internal class World
{
    public World(string dir) { Dir = dir; Flag = Path.Combine(dir, "power-mode.json"); }
    public string Dir { get; }
    public string Flag { get; }
}

/// <summary>A flag directory plus a simulated machine: power plans, the active one, the Task Scheduler, every call logged (the JS machine()).</summary>
internal sealed class Machine : World
{
    public Machine(string dir, string sim, Dictionary<string, string> env) : base(dir)
    {
        Sim = sim;
        Env = env;
        TaskFile = Path.Combine(sim, Pm.TaskFileName);
    }

    public string Sim { get; }
    public string TaskFile { get; }
    public Dictionary<string, string> Env { get; }

    public string[] Files() => Pm.Ls(Dir).Where(f => f != "power-mode.config.json").ToArray();

    public void RegisterGpu(int cap, int restore)
    {
        File.WriteAllText(Path.Combine(Sim, $"task-{Pm.CapTask}.xml"), Pm.GpuTaskXml(cap));
        File.WriteAllText(Path.Combine(Sim, $"task-{Pm.RestoreTask}.xml"), Pm.GpuTaskXml(restore));
    }

    public int Gpu() => int.Parse(File.ReadAllText(Path.Combine(Sim, "gpu.txt")).Split(',')[0], CultureInfo.InvariantCulture);

    public string[] GpuCalls() => Calls().Where(c => c.StartsWith("nvidia-smi", StringComparison.Ordinal)).ToArray();

    public Task<Res> Run(string[] args, Dictionary<string, string>? more = null) =>
        Pm.CliAsync([.. args, "-Path", Flag], null, Pm.Merge(Env, more));

    public async Task<int> Code(string[] args, Dictionary<string, string>? more = null) => (await Run(args, more)).Status;

    public string Active() => File.ReadAllText(Path.Combine(Sim, "active.txt")).Trim();

    public string? Overlay()
    {
        string f = Path.Combine(Sim, "overlay.txt");
        return File.Exists(f) ? File.ReadAllText(f).Trim() : null;
    }

    public string[] Calls() => File.ReadAllText(Path.Combine(Sim, "calls.log")).Split('\n').Where(c => c.Length > 0).ToArray();

    public string? TaskXml() => File.Exists(TaskFile) ? File.ReadAllText(TaskFile, Encoding.Unicode).TrimStart('\uFEFF') : null;

    public JsonObject Json() => Pm.ParseObject(Pm.ReadText(Flag));
}

/// <summary>A child pwsh that opens a file for reading with FileShare.Read, prints "open" and holds it (the JS holdOpen()).</summary>
internal sealed class Holder : IDisposable
{
    private readonly Process _p;
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Holder(string file, int ms)
    {
        const string script = "$f = [IO.File]::Open($env:HOLD_FILE, 'Open', 'Read', 'Read'); [Console]::Out.WriteLine('open'); [Console]::Out.Flush(); "
            + "Start-Sleep -Milliseconds ([int]$env:HOLD_MS); $f.Dispose()";
        Dictionary<string, string> env = Pm.Merge(Pm.BaseEnv(), new() { ["HOLD_FILE"] = file, ["HOLD_MS"] = ms.ToString(CultureInfo.InvariantCulture) });
        _p = Pm.StartProc("pwsh", ["-NoProfile", "-NonInteractive", "-Command", script], env);
        _p.StandardInput.Close();
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await _p.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
                if (line.Contains("open", StringComparison.Ordinal)) _opened.TrySetResult();
            _opened.TrySetException(new InvalidOperationException("holder exited before opening"));
        });
        _ = _p.StandardError.ReadToEndAsync();
    }

    public Task Opened => _opened.Task;

    public Task Done => _p.WaitForExitAsync();

    public void Kill()
    {
        if (!_p.HasExited) _p.Kill();
    }

    public void Dispose()
    {
        Kill();
        _p.Dispose();
    }
}

/// <summary>The shared harness: constants, the stand-in scripts, process helpers and the small assertion helpers.</summary>
internal static class Pm
{
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string Saver = "a1841308-3541-4fab-bc81-f71556f20b4a";
    public const string Best = "ded574b5-45a0-4f42-8737-46345c09c238";
    public const string High = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string Custom = "11111111-2222-3333-4444-555555555555";
    public const string Gone = "99999999-8888-7777-6666-555555555555";
    public static readonly string[] All = [Balanced, Saver, High, Custom];
    public const string TaskFileName = "task-BluettiWatcherEcoEnd.xml";
    public const string CapTask = "BluettiWatcherEcoGpuCap";
    public const string RestoreTask = "BluettiWatcherEcoGpuRestore";
    public const string GpuQuery = "nvidia-smi --query-gpu=power.limit,power.min_limit,power.default_limit --format=csv,noheader,nounits";
    public const string LiveQuery = "nvidia-smi --query-gpu=power.limit --format=csv,noheader,nounits";
    // The zone the tests pin (POWER_MODE_TZ): the IANA id for PowerShell 7, the Windows id for Windows PowerShell 5.1.
    public const string PinTz = "Europe/Kyiv,FLE Standard Time";
    public const string EmergencyRe = """^power mode: EMERGENCY since [0-9]{2}\.[0-9]{2}\.[0-9]{4} [0-9]{2}:[0-9]{2}\z""";
    public const string DefaultConfig = """{"returnPlan":"balanced"}""";
    public const string OldNormal = """{ "mode": "normal", "since": "2026-10-02T22:20:00Z" }""" + "\n";
    public const string OldEmergency = """{ "mode": "emergency", "since": "2026-10-02T22:20:00Z" }""" + "\n";
    public const string OldSince = "2026-10-02T22:20:00Z";
    public static readonly Dictionary<string, string> NotElevated = new() { ["POWER_MODE_ELEVATED"] = "0" };

    public static readonly Regex FlagRe = new("""^\{ "mode": "(emergency|normal)", "since": "([0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}[+-][0-9]{2}:[0-9]{2})"(?:, "by": "manual")? \}\n\z""", RegexOptions.CultureInvariant);

    /// <summary>The long form of TEMP: CI runners hand an 8.3 short path (RUNNER~1) that PowerShell prints long.</summary>
    public static readonly string Tmp = LongPath(Path.GetTempPath());
    public static readonly string ScriptDir = FindScriptDir();
    public static readonly string CliScript = Path.Combine(ScriptDir, "power-mode.ps1");
    public static readonly string End = Path.Combine(ScriptDir, "power-mode-end.ps1");
    public static readonly string Lib = Path.Combine(ScriptDir, "power-mode-flag.ps1");
    /// <summary>The switch: BluettiWatcher.exe (BLUETTI_WATCHER_EXE, else the one built beside these tests); set for every child, so the forwarders find it too.</summary>
    public static readonly string Exe = FindExe();
    /// <summary>PM_SWITCH_ENTRY=script: every call through the forwarders (pwsh power-mode.ps1, Windows PowerShell 5.1 power-mode-end.ps1) instead of the exe.</summary>
    public static readonly bool ThroughScripts = Environment.GetEnvironmentVariable("PM_SWITCH_ENTRY") == "script";

    private static string FindExe()
    {
        string exe = Environment.GetEnvironmentVariable("BLUETTI_WATCHER_EXE") is { Length: > 0 } e ? e : Path.Combine(AppContext.BaseDirectory, "BluettiWatcher.exe");
        Environment.SetEnvironmentVariable("BLUETTI_WATCHER_EXE", exe);
        return exe;
    }
    public static readonly string SystemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? "C:\\Windows";
    public static readonly string Sys32 = Path.Combine(SystemRoot, "System32");
    public static readonly string Ps51 = Path.Combine(SystemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    public static readonly string ProgramData = Environment.GetEnvironmentVariable("ProgramData") ?? "C:\\ProgramData";

    private static readonly string[] NameVars = ["POWER_MODE_FILE", "POWER_MODE_MUTEX", "POWER_MODE_TASK_PREFIX", "POWER_MODE_EXTRA_READERS"];

    // ---- the stand-in scripts (the JS FAKE_POWERCFG / FAKE_NVIDIASMI / FAKE_SCHTASKS, as the text they write) -------------

    public const string FakePowercfg = """
        $d = $env:FAKE_PM_DIR
        [IO.File]::AppendAllText("$d\calls.log", "powercfg $($args -join ' ')`n")
        $plans = @(Get-Content -LiteralPath "$d\plans.txt")
        $active = (Get-Content -LiteralPath "$d\active.txt" -Raw).Trim()
        if ($args[0] -eq '/list') { 'Existing Power Schemes (* Active)'; foreach ($p in $plans) { "Power Scheme GUID: $p  (a plan)$(if ($p -eq $active) { ' *' })" }; exit 0 }
        if ($args[0] -eq '/setactive' -and $plans -contains $args[1] -and $env:FAKE_PM_SETFAIL -ne '1') { [IO.File]::WriteAllText("$d\active.txt", $args[1]); exit 0 }
        if ($args[0] -eq '/overlaysetactive' -and $env:FAKE_PM_OVERLAYFAIL -ne '1') { [IO.File]::WriteAllText("$d\overlay.txt", $args[1]); exit 0 }
        'Invalid Parameters -- try "/?" for help'; exit 1
        """;

    // gpu.txt = `limit,min,default` in watts; every call logged beside the powercfg ones.
    public const string FakeNvidiaSmi = """
        $d = $env:FAKE_PM_DIR
        [IO.File]::AppendAllText("$d\calls.log", "nvidia-smi $($args -join ' ')`n")
        $g = (Get-Content -LiteralPath "$d\gpu.txt" -Raw).Trim() -split ","
        if ($args[0] -like '--query-gpu=*') { if ($env:FAKE_GPU_QUERYFAIL -eq '1') { 'NVIDIA-SMI has failed because it could not communicate with the NVIDIA driver.'; exit 9 }; $map = @{ 'power.limit' = $g[0]; 'power.min_limit' = $g[1]; 'power.default_limit' = $g[2]; 'power.max_limit' = $g[2] }; (($args[0] -replace '^--query-gpu=', '') -split ',' | ForEach-Object { "$($map[$_]).00" }) -join ', '; exit 0 }
        if ($args[0] -eq '-pl' -and $env:FAKE_GPU_SETFAIL -ne '1') { [IO.File]::WriteAllText("$d\gpu.txt", "$($args[1]),$($g[1]),$($g[2])"); "Power limit for GPU 00000000:01:00.0 was set to $($args[1]).00 W"; exit 0 }
        'Changing power management limit is not permitted: Insufficient Permissions'; exit 4
        """;

    public const string FakeSchtasks = """
        $d = $env:FAKE_PM_DIR
        [IO.File]::AppendAllText("$d\calls.log", "schtasks $($args -join ' ')`n")
        if ($env:FAKE_PM_MUTEX) { $s = & "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -Command '$mx = New-Object Threading.Mutex($false, $env:FAKE_PM_MUTEX); try { if ($mx.WaitOne(0)) { $mx.ReleaseMutex(); ''free'' } else { ''held'' } } catch [Threading.AbandonedMutexException] { ''abandoned'' }'; [IO.File]::AppendAllText("$d\calls.log", "mutex $($env:FAKE_PM_MUTEX) $s`n") }
        $file ="$d\task-$($args[[array]::IndexOf($args, '/tn') + 1]).xml"
        if ($args[0] -eq '/create') { if ($env:FAKE_PM_DENY -eq '1') { 'ERROR: Access is denied.'; exit 1 }; [IO.File]::Copy($args[[array]::IndexOf($args, '/xml') + 1], $file, $true); 'SUCCESS: created'; exit 0 }
        if ($args[0] -eq '/delete' -and [IO.File]::Exists($file)) { [IO.File]::Delete($file); exit 0 }
        if ($args[0] -eq '/query' -and [IO.File]::Exists($file)) { if ($args -contains '/xml') { [IO.File]::ReadAllText($file) }; exit 0 }
        if ($args[0] -eq '/run' -and [IO.File]::Exists($file) -and $env:FAKE_PM_RUNFAIL -ne '1') { $x = [IO.File]::ReadAllText($file); if ($env:FAKE_PM_HELPER_IDLE -ne '1' -and $x -match '-pl (\d+)') { $g = (Get-Content -LiteralPath "$d\gpu.txt" -Raw).Trim() -split ','; [IO.File]::WriteAllText("$d\gpu.txt", "$($Matches[1]),$($g[1]),$($g[2])") }; 'SUCCESS: Attempted to run the scheduled task.'; exit 0 }
        'ERROR: The system cannot find the file specified.'; exit 1
        """;

    /// <summary>The action gpu-task install registers (New-GpuTaskXml), as schtasks /query /xml prints it.</summary>
    public static string GpuTaskXml(int watts) =>
        $"<Task><Principals><Principal id=\"Author\"><RunLevel>HighestAvailable</RunLevel></Principal></Principals><Actions Context=\"Author\"><Exec><Command>{Sys32}\\conhost.exe</Command><Arguments>--headless &quot;{Sys32}\\nvidia-smi.exe&quot; -pl {watts}</Arguments></Exec></Actions></Task>";

    // ---- time -----------------------------------------------------------------------------------------------------------

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>iso(): UTC, no fraction, +00:00.</summary>
    public static string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "+00:00";

    /// <summary>new Date(ms).toISOString().</summary>
    public static string IsoMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static long ParseMs(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

    public static string EcoFlag(long untilMs, string? prevPlan = null) =>
        "{ \"mode\": \"eco\", \"since\": \"" + Iso(untilMs - 3600000) + "\", \"until\": \"" + Iso(untilMs) + "\""
        + (prevPlan is null ? "" : ", \"prevPlan\": \"" + prevPlan + "\"") + " }\n";

    /// <summary>The flag text with its closing " }\n" replaced by tail (which carries its own).</summary>
    public static string Amend(string flag, string tail) => flag.Replace(" }\n", tail, StringComparison.Ordinal);

    public static string Owed(int prev, int cap) => $", \"prevGpuLimitW\": {prev}, \"gpuCapW\": {cap} }}\n";

    public static string FlagText(string mode, string extra = "") => $"{{ \"mode\": \"{mode}\", \"since\": \"2026-10-09T10:00:00+03:00\"{extra} }}\n";

    /// <summary>JSON.stringify({ mode: 'emergency', since }).</summary>
    public static string EmergencyJson(string since) => $"{{\"mode\":\"emergency\",\"since\":\"{since}\"}}";

    public static string NormalJson(string since) => $"{{\"mode\":\"normal\",\"since\":\"{since}\"}}";

    public static string EcoJson(string since, string until) => $"{{\"mode\":\"eco\",\"since\":\"{since}\",\"until\":\"{until}\"}}";

    public static double MinutesAhead(JsonObject j) => (ParseMs((string)j.V("until")!) - Now()) / 60000.0;

    // ---- files and processes ----------------------------------------------------------------------------------------------

    /// <summary>fs.readFileSync(f, 'utf8'): a BOM stays in the string.</summary>
    public static string ReadText(string file) => new UTF8Encoding(false).GetString(File.ReadAllBytes(file));

    /// <summary>fs.writeFileSync(f, text): UTF-8, no BOM.</summary>
    public static void WriteText(string file, string text) => File.WriteAllText(file, text);

    public static byte[] ReadBytes(string file) => File.ReadAllBytes(file);

    public static JsonObject ParseObject(string text) => JsonNode.Parse(text)!.AsObject();

    /// <summary>fs.readdirSync, sorted.</summary>
    public static string[] Ls(string dir) => Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).Select(n => n!).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    public static string MkTemp(Cleanup t, string prefix)
    {
        string dir = Path.Combine(Tmp, prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
        Directory.CreateDirectory(dir);
        t.After(() => RemoveTree(dir));
        return dir;
    }

    /// <summary>rmSync(dir, { recursive, force }): a junction inside is removed as a link, a handle a killed process has not let go of yet is waited for.</summary>
    public static void RemoveTree(string dir)
    {
        for (int attempt = 1; Directory.Exists(dir); attempt++)
        {
            try
            {
                RemoveLinks(dir);
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException) when (attempt < 30)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static void RemoveLinks(string dir)
    {
        foreach (string sub in Directory.GetDirectories(dir))
        {
            if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(sub);
            else RemoveLinks(sub);
        }
    }

    public static World NewWorld(Cleanup t) => new(MkTemp(t, "power-mode-cli-"));

    public static Machine NewMachine(Cleanup t, string[]? plans = null, string active = Balanced, int[]? gpu = null, string? config = DefaultConfig)
    {
        plans ??= [Balanced, Saver];
        string dir = MkTemp(t, "power-mode-cli-");
        string sim = MkTemp(t, "power-mode-sim-");
        File.WriteAllText(Path.Combine(sim, "plans.txt"), string.Join('\n', plans) + "\n");
        File.WriteAllText(Path.Combine(sim, "active.txt"), active);
        File.WriteAllText(Path.Combine(sim, "calls.log"), "");
        File.WriteAllText(Path.Combine(sim, "fake-powercfg.ps1"), FakePowercfg);
        File.WriteAllText(Path.Combine(sim, "fake-schtasks.ps1"), FakeSchtasks);
        Dictionary<string, string> env = Env(("FAKE_PM_DIR", sim), ("POWER_MODE_POWERCFG", Path.Combine(sim, "fake-powercfg.ps1")), ("POWER_MODE_SCHTASKS", Path.Combine(sim, "fake-schtasks.ps1")));
        if (gpu is not null)
        {
            File.WriteAllText(Path.Combine(sim, "gpu.txt"), string.Join(',', gpu));
            File.WriteAllText(Path.Combine(sim, "fake-nvidia-smi.ps1"), FakeNvidiaSmi);
            env["POWER_MODE_NVIDIASMI"] = Path.Combine(sim, "fake-nvidia-smi.ps1");
        }
        if (config is not null) File.WriteAllText(Path.Combine(dir, "power-mode.config.json"), config);
        return new Machine(dir, sim, env);
    }

    public static Dictionary<string, string> Env(params (string K, string V)[] kv)
    {
        Dictionary<string, string> d = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string k, string v) in kv) d[k] = v;
        return d;
    }

    /// <summary>The process environment without the four names an install sets: a runner's own values never reach a test that does not set them.</summary>
    public static Dictionary<string, string> BaseEnv()
    {
        Dictionary<string, string> d = new(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables()) d[(string)e.Key] = (string?)e.Value ?? "";
        foreach (string k in NameVars) d.Remove(k);
        return d;
    }

    public static Dictionary<string, string> Merge(Dictionary<string, string> a, Dictionary<string, string>? b)
    {
        Dictionary<string, string> d = new(a, StringComparer.OrdinalIgnoreCase);
        if (b is not null) foreach (KeyValuePair<string, string> kv in b) d[kv.Key] = kv.Value;
        return d;
    }

    public static Process StartProc(string file, IEnumerable<string> args, Dictionary<string, string>? env)
    {
        ProcessStartInfo psi = new(file)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (KeyValuePair<string, string> kv in env ?? BaseEnv()) psi.Environment[kv.Key] = kv.Value;
        return Process.Start(psi) ?? throw new InvalidOperationException("could not start " + file);
    }

    /// <summary>spawnSync's result shape without blocking a thread, so that the concurrent tests' children overlap.</summary>
    public static async Task<Res> SpawnAsync(string file, IEnumerable<string> args, Dictionary<string, string>? env = null)
    {
        using Process p = StartProc(file, args, env);
        p.StandardInput.Close();
        Task<string> so = p.StandardOutput.ReadToEndAsync();
        Task<string> se = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().ConfigureAwait(false);
        return new Res(p.ExitCode, await so.ConfigureAwait(false), await se.ConfigureAwait(false));
    }

    public static Res Norm(Res r) => new(r.Status, r.Out.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd(), r.Err);

    /// <summary>power-mode.ps1 through pwsh; args after the script. env.POWER_MODE_FILE is removed unless envFlag gives it.</summary>
    public static async Task<Res> CliAsync(string[] args, string? envFlag = null, Dictionary<string, string>? extra = null)
    {
        extra ??= [];
        Dictionary<string, string> env = Merge(BaseEnv(), extra);
        env.Remove("POWER_MODE_FILE");
        // Times print in the PC's zone; the tests pin it (POWER_MODE_TZ) so the expected text does not depend on the runner's.
        if (!extra.ContainsKey("POWER_MODE_TZ")) env["POWER_MODE_TZ"] = PinTz;
        if (envFlag is not null) env["POWER_MODE_FILE"] = envFlag;
        // Elevated unless a test says otherwise: the answer never depends on the shell the suite runs in.
        if (!extra.ContainsKey("POWER_MODE_ELEVATED")) env["POWER_MODE_ELEVATED"] = "1";
        env["POWER_MODE_TEST"] = "1"; // a call that names no stand-in stops the script: the real tools are never started
        // No test reaches the real powercfg or Task Scheduler: without a simulated machine the seams name a script that does not exist.
        foreach (string seam in new[] { "POWER_MODE_POWERCFG", "POWER_MODE_SCHTASKS" })
            if (!extra.ContainsKey(seam)) env[seam] = Path.Combine(Path.GetTempPath(), "power-mode-no-such-seam.ps1");
        (string file, string[] argv) = CliCommand(args);
        return Norm(await SpawnAsync(file, argv, env).ConfigureAwait(false));
    }

    // ---- THE one place that says how the switch and the end script are started: switching the tests to the exe
    // (`BluettiWatcher.exe power-mode <args>`) is a change of these members only (and of Cli / End / Lib above). ------------------

    /// <summary>The command that runs the switch with args (today pwsh -File power-mode.ps1).</summary>
    public static (string File, string[] Args) CliCommand(string[] args) => ThroughScripts ? ("pwsh", ["-NoProfile", "-NonInteractive", "-File", CliScript, .. args]) : (Exe, ["power-mode", .. args]);

    /// <summary>The same call as text inside a PowerShell host script (the poller calls the switch this way).</summary>
    public static string CliCallInHost => $"& '{CliScript}'";

    /// <summary>The command that runs the end script with args, as the end task starts it (Windows PowerShell 5.1).</summary>
    public static (string File, string[] Args) EndCommand(string[] args) => ThroughScripts ? (Ps51, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", End, .. args]) : (Exe, ["power-mode", "end", .. args]);

    /// <summary>Runs the switch with exactly this environment (no seams added), raw output.</summary>
    public static Task<Res> SpawnCliAsync(string[] args, Dictionary<string, string> env)
    {
        (string file, string[] argv) = CliCommand(args);
        return SpawnAsync(file, argv, env);
    }

    /// <summary>Starts the switch without waiting for it (output drained in the background).</summary>
    public static Process StartCli(string[] args, Dictionary<string, string> env)
    {
        (string file, string[] argv) = CliCommand(args);
        return StartDrained(file, argv, env);
    }

    /// <summary>Starts a process without waiting for it: no input, both streams drained in the background.</summary>
    public static Process StartDrained(string file, IEnumerable<string> args, Dictionary<string, string> env)
    {
        Process p = StartProc(file, args, env);
        p.StandardInput.Close();
        _ = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        return p;
    }

    /// <summary>The end script under Windows PowerShell 5.1: what the end task runs. argv = null: `-Path flag` then args; else exactly argv after the script.</summary>
    public static async Task<Res> End51Async(Machine m, string[] args, Dictionary<string, string>? more = null, string[]? argv = null)
    {
        more ??= [];
        Dictionary<string, string> env = Merge(BaseEnv(), Env(("FAKE_PM_DIR", m.Sim), ("POWER_MODE_POWERCFG", Path.Combine(m.Sim, "fake-powercfg.ps1")), ("POWER_MODE_SCHTASKS", Path.Combine(m.Sim, "fake-schtasks.ps1"))));
        env = Merge(env, more);
        env.Remove("POWER_MODE_FILE");
        // As the Task Scheduler starts it: no PowerShell 7 module path, which keeps 5.1 from loading Get-Acl's module.
        env.Remove("PSModulePath");
        if (!more.ContainsKey("POWER_MODE_TZ")) env["POWER_MODE_TZ"] = PinTz;
        if (!more.ContainsKey("POWER_MODE_NVIDIASMI")) env.Remove("POWER_MODE_NVIDIASMI");
        if (!more.ContainsKey("POWER_MODE_ELEVATED")) env["POWER_MODE_ELEVATED"] = "1";
        env["POWER_MODE_TEST"] = "1";
        string[] tail = argv ?? ["-Path", m.Flag, .. args];
        (string file, string[] cmd) = EndCommand(tail);
        return Norm(await SpawnAsync(file, cmd, env).ConfigureAwait(false));
    }

    /// <summary>The library functions, dot-sourced in pwsh: what install registers, and how a registered task is recognised.</summary>
    public static async Task<string> LibAsync(Cleanup t, string code)
    {
        string dir = MkTemp(t, "power-mode-lib-");
        string file = Path.Combine(dir, "probe.ps1");
        await File.WriteAllTextAsync(file, $". '{Lib}'\n{code}\n").ConfigureAwait(false);
        Res r = await SpawnAsync("pwsh", ["-NoProfile", "-NonInteractive", "-File", file], Merge(BaseEnv(), Env(("POWER_MODE_TEST", "1")))).ConfigureAwait(false);
        Eq(0, r.Status, r.Out + r.Err);
        return r.Out.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }

    /// <summary>The configured flag library, dot-sourced with the configured path pointed into a temp world through a script-scope seam.</summary>
    public sealed class FlagLib
    {
        private readonly string _body;
        private readonly string _seam;
        private readonly Dictionary<string, string> _extraEnv;

        public FlagLib(Cleanup t, string body, bool seams, Dictionary<string, string>? extraEnv)
        {
            _body = body;
            _extraEnv = extraEnv ?? [];
            // Its own world: the ACL cases leave folders the test user cannot delete until their ACL is reset.
            string dir = Path.Combine(Tmp, "power-mode-cli-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6)));
            Directory.CreateDirectory(dir);
            t.After(() =>
            {
                Icacls(dir, "/reset", "/T", "/Q");
                RemoveTree(dir);
            });
            Dir = dir;
            Fresh = Path.Combine(dir, "pd", "BluettiWatcher", "power-mode.json");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetDirectoryName(Fresh))!); // %ProgramData% itself exists
            _seam = seams ? $"$script:PowerModeDefaultFlagPathTest = '{Fresh}'; " : "";
        }

        public string Dir { get; }
        public string Fresh { get; }

        public async Task<Res> Run(string? code = null)
        {
            Dictionary<string, string> env = Merge(Merge(BaseEnv(), Env(("POWER_MODE_TEST", "1"))), _extraEnv);
            Res r = await SpawnAsync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", $"$ErrorActionPreference = 'Stop'; . '{Lib}'; {_seam}{code ?? _body}"], env).ConfigureAwait(false);
            return Norm(r);
        }
    }

    public static FlagLib NewFlagLib(Cleanup t, string body, bool seams = true, Dictionary<string, string>? extraEnv = null) => new(t, body, seams, extraEnv);

    /// <summary>icacls, output discarded (the JS spawnSync).</summary>
    public static int Icacls(params string[] args)
    {
        using Process p = StartProc("icacls", args, null);
        p.StandardInput.Close();
        _ = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return p.ExitCode;
    }

    // ---- the mutex probe ---------------------------------------------------------------------------------------------------

    // Another process asks for the flag mutex with no wait: free, held (not released) or abandoned (the holding process died
    // holding it). Re-entry by one thread would hide a missing release, so this always crosses processes.
    public const string MutexProbe = """$m = [Threading.Mutex]::new($false, 'Local\BluettiWatcherPowerMode'); try { if ($m.WaitOne(0)) { 'free'; $m.ReleaseMutex() } else { 'held' } } catch [Threading.AbandonedMutexException] { 'abandoned'; $m.ReleaseMutex() }""";

    /// <summary>A test that probes the mutex runs the script under a mutex name of its own (POWER_MODE_MUTEX).</summary>
    public static string UniqueMutex() => $"Local\\BWPMTest-{Environment.ProcessId}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))}";

    public static async Task<string> ProbeMutex(string name) =>
        (await SpawnAsync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", MutexProbe.Replace("Local\\BluettiWatcherPowerMode", name, StringComparison.Ordinal)]).ConfigureAwait(false)).Out.Trim();

    public static readonly bool RunnerIsAdmin = ComputeRunnerIsAdmin();

    private static bool ComputeRunnerIsAdmin()
    {
        using Process p = StartProc("pwsh", ["-NoProfile", "-NonInteractive", "-Command", "([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)"], null);
        p.StandardInput.Close();
        Task<string> so = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return so.GetAwaiter().GetResult().Trim() == "True";
    }

    public const string Names = "$n = Get-PowerModeNames; $n.flag; $n.mutex; $n.taskPrefix; $n.endTask; $n.gpuCapTask; $n.gpuRestoreTask; Get-PowerModeFlagPath; [Environment]::GetFolderPath('CommonApplicationData')";

    /// <summary>An install with its own names sets the four variables: every name, path and command is then exactly that install's.</summary>
    public static Dictionary<string, string> Compat() => Env(("POWER_MODE_FILE", "C:\\ProgramData\\MyPowerMode\\power-mode.json"), ("POWER_MODE_MUTEX", "Local\\MyPowerModeFlag"),
        ("POWER_MODE_TASK_PREFIX", "MyEcoMode"), ("POWER_MODE_EXTRA_READERS", "mypmreader"));

    public static readonly string[] NameVarList = ["POWER_MODE_FILE", "POWER_MODE_MUTEX", "POWER_MODE_TASK_PREFIX", "POWER_MODE_EXTRA_READERS"];

    private static readonly System.Text.Json.JsonSerializerOptions RelaxedJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>JSON.stringify: no escaping of + and the like.</summary>
    public static string ToJson(JsonNode node) => node.ToJsonString(RelaxedJson);

    public static string ReadUtf16(string file) => File.ReadAllText(file, Encoding.Unicode);

    /// <summary>The task's command line after the end script's name, split as the Task Scheduler's command line is.</summary>
    public static string[] TaskArgv(string xml)
    {
        string args = Regex.Match(xml, "<Arguments>([^<]*)</Arguments>").Groups[1].Value.Replace("&quot;", "\"", StringComparison.Ordinal);
        const string marker = "power-mode end ";
        string rest = args[(args.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
        return Regex.Matches(rest, "\"([^\"]*)\"|(\\S+)").Select(x => x.Groups[1].Success ? x.Groups[1].Value : x.Groups[2].Value).ToArray();
    }

    /// <summary>A flag folder whose ACL the setup builds by hand, then Initialize-PowerModeFlagDir: 'written' or 'refused: why'.</summary>
    public static async Task<Res> AclCaseAsync(Cleanup t, string setup)
    {
        FlagLib l = NewFlagLib(t, AclPs + "$d = Split-Path (Get-PowerModeFlagPath); " + setup + "; try { Initialize-PowerModeFlagDir (Get-PowerModeFlagPath); 'written' } catch { \"refused: $($_.Exception.Message)\" }");
        Directory.CreateDirectory(Path.GetDirectoryName(l.Fresh)!);
        return await l.Run().ConfigureAwait(false);
    }

    // ---- ACL probe helper (the JS ACL_PS) ------------------------------------------------------------------------------------

    /// <summary>Builds the folder's ACL by hand: protected, the user's rights, plus extra (sid=rights) grants.</summary>
    public const string AclPs = "function Set-TestAcl([string] $Dir, [string] $OwnerRights, [string[]] $Extra) { " +
        "$s = New-Object Security.AccessControl.DirectorySecurity; $s.SetAccessRuleProtection($true, $false); " +
        "$f = [Security.AccessControl.InheritanceFlags] 'ContainerInherit, ObjectInherit'; " +
        "$add = { param($sid, $r) $s.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier $sid), [Security.AccessControl.FileSystemRights] $r, $f, 'None', 'Allow'))) }; " +
        "& $add 'S-1-5-18' 'FullControl'; & $add 'S-1-5-32-544' 'FullControl'; & $add ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value) $OwnerRights; " +
        "foreach ($e in $Extra) { if ($e) { $sid, $r = $e -split '=', 2; & $add $sid $r } }; Set-Acl -LiteralPath $Dir -AclObject $s }; ";

    // ---- assertions and formatting -------------------------------------------------------------------------------------------

    public static void Eq<T>(T expected, T actual, string? msg = null)
    {
        try { Assert.Equal(expected, actual); }
        catch (EqualException e) when (msg is not null) { throw new XunitException(msg + "\n" + e.Message, e); }
    }

    /// <summary>deepEqual on two lists of lines.</summary>
    public static void EqL(string[] expected, IEnumerable<string> actual, string? msg = null) => Eq(expected, actual.ToArray(), msg);

    public static string Cfg(string returnPlan) => $"{{\"returnPlan\":\"{returnPlan}\"}}";

    public const string Batt61 = """, "battery": { "soc": 61, "at": "2026-10-06T21:05:06+03:00", "level": "ok", "mains": false } }""" + "\n";

    public static void Ok(bool cond, string? msg = null) => Assert.True(cond, msg);

    public static void Match(string pattern, string text, RegexOptions opts = RegexOptions.None, string? msg = null) =>
        Assert.True(Regex.IsMatch(text, pattern, opts | RegexOptions.CultureInvariant), $"/{pattern}/ does not match: {text} {msg}");

    public static void NoMatch(string pattern, string text, RegexOptions opts = RegexOptions.None) =>
        Assert.False(Regex.IsMatch(text, pattern, opts | RegexOptions.CultureInvariant), $"/{pattern}/ matches: {text}");

    public static string[] Lines(string s) => s.Split('\n');

    public static bool Has(string s, string part) => s.Contains(part, StringComparison.Ordinal);

    /// <summary>A JSON value as a plain object: string, bool, double, or null for absent. Path = nested keys.</summary>
    public static object? V(this JsonNode? n, params string[] path)
    {
        foreach (string p in path) n = n is JsonObject o ? o[p] : null;
        if (n is null) return null;
        if (n is JsonValue v)
        {
            if (v.TryGetValue(out string? s)) return s;
            if (v.TryGetValue(out bool b)) return b;
            if (v.TryGetValue(out double d)) return d;
        }
        return n.ToJsonString();
    }

    public static string Fmt(object? v) => v switch
    {
        null => "undefined",
        bool b => b ? "true" : "false",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    /// <summary>{ a: 1, b: 2 } as "a=1 b=2": the JS deepEqual on a small object, with the keys named in a failure.</summary>
    public static string Obj(params (string K, object? V)[] kv) => string.Join(' ', kv.Select(x => x.K + "=" + Fmt(x.V)));

    public static string Pick(JsonNode j, params string[] keys) => Obj(keys.Select(k => (k, j.V(k))).ToArray());

    // ---- paths ---------------------------------------------------------------------------------------------------------------

    private delegate uint GetLongPathNameW([MarshalAs(UnmanagedType.LPWStr)] string shortPath, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder longPath, uint cch);

    private static string LongPath(string p)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
        if (!OperatingSystem.IsWindows()) return full;
        nint lib = NativeLibrary.Load("kernel32.dll");
        GetLongPathNameW f = Marshal.GetDelegateForFunctionPointer<GetLongPathNameW>(NativeLibrary.GetExport(lib, "GetLongPathNameW"));
        StringBuilder sb = new(32768);
        uint n = f(full, sb, 32768);
        return n == 0 || n >= 32768 ? full : sb.ToString();
    }

    private static string FindScriptDir()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "power-mode");
            if (File.Exists(Path.Combine(candidate, "power-mode.ps1"))) return candidate;
        }
        throw new DirectoryNotFoundException("power-mode/power-mode.ps1 not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>Tests that wait on a wall-clock window (a reader holding the flag open, a lock held for a set time, a tool
/// timeout, a process that must still be alive) run alone, after every parallel collection: one collection with parallelization off.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PowerModeSerialGroup
{
    public const string Name = "PowerModeSerial";
}
