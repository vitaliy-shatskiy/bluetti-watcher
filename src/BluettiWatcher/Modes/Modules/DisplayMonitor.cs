namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// One active monitor: <paramref name="Id"/> is its device path (stable across reboots and display re-ordering, unlike
/// the <c>\\.\DISPLAYn</c> name), the current mode and the refresh rates it offers at the current resolution.
/// </summary>
internal sealed record DisplayMonitor(string Id, string Name, int Width, int Height, int Hz, IReadOnlyList<int> SupportedHz);
