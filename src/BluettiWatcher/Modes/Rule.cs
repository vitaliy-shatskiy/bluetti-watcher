namespace BluettiWatcher.Modes;

/// <summary>An Auto rule: when <see cref="Condition"/> holds, <see cref="ModeId"/> is chosen. The first matching rule wins.</summary>
internal sealed record Rule
{
    public required RuleCondition Condition { get; init; }
    public required string ModeId { get; init; }
}
