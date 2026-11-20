using BluettiWatcher.Modes;

namespace BluettiWatcher.Tray;

/// <summary>The tray's modes over the v2 engine: every mode in the settings' order, and Auto (the rules).</summary>
/// <remarks>Pattern: Adapter.</remarks>
internal sealed class ModesTrayModes(ModesEngine engine) : ITrayModes
{
    public IReadOnlyList<TrayMode> Modes => [.. engine.Settings.Modes.Select(m => new TrayMode(m.Id, m.Name))];

    public string? Current => engine.Settings.Selection is { Kind: SelectionKind.Manual, ManualModeId: { } id } ? id : null;

    /// <summary>A refusal (a mode deleted meanwhile) is logged by the engine's API path; the tray just keeps the old choice.</summary>
    public Task SelectAsync(string? modeId, CancellationToken ct) => engine.SelectAsync(modeId ?? ModesValidation.AutoChoice, ct);
}
