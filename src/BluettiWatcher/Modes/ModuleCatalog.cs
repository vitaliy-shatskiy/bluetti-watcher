using BluettiWatcher.Io;
using BluettiWatcher.Modes.Broker;
using BluettiWatcher.Modes.Modules;

namespace BluettiWatcher.Modes;

/// <summary>
/// The action modules the service registers, in catalogue order. Each adapter adds itself here (explicit list, no
/// reflection: NativeAOT); the registry test runs over this list, so a module whose defaults fail its own schema fails it.
/// </summary>
internal static class ModuleCatalog
{
    /// <summary>The <c>actions</c> keys that become named commands under v2: the built-in Eco runs them on enter and leave.</summary>
    public static readonly string[] MigratedActions = [StateActions.Eco, StateActions.Normal];

    /// <summary>
    /// Every module on its Windows adapter. Building them touches nothing on the machine: the adapters read only when the
    /// reconciler or the catalogue asks. <paramref name="addEvent"/> writes an event row, <paramref name="log"/> a log line.
    /// </summary>
    public static IReadOnlyList<IActionModule> Production(StationConfig config, IProcessRunner? runner = null, TimeProvider? time = null,
        Action<DateTimeOffset, string, string>? addEvent = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        runner ??= new ProcessRunner();
        time ??= TimeProvider.System;
        addEvent ??= (_, _, _) => { };
        log ??= _ => { };
        var paths = BrokerPaths.Default;
        var broker = new BrokerClient(paths, runner, time);
        return
        [
            .. PowerModules.All(new PowerSchemes(), new NvidiaSmi(runner), broker),
            .. ServicesAppsModules.All(broker, runner, Commands(config), time, log),
            .. DisplayGpuModules.All(broker, runner, InspectorPath(paths), paths.Profiles, time, addEvent, log),
        ];
    }

    /// <summary>Profile Inspector as the broker's policy names it (the user may read the policy); null when it names none or cannot be read.</summary>
    private static string? InspectorPath(BrokerPaths paths)
    {
        try
        {
            return File.Exists(paths.Policy) && BrokerPolicy.Parse(File.ReadAllText(paths.Policy), out _) is { ProfileInspector.Length: > 0 } p ? p.ProfileInspector : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;   // the profile module then reports itself unavailable, naming the missing inspector
        }
    }

    /// <summary>
    /// <c>modes.commands</c>, plus the config's <c>actions.eco</c> / <c>actions.normal</c> lines as the commands
    /// <c>eco</c> / <c>normal</c> unless <c>modes.commands</c> already names them. A line that cannot be split is left out.
    /// </summary>
    public static IReadOnlyDictionary<string, CommandSpec> Commands(StationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var commands = new Dictionary<string, CommandSpec>(StringComparer.Ordinal);
        foreach (var (name, c) in config.Modes.Commands)
            if (!string.IsNullOrWhiteSpace(c.Exe)) commands[name] = new CommandSpec(c.Exe, [.. c.Args], c.TimeoutSec);
        foreach (string key in MigratedActions)
        {
            if (commands.ContainsKey(key) || config.Actions?.GetValueOrDefault(key) is not { } line || string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var (exe, args) = CommandLineArgs.Split(line);
                commands[key] = new CommandSpec(exe, args, config.ActionTimeoutSec);
            }
            catch (ArgumentException) { /* an unclosed quote: the line never ran under legacy either; Validate names the missing command */ }
        }
        return commands;
    }
}
