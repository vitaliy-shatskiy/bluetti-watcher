namespace BluettiWatcher.Decision;

/// <summary>
/// The grid input voltage alert: fires once when a reading goes above <see cref="HighAt"/> (230 V + 10 %), and re-arms only
/// under <see cref="RearmBelow"/>, so a voltage hovering at the line is one alert. A reading with no grid voltage changes nothing.
/// </summary>
internal static class GridVoltageAlarm
{
    public const double HighAt = 253.0;
    public const double RearmBelow = 250.0;

    /// <summary>The next "high" state from the last one and this reading's voltage; Fire = the alert goes out now.</summary>
    public static (bool High, bool Fire) Step(bool high, double? volt) => volt switch
    {
        > HighAt when !high => (true, true),
        < RearmBelow when high => (false, false),
        _ => (high, false),
    };
}
