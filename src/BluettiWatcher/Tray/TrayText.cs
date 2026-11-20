using System.Globalization;
using BluettiWatcher.Io;

namespace BluettiWatcher.Tray;

/// <summary>The tray tooltip: charge and input, watts, mode, time left; a stale reading is named with its time.</summary>
internal static class TrayText
{
    /// <summary>The shell's tooltip holds 127 characters.</summary>
    public const int TooltipMax = 127;

    public static string Tooltip(TraySnapshot s, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(s);
        var lines = new List<string>(4);
        string charge = s.Soc is { } soc ? $"Battery {TrayIconRenderer.Text(soc)} %" : "Battery ?";
        if (s.Stale)
        {
            lines.Add(s.ReadAt is { } at ? $"{charge} · no reading since {FlagFileReader.Clock(at, zone)}" : "Bluetti Watcher · no reading yet");
        }
        else
        {
            lines.Add($"{charge} · {(s.Power == TrayPower.Mains ? "on mains" : "on battery")}");
            lines.Add($"In {Watts(s.InWatts)} · out {Watts(s.OutWatts)}");
        }
        lines.Add($"Mode: {s.Mode}");
        if (s.HoursLeft is { } h && double.IsFinite(h) && h >= 0) lines.Add($"Time left {Duration(h)}");
        string text = string.Join('\n', lines);
        return text.Length <= TooltipMax ? text : text[..TooltipMax];
    }

    private static string Watts(double? w) => w is { } v ? $"{Math.Round(v).ToString(CultureInfo.InvariantCulture)} W" : "? W";

    /// <summary>Hours as "3 h 20 min" (minutes rounded), "45 min" under an hour.</summary>
    public static string Duration(double hours)
    {
        long minutes = (long)Math.Round(hours * 60, MidpointRounding.AwayFromZero);
        return minutes < 60
            ? $"{minutes.ToString(CultureInfo.InvariantCulture)} min"
            : $"{(minutes / 60).ToString(CultureInfo.InvariantCulture)} h {(minutes % 60).ToString(CultureInfo.InvariantCulture)} min";
    }
}
