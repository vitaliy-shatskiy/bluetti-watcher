namespace BluettiWatcher.Tray;

/// <summary>What a station alarm is: it picks the notification group and icon.</summary>
internal enum StationAlarmKind
{
    Fault,
    Warning,
    OverTemperature,
}
