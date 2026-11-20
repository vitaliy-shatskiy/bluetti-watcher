namespace BluettiWatcher.Decision;

/// <summary>The station's active warning and fault codes, whether it is over its temperature limit, and the hottest reading (°C; null = not reported).</summary>
internal sealed record StationAlarmState(IReadOnlyList<string> Warnings, IReadOnlyList<string> Faults, bool Hot, double? TempMax);
