namespace BluettiWatcher.Tray;

/// <summary>One fault, warning or over-temperature the station reports; <see cref="Code"/> tells one alarm from another.</summary>
internal sealed record StationAlarm(string Code, string Text, StationAlarmKind Kind);
