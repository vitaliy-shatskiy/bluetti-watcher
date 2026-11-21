using BluettiWatcher.Modes.Broker;

namespace BluettiWatcher.Modes.Modules;

/// <summary>The display and GPU modules on their Windows adapters, for the registry to add in one call.</summary>
/// <remarks>Pattern: Factory.</remarks>
internal static class DisplayGpuModules
{
    /// <param name="broker">Runs the NVIDIA profile import and export.</param>
    /// <param name="inspectorPath">NVIDIA Profile Inspector's exe; null or missing = the profile module is unavailable.</param>
    /// <param name="policyFolder">The broker's policy folder the <c>.nip</c> files are picked from.</param>
    public static IReadOnlyList<IActionModule> All(IAdminBroker broker, string? inspectorPath, string policyFolder) =>
    [
        new RefreshRateModule(new WindowsDisplayConfig()),
        new BrightnessModule(new WindowsDdcMonitors()),
        new NvidiaProfileModule(new NvidiaProfileFiles(inspectorPath, policyFolder), broker),
    ];
}
