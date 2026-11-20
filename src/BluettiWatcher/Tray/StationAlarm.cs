using System.Globalization;

namespace BluettiWatcher.Tray;

/// <summary>
/// One fault, warning or over-temperature the station reports; <see cref="Code"/> tells one alarm from another.
/// <see cref="Value"/> is the station's fault or warning code, or the temperature in °C ("" when none came); the words are
/// <see cref="TrayNotices.FromAlarm"/>'s.
/// </summary>
internal sealed record StationAlarm(string Code, string Value, StationAlarmKind Kind)
{
    /// <summary>The whole active list of one reading, for <see cref="TrayController.ReportAlarms"/>.</summary>
    public static IReadOnlyList<StationAlarm> ListOf(Decision.StationAlarmState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var list = new List<StationAlarm>();
        list.AddRange(state.Faults.Select(c => new StationAlarm("fault:" + c, c, StationAlarmKind.Fault)));
        list.AddRange(state.Warnings.Select(c => new StationAlarm("warning:" + c, c, StationAlarmKind.Warning)));
        if (state.Hot)
            list.Add(new StationAlarm("temperature", state.TempMax is { } v ? v.ToString("0", CultureInfo.InvariantCulture) : "", StationAlarmKind.OverTemperature));
        return list;
    }
}
