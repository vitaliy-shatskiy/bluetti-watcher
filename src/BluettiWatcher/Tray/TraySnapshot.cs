using BluettiWatcher.Decision;
using BluettiWatcher.Io;

namespace BluettiWatcher.Tray;

/// <summary>
/// What the tray shows, built after each poll and each mode change. <see cref="Cut"/> is the decision's confirmed cut (the
/// mains notices follow it); <see cref="Power"/> is the last fresh reading's input (the icon's colour follows it).
/// </summary>
internal sealed record TraySnapshot(double? Soc, TrayPower Power, double? InWatts, double? OutWatts, string Mode,
    double? HoursLeft, DateTimeOffset? ReadAt, bool Stale, bool Cut)
{
    /// <summary>
    /// A reading older than <paramref name="staleAfterSec"/> (or none) is stale: the icon turns grey and the tooltip says when
    /// it was read, never the old values as if they were current.
    /// </summary>
    public static TraySnapshot From(Reading? last, DateTimeOffset? lastAt, StationState state, PowerModeFlag? flag,
        double? hoursLeft, DateTimeOffset now, int staleAfterSec)
    {
        ArgumentNullException.ThrowIfNull(state);
        bool stale = last is null || lastAt is null || (now - lastAt.Value).TotalSeconds > staleAfterSec;
        var power = stale ? TrayPower.Unknown : last!.InAc > 0 ? TrayPower.Mains : TrayPower.Battery;   // stale is false only with a reading
        string mode = $"{flag?.Mode ?? "unknown"} · {state.Manual}";
        return new TraySnapshot(last?.Soc, power, Sum(last?.InAc, last?.InDc), Sum(last?.OutAc, last?.OutDc), mode,
            power == TrayPower.Battery ? hoursLeft : null, lastAt, stale, state.Cut || state.CutBeforeGap);
    }

    private static double? Sum(double? a, double? b) => a is null && b is null ? null : (a ?? 0) + (b ?? 0);
}
