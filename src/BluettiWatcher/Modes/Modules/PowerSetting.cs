namespace BluettiWatcher.Modes.Modules;

/// <summary>One power setting: its subgroup and setting GUIDs (winnt.h), and the name its resource keys use.</summary>
internal sealed record PowerSetting(string Name, Guid Subgroup, Guid Setting)
{
    private static readonly Guid Processor = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid Video = new("7516b95f-f776-4464-8c53-06167f40cc99");

    /// <summary>PROCTHROTTLEMAX: maximum processor state, percent.</summary>
    public static readonly PowerSetting MaxProcessorState = new("maxProcessorState", Processor, new("bc5038f7-23e0-4960-96da-33abaf5935ec"));

    /// <summary>PERFBOOSTMODE: processor performance boost mode, 0..6.</summary>
    public static readonly PowerSetting BoostMode = new("boostMode", Processor, new("be337238-0d82-4146-a960-4f3749d470c7"));

    /// <summary>VIDEOIDLE: turn the display off after, seconds (0 = never).</summary>
    public static readonly PowerSetting DisplayOff = new("offTimeout", Video, new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e"));
}
