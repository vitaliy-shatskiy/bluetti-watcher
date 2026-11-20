namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// One named command from config.json (<c>modes.commands.{name}</c>): an executable, its argument list (no shell) and its
/// time limit in seconds. Only the config file defines these; mode settings refer to them by name.
/// </summary>
internal sealed record CommandSpec(string Exe, IReadOnlyList<string> Args, int TimeoutSec);
