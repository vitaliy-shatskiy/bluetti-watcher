namespace BluettiWatcher.Modes.Modules;

/// <summary>DDC/CI brightness per physical monitor: the only way the brightness module reaches a monitor.</summary>
internal interface IDdcMonitors
{
    /// <summary>The physical monitors with whether each answers DDC/CI; null when the monitors cannot be listed.</summary>
    Task<IReadOnlyList<DdcMonitor>?> MonitorsAsync(CancellationToken ct);

    Task<DdcBrightness> ReadBrightnessAsync(string monitorId, CancellationToken ct);

    /// <summary>Writes the raw VCP 0x10 value; null = sent, else the error. The monitor may take a moment to show it.</summary>
    Task<string?> WriteBrightnessAsync(string monitorId, int raw, CancellationToken ct);
}
