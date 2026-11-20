namespace BluettiWatcher.Modes;

/// <summary>
/// The selector's saved state: Auto or a held manual mode, and Auto's last pick (kept while no rule matches, and the rule
/// in force for its hysteresis).
/// </summary>
internal sealed record Selection
{
    public SelectionKind Kind { get; init; }
    /// <summary>The held mode when <see cref="Kind"/> is <see cref="SelectionKind.Manual"/>.</summary>
    public string? ManualModeId { get; init; }
    /// <summary>The mode Auto chose last; null before any rule matched.</summary>
    public string? AutoModeId { get; init; }
    /// <summary>The index of the rule in force (its hysteresis applies); null when none.</summary>
    public int? AutoRule { get; init; }

    public static readonly Selection Auto = new();
}
