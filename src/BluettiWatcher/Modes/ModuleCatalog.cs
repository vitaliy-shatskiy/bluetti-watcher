namespace BluettiWatcher.Modes;

/// <summary>
/// The action modules the service registers, in catalogue order. Each adapter adds itself here (explicit list, no
/// reflection: NativeAOT); the registry test runs over this list, so a module whose defaults fail its own schema fails it.
/// </summary>
internal static class ModuleCatalog
{
    public static IReadOnlyList<IActionModule> Production(StationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return [];
    }
}
