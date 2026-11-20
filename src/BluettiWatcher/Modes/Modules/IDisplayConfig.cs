namespace BluettiWatcher.Modes.Modules;

/// <summary>The monitors' modes and the refresh-rate write: the only way the refresh-rate module reaches the display.</summary>
internal interface IDisplayConfig
{
    /// <summary>The active monitors; null when the display configuration cannot be read.</summary>
    IReadOnlyList<DisplayMonitor>? Monitors();

    /// <summary>Sets <paramref name="hz"/> at the monitor's current resolution, saved for the user; null = done, else the error.</summary>
    string? SetRefreshRate(string monitorId, int hz);
}
