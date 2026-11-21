namespace BluettiWatcher.Tray;

/// <summary>
/// Which notification groups show; every group is on until switched off. <see cref="Enabled"/> off silences them all and
/// keeps each group's own choice for when it is on again.
/// </summary>
internal sealed record NotificationSettings(bool Mains = true, bool ModeChanges = true, bool LowBattery = true,
    bool Failures = true, bool Faults = true, bool OverTemperature = true, bool Enabled = true)
{
    /// <summary>The JSON name of the switch over every group.</summary>
    public const string EnabledKey = "enabled";

    /// <summary>A notice of <paramref name="kind"/> shows: notifications on and its group on.</summary>
    public bool Shows(NotificationKind kind) => Enabled && IsOn(kind);

    /// <summary>The JSON names, in the order the file and the API write them.</summary>
    public static readonly IReadOnlyList<(string Name, NotificationKind Kind)> Keys =
    [
        ("mains", NotificationKind.Mains),
        ("modeChanges", NotificationKind.ModeChanges),
        ("lowBattery", NotificationKind.LowBattery),
        ("failures", NotificationKind.Failures),
        ("faults", NotificationKind.Faults),
        ("overTemperature", NotificationKind.OverTemperature),
    ];

    public bool IsOn(NotificationKind kind) => kind switch
    {
        NotificationKind.Mains => Mains,
        NotificationKind.ModeChanges => ModeChanges,
        NotificationKind.LowBattery => LowBattery,
        NotificationKind.Failures => Failures,
        NotificationKind.Faults => Faults,
        NotificationKind.OverTemperature => OverTemperature,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public NotificationSettings With(NotificationKind kind, bool on) => kind switch
    {
        NotificationKind.Mains => this with { Mains = on },
        NotificationKind.ModeChanges => this with { ModeChanges = on },
        NotificationKind.LowBattery => this with { LowBattery = on },
        NotificationKind.Failures => this with { Failures = on },
        NotificationKind.Faults => this with { Faults = on },
        NotificationKind.OverTemperature => this with { OverTemperature = on },
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}
