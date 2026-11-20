using System.Globalization;

namespace BluettiWatcher.Decision;

/// <summary>
/// The station's own warnings, faults and temperatures (V2 stations, <see cref="StationExtras"/>): an event when a code
/// appears or clears, and one when a temperature goes above the configured limit, re-armed only
/// <see cref="TempRearmMargin"/> °C under it, so a value hovering at the line is one event. A block not read this poll
/// changes nothing.
/// </summary>
internal static class StationAlarms
{
    public const string Warning = "station-warning";
    public const string Fault = "station-fault";
    public const string Cleared = "station-alarm-cleared";
    public const string Hot = "station-hot";
    public const double TempRearmMargin = 5;

    /// <summary>The codes active and the hot state after this reading's <paramref name="x"/>, and the events it raises.</summary>
    public static (IReadOnlySet<string> Active, bool Hot, IReadOnlyList<AlarmEvent> Events) Step(IReadOnlySet<string> active, bool hot, StationExtras? x, int highC)
    {
        var events = new List<AlarmEvent>();
        var now = new HashSet<string>(active, StringComparer.Ordinal);
        if (x?.Warnings is { } warnings && x.Faults is { } faults)
        {
            now = new HashSet<string>(warnings.Concat(faults), StringComparer.Ordinal);
            foreach (var w in warnings.Where(c => !active.Contains(c))) events.Add(new(Warning, $"station warning {w}"));
            foreach (var f in faults.Where(c => !active.Contains(c))) events.Add(new(Fault, $"station fault {f}"));
            foreach (var c in active.Where(c => !now.Contains(c)).OrderBy(c => c, StringComparer.Ordinal)) events.Add(new(Cleared, $"station code {c} cleared"));
        }
        if (x?.TempMax is { } t)
        {
            if (t > highC && !hot)
            {
                hot = true;
                events.Add(new(Hot, string.Create(CultureInfo.InvariantCulture, $"station temperature {t:0} °C, above {highC} °C")));
            }
            else if (t < highC - TempRearmMargin) hot = false;
        }
        return (now, hot, events);
    }
}
