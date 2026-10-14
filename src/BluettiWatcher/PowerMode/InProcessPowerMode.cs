using System.Globalization;
using BluettiWatcher.Io;

namespace BluettiWatcher.PowerMode;

/// <summary>
/// Runs a power-mode command (the arguments after <c>power-mode</c>) and answers like a finished child process; the
/// environment given is added to (or overrides) this process's environment for the command.
/// </summary>
internal interface IPowerModeRunner
{
    Task<ProcessResult> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, IReadOnlyDictionary<string, string>? env = null);
}

/// <summary>
/// The switch in this process: no PowerShell, no child process. It runs on a thread of its own (the flag's mutex belongs
/// to the thread that took it); past <c>timeout</c> the answer is "timed out" and the command finishes on its own.
/// </summary>
internal sealed class InProcessPowerMode : IPowerModeRunner
{
    public async Task<ProcessResult> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, IReadOnlyDictionary<string, string>? env = null)
    {
        if (!OperatingSystem.IsWindows()) return new ProcessResult(2, ["power mode: COULD NOT RUN: Windows only"], false);
        var vars = PowerModeSwitch.ProcessEnvironment();
        if (env is not null) vars.AddRange(env);   // later entries win
        var work = Task.Factory.StartNew(() =>
        {
            if (!OperatingSystem.IsWindows()) return (2, "");
            using var text = new StringWriter(CultureInfo.InvariantCulture);
            using var powerMode = new PowerModeSwitch(vars, text);
            int code = powerMode.Run(args);
            return (code, text.ToString());
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            var (code, output) = await work.WaitAsync(timeout, ct);
            return new ProcessResult(code, [.. output.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0)], false);
        }
        catch (TimeoutException)
        {
            // Not a failure: the command is not stopped (a thread is never aborted; it may hold the flag's mutex and a write)
            // and goes on to its end, so it can still write the flag after this answer. The caller says "still running".
            return new ProcessResult(-1, [], true);
        }
    }
}
