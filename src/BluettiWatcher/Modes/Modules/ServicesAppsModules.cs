using BluettiWatcher.Io;
using BluettiWatcher.Modes.Broker;

namespace BluettiWatcher.Modes.Modules;

/// <summary>The services-and-apps group of action modules, built over the real Windows ports for the registry to register in one line.</summary>
internal static class ServicesAppsModules
{
    /// <summary>
    /// <paramref name="commands"/> are config.json's named commands (<c>modes.commands</c>), the only ones
    /// <c>command.pair</c> can run; <paramref name="log"/> receives each of its runs; <paramref name="runner"/> starts them
    /// and <c>schtasks.exe</c>.
    /// </summary>
    public static IReadOnlyList<IActionModule> All(IAdminBroker broker, IProcessRunner runner, IReadOnlyDictionary<string, CommandSpec> commands,
        TimeProvider time, Action<string> log)
    {
        var processes = new WindowsProcessControl();
        return
        [
            new ServiceStateModule(new WindowsServiceReader(), broker, time),
            new ProcessCloseModule(processes, time),
            new CommandPairModule(commands, runner, log),
            new PhoneLinkModule(processes, time),
            new ScheduledTaskModule(new SchtasksTaskScheduler(runner), broker),
        ];
    }
}
