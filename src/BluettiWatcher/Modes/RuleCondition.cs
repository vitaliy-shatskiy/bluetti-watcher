namespace BluettiWatcher.Modes;

/// <summary>
/// An Auto rule's condition; only the fields of its <see cref="Kind"/> are read. <see cref="Hysteresis"/> keeps a rule
/// that is already in force matching a little past its threshold, so a value hovering at it does not flap the mode.
/// </summary>
internal sealed record RuleCondition
{
    public ConditionKind Kind { get; init; }
    /// <summary><see cref="ConditionKind.MainsOffFor"/>, <see cref="ConditionKind.MainsOnFor"/>: 0 to 86400.</summary>
    public int Seconds { get; init; }
    /// <summary><see cref="ConditionKind.SocBelow"/>: 0 to 100.</summary>
    public double Percent { get; init; }
    /// <summary><see cref="ConditionKind.CutWithin"/>: 0 to 1440.</summary>
    public int Minutes { get; init; }
    /// <summary><see cref="ConditionKind.TimeWindow"/>: local <c>HH:mm</c>.</summary>
    public string From { get; init => field = value ?? ""; } = "";
    /// <summary><see cref="ConditionKind.TimeWindow"/>: local <c>HH:mm</c>, exclusive.</summary>
    public string To { get; init => field = value ?? ""; } = "";
    /// <summary>
    /// Percent points (<see cref="ConditionKind.SocBelow"/>) or minutes (<see cref="ConditionKind.CutWithin"/>) a rule in
    /// force keeps matching past its threshold; the mains rules hold for the whole off (on) spell once in force.
    /// </summary>
    public double Hysteresis { get; init; }
}
