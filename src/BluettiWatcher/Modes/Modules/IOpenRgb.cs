namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// OpenRGB as <c>rgb.lighting</c> needs it. Only <see cref="TurnOffAsync"/> and <see cref="LoadProfileAsync"/> may start
/// OpenRGB's SDK server: its device detection writes to some controllers (a Gigabyte RGB Fusion 2 board resets its
/// lighting), so nothing that runs while the action is off or the page reads the catalogue may start it.
/// </summary>
internal interface IOpenRgb
{
    /// <summary>OpenRGB.exe as found on disk (the configured path, else the install folders); null = not installed. Touches no device.</summary>
    string? ExePath { get; }

    /// <summary>Profile names saved in OpenRGB's configuration folder; null when the folder cannot be read. Touches no device.</summary>
    IReadOnlyList<string>? Profiles();

    /// <summary>Device names of a server that already runs; null when none answers. Never starts one.</summary>
    Task<IReadOnlyList<string>?> RunningDevicesAsync(CancellationToken ct);

    /// <summary>Starts the server when none runs, turns <paramref name="devices"/> (empty = all) off and reads them back; null = done, else the failure.</summary>
    Task<string?> TurnOffAsync(IReadOnlyList<string> devices, CancellationToken ct);

    /// <summary>Starts the server when none runs and loads the saved profile; null = done, else the failure.</summary>
    Task<string?> LoadProfileAsync(string profile, CancellationToken ct);
}
