using System.Text.Json.Serialization;

namespace BluettiWatcher.Modes;

/// <summary>What an Auto rule watches.</summary>
internal enum ConditionKind
{
    /// <summary>Mains confirmed off for at least <see cref="RuleCondition.Seconds"/>.</summary>
    [JsonStringEnumMemberName("mainsOffFor")] MainsOffFor,
    /// <summary>Mains confirmed on for at least <see cref="RuleCondition.Seconds"/>.</summary>
    [JsonStringEnumMemberName("mainsOnFor")] MainsOnFor,
    /// <summary>The battery under <see cref="RuleCondition.Percent"/>.</summary>
    [JsonStringEnumMemberName("socBelow")] SocBelow,
    /// <summary>A scheduled outage (DTEK) starts within <see cref="RuleCondition.Minutes"/>.</summary>
    [JsonStringEnumMemberName("cutWithin")] CutWithin,
    /// <summary>The local time is within <see cref="RuleCondition.From"/> to <see cref="RuleCondition.To"/> (may wrap midnight).</summary>
    [JsonStringEnumMemberName("timeWindow")] TimeWindow,
}
