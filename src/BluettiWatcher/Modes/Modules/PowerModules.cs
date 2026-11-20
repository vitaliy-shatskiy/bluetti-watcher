using BluettiWatcher.Modes.Broker;

namespace BluettiWatcher.Modes.Modules;

/// <summary>The power modules (plan, processor policy, display timeout, GPU limit), for the registry in one line.</summary>
internal static class PowerModules
{
    public static IReadOnlyList<IActionModule> All(IPowerSchemes schemes, INvidiaSmi smi, IAdminBroker broker) =>
    [
        new PowerPlanModule(schemes),
        new CpuPolicyModule(schemes),
        new DisplayOffTimeoutModule(schemes),
        new GpuPowerLimitModule(smi, broker),
    ];
}
