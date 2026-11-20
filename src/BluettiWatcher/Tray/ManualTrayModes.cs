using BluettiWatcher.Decision;

namespace BluettiWatcher.Tray;

/// <summary>The tray's modes over the page's Auto / Eco / Max (<see cref="StationService.SetModeAsync"/>).</summary>
/// <remarks>Pattern: Adapter.</remarks>
internal sealed class ManualTrayModes(StationService service) : ITrayModes
{
    private static readonly TrayMode[] Choices = [new("eco", "Eco"), new("max", "Max")];

    public IReadOnlyList<TrayMode> Modes => Choices;

    public string? Current => service.State.Manual switch
    {
        ManualMode.Eco => "eco",
        ManualMode.Max => "max",
        _ => null,
    };

    public Task SelectAsync(string? modeId, CancellationToken ct) => service.SetModeAsync(modeId switch
    {
        null => ManualMode.Auto,
        "eco" => ManualMode.Eco,
        "max" => ManualMode.Max,
        _ => throw new ArgumentOutOfRangeException(nameof(modeId), modeId, "not a tray mode"),
    }, ct);
}
