namespace BluettiWatcher.Tray;

/// <summary>One fault, warning or over-temperature the station reports; <see cref="Code"/> tells one alarm from another.</summary>
internal sealed record StationAlarm(string Code, string Text, StationAlarmKind Kind)
{
    /// <summary>The whole active list of one reading, for <see cref="TrayController.ReportAlarms"/>.</summary>
    public static IReadOnlyList<StationAlarm> ListOf(Decision.StationAlarmState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var list = new List<StationAlarm>();
        list.AddRange(state.Faults.Select(c => new StationAlarm("fault:" + c, $"Station fault {c}.", StationAlarmKind.Fault)));
        list.AddRange(state.Warnings.Select(c => new StationAlarm("warning:" + c, $"Station warning {c}.", StationAlarmKind.Warning)));
        if (state.Hot)
        {
            string t = state.TempMax is { } v ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $" ({v:0} °C)") : "";
            list.Add(new StationAlarm("temperature", $"The station is above its temperature limit{t}.", StationAlarmKind.OverTemperature));
        }
        return list;
    }
}
