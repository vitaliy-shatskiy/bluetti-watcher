namespace BluettiWatcher.Tray;

/// <summary>
/// The modes the tray menu lists and switches: a port, so the menu does not depend on which engine runs the modes
/// (<see cref="ManualTrayModes"/>, <see cref="ModesTrayModes"/>).
/// </summary>
internal interface ITrayModes
{
    /// <summary>The modes besides Auto, in menu order.</summary>
    IReadOnlyList<TrayMode> Modes { get; }

    /// <summary>The chosen mode's id; null = Auto.</summary>
    string? Current { get; }

    /// <summary>Chooses a mode (null = Auto). A refusal is the engine's own event; nothing is thrown for it.</summary>
    Task SelectAsync(string? modeId, CancellationToken ct);
}
