using BluettiWatcher.Io;
using BluettiWatcher.Modes.Broker;

namespace BluettiWatcher.Modes.Modules;

/// <summary>The services-and-apps group of action modules, built over the real Windows ports for the registry to register in one line.</summary>
internal static class ServicesAppsModules
{
    /// <summary>
    /// <paramref name="log"/> receives each command run of <c>command.pair</c>; <paramref name="runner"/> starts its commands
    /// and <c>schtasks.exe</c>.
    /// </summary>
    public static IReadOnlyList<IActionModule> All(IAdminBroker broker, IProcessRunner runner, TimeProvider time, Action<string> log)
    {
        var processes = new WindowsProcessControl();
        return
        [
            new ServiceStateModule(new WindowsServiceControl(), broker, time),
            new ProcessCloseModule(processes, time),
            new CommandPairModule(runner, log),
            new PhoneLinkModule(processes, time),
            new ScheduledTaskModule(new SchtasksTaskScheduler(runner), broker),
        ];
    }
}
