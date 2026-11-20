using BluettiWatcher.Io;

namespace BluettiWatcher.Tray;

/// <summary>The tray tooltip, one fact per line: charge, input, watts, mode, time left; a stale reading is named with its time.</summary>
internal static class TrayText
{
    /// <summary>The shell's tooltip holds 127 characters.</summary>
    public const int TooltipMax = 127;

    public static string Tooltip(TraySnapshot s, TimeZoneInfo zone, TrayWords w)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(w);
        var lines = new List<string>(6);
        if (s.Stale)
        {
            if (s.ReadAt is { } at)
            {
                lines.Add(w.Battery(s.Soc));
                lines.Add(w.NoReadingSince(FlagFileReader.Clock(at, zone)));
            }
            else lines.Add(w.NoReadingYet);
        }
        else
        {
            lines.Add(w.Battery(s.Soc));
            lines.Add(s.Power == TrayPower.Mains ? w.OnMains : w.OnBattery);
            lines.Add(w.In(s.InWatts));
            lines.Add(w.Out(s.OutWatts));
        }
        lines.Add(w.ModeLine(s.Mode, s.Choice));
        if (s.HoursLeft is { } h && double.IsFinite(h) && h >= 0) lines.Add(w.TimeLeft(h));
        string text = string.Join('\n', lines);
        return text.Length <= TooltipMax ? text : text[..TooltipMax];
    }
}
