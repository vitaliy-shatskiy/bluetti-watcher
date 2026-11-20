using System.Globalization;
using BluettiWatcher.PowerMode;

namespace BluettiWatcher.Modes;

/// <summary>
/// The flag word through the switch's own flag code (<see cref="PowerModeSwitch.WriteModeWord"/>): same lock, same atomic
/// write, same format. On a thread of its own (the lock is a mutex owned by its thread), bounded by the power-mode timeout.
/// A dry run logs instead of writing.
/// </summary>
internal sealed class SwitchFlagWordWriter(StationConfig config, Action<string> log) : IFlagWordWriter
{
    public async Task<string> WriteAsync(FlagWord word, CancellationToken ct)
    {
        string text = Word(word);
        if (config.DryRun)
        {
            log($"dry run, would set the power-mode flag to {text}");
            return "";
        }
        var vars = PowerModeSwitch.ProcessEnvironment();
        vars.AddRange(config.PowerModeEnvironment);
        var work = Task.Factory.StartNew(() =>
        {
            using var output = new StringWriter(CultureInfo.InvariantCulture);
            using var sw = new PowerModeSwitch(vars, output);
            return sw.WriteModeWord(config.FlagFile, text, "station") == 0 ? "" : output.ToString().Trim();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try { return await work.WaitAsync(TimeSpan.FromSeconds(config.PowerModeTimeoutSec), ct); }
        catch (TimeoutException) { return "no answer in time (the write may still land)"; }
    }

    public static string Word(FlagWord word) => word switch
    {
        FlagWord.Eco => "eco",
        FlagWord.Emergency => "emergency",
        _ => "normal",
    };
}
