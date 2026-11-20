namespace BluettiWatcher.Modes;

/// <summary>
/// Config entry <c>modes.commands.{name}</c>: an executable, its argument list (no shell) and its time limit. Only
/// config.json defines commands; a mode's <c>command.pair</c> action refers to them by name.
/// </summary>
internal sealed class ModeCommandConfig
{
    public string Exe { get; set; } = "";
    public List<string> Args { get; set => field = value ?? []; } = [];
    /// <summary>Seconds, kept within 1 to 600 when run.</summary>
    public int TimeoutSec { get; set; } = 30;
}
