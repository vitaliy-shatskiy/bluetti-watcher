using System.Diagnostics;
using System.Globalization;
using BluettiWatcher.Decision;
using BluettiWatcher.PowerMode;

namespace BluettiWatcher.Io;

internal sealed record ActionResult(bool Ok, int ExitCode, string CommandLine, string LastLine)
{
    public bool DryRun => ExitCode == DryRunExit;
    public const int DryRunExit = -2;
}

/// <summary>Every change on this PC goes through the power-mode switch (<c>BluettiWatcher.exe power-mode</c>); nothing else writes the flag.</summary>
internal interface IPowerModeActions
{
    Task<ActionResult> StationEcoAsync(CancellationToken ct);
    Task<ActionResult> StationEmergencyAsync(CancellationToken ct);
    Task<ActionResult> LiftAsync(CancellationToken ct);
    Task<ActionResult> BatteryAsync(BatteryReport battery, double hibernateAt, CancellationToken ct);
}

/// <summary>
/// The command lines, in one place so they can follow power-mode.ps1:
/// station eco = <c>eco -Source station</c> (no end time; lasts until off) · station emergency = <c>on -Source station</c> ·
/// lift (mains back, and the page's Max) = <c>off -Source station</c> (every mode lifted, Balanced + Best performance paid
/// back) · battery = <c>battery -Soc -Level -Mains -HibernateAt</c>.
/// Each runs in this process (<c>BluettiWatcher.exe power-mode ... -Path &lt;flag&gt;</c>, <paramref name="inProcess"/>), only
/// at a transition, with POWER_MODE_FILE, POWER_MODE_MUTEX, POWER_MODE_TASK_PREFIX and POWER_MODE_EXTRA_READERS in its
/// environment. A configured script (<c>powerMode.script</c>, <c>--power-mode</c>) or no in-process runner = the script
/// through <c>pwsh -NoProfile -File &lt;script&gt;</c>, windowless, as before.
/// </summary>
internal sealed class PowerModeAdapter(StationConfig config, IProcessRunner runner, Action<string> log, IPowerModeRunner? inProcess = null) : IPowerModeActions
{
    public static IReadOnlyList<string> EcoArgs { get; } = ["eco", "-Source", "station"];
    public static IReadOnlyList<string> EmergencyArgs { get; } = ["on", "-Source", "station"];
    public static IReadOnlyList<string> LiftArgs { get; } = ["off", "-Source", "station"];

    public static IReadOnlyList<string> BatteryArgs(BatteryReport b, double hibernateAt)
    {
        var inv = CultureInfo.InvariantCulture;
        return ["battery", "-Soc", b.Soc.ToString("0.##", inv), "-Level", b.Level.ToString().ToLowerInvariant(),
            "-Mains", b.Mains ? "on" : "off", "-HibernateAt", hibernateAt.ToString("0.##", inv)];
    }

    /// <summary>The full argument list for pwsh.</summary>
    public IReadOnlyList<string> PowerModeCommand(IReadOnlyList<string> args) =>
        ["-NoProfile", "-NonInteractive", "-File", config.PowerModeScript, .. args, "-Path", config.FlagFile];

    /// <summary>The arguments of <c>BluettiWatcher.exe power-mode</c>, the in-process switch.</summary>
    public IReadOnlyList<string> SwitchCommand(IReadOnlyList<string> args) => [.. args, "-Path", config.FlagFile];

    /// <summary>In this process unless a script is configured (or no in-process runner was given).</summary>
    public bool InProcess => inProcess is not null && string.IsNullOrEmpty(config.PowerMode.Script);

    public Task<ActionResult> StationEcoAsync(CancellationToken ct) => RunAsync(EcoArgs, ct);
    public Task<ActionResult> StationEmergencyAsync(CancellationToken ct) => RunAsync(EmergencyArgs, ct);
    public Task<ActionResult> LiftAsync(CancellationToken ct) => RunAsync(LiftArgs, ct);
    public Task<ActionResult> BatteryAsync(BatteryReport battery, double hibernateAt, CancellationToken ct) =>
        RunAsync(BatteryArgs(battery, hibernateAt), ct);

    private async Task<ActionResult> RunAsync(IReadOnlyList<string> switchArgs, CancellationToken ct)
    {
        var args = InProcess ? SwitchCommand(switchArgs) : PowerModeCommand(switchArgs);
        string line = (InProcess ? "BluettiWatcher.exe power-mode" : config.PwshPath) + " " + string.Join(' ', args.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a));
        if (config.DryRun)
        {
            log($"dry run, would run: {line}");
            return new ActionResult(false, ActionResult.DryRunExit, line, "dry run");
        }
        long started = Stopwatch.GetTimestamp();
        var timeout = TimeSpan.FromSeconds(config.PowerModeTimeoutSec);
        var r = InProcess ? await inProcess!.RunAsync(args, timeout, ct, config.PowerModeEnvironment)
            : await runner.RunAsync(config.PwshPath, args, timeout, ct, config.PowerModeEnvironment);
        string last = r.Lines.Count > 0 ? r.Lines[^1] : r.TimedOut ? "timed out" : "";
        bool ok = r.ExitCode == 0 && !r.TimedOut;
        // The log line is short (the mode, the exit, the time); a failure adds the last output line, and its event keeps the whole command.
        string ms = ((long)Stopwatch.GetElapsedTime(started).TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        // The verb is switchArgs[0] on both paths (the pwsh arguments put it after -File <script>, the exe's right at the start).
        // An in-process command that timed out is not over: it finishes on its own and may still write the flag.
        if (r.TimedOut && InProcess) log($"power-mode {switchArgs[0]} -> no answer in {ms} ms, still running (it may yet write the flag)");
        else log($"power-mode {switchArgs[0]} -> exit {r.ExitCode} in {ms} ms{(ok ? "" : ": " + last)}");
        return new ActionResult(ok, r.ExitCode, line, last);
    }
}

internal interface IHibernator
{
    /// <summary>true = the hibernation command succeeded.</summary>
    Task<bool> HibernateAsync(CancellationToken ct);
}

/// <summary><c>shutdown /h</c>, windowless.</summary>
internal sealed class ShutdownHibernator(IProcessRunner runner, Action<string> log) : IHibernator
{
    public async Task<bool> HibernateAsync(CancellationToken ct)
    {
        var r = await runner.RunAsync("shutdown.exe", ["/h"], TimeSpan.FromSeconds(30), ct);
        log($"shutdown /h -> exit {r.ExitCode}");
        return r.ExitCode == 0 && !r.TimedOut;
    }
}

/// <summary>Dry run: logs what it would do and never hibernates.</summary>
internal sealed class DryRunHibernator(Action<string> log) : IHibernator
{
    public Task<bool> HibernateAsync(CancellationToken ct)
    {
        log("dry run, would run: shutdown.exe /h");
        return Task.FromResult(false);
    }
}
