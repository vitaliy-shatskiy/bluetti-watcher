namespace BluettiWatcher.Modes;

/// <summary>A named set of actions; exactly one mode is active at a time.</summary>
internal sealed record Mode
{
    /// <summary>Lower-case letters, digits and <c>-</c>, 1 to 32 characters; the built-ins are <c>normal</c>, <c>eco</c>, <c>max</c>.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>What the power-mode flag says while this mode is active.</summary>
    public FlagWord FlagWord { get; init; }
    /// <summary>A built-in mode is editable but not deletable.</summary>
    public bool BuiltIn { get; init; }
    public IReadOnlyList<ModeAction> Actions { get; init => field = value ?? []; } = [];
}
