namespace BluettiWatcher.Tray;

/// <summary>
/// What the service pushes to the tray: a snapshot after each poll or mode change (<see cref="Update"/>), each stored
/// event (<see cref="OnEvent"/>), the station's alarms (<see cref="ReportAlarms"/>). It turns them into notices, keeps the
/// ones whose group is switched on and hands everything to the view. Thread-safe; nothing here blocks or throws into
/// the caller's poll.
/// </summary>
internal sealed class TrayController(ITrayView view, NotificationSettingsStore settings)
{
    private readonly Lock _gate = new();
    private TraySnapshot? _last;
    private HashSet<string> _alarms = new(StringComparer.Ordinal);

    public void Update(TraySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        TrayNotice? cut;
        lock (_gate)
        {
            cut = TrayNotices.FromCut(_last, snapshot);
            _last = snapshot;
        }
        view.Show(snapshot);
        Notify(cut);
    }

    /// <summary>A stored event (<c>StationStore.EventAdded</c>).</summary>
    public void OnEvent(string kind, string detail)
    {
        double? soc;
        lock (_gate) soc = _last is { Stale: false } fresh ? fresh.Soc : null;
        Notify(TrayNotices.FromEvent(kind, detail, soc));
    }

    /// <summary>
    /// The station's active faults, warnings and over-temperature, the whole list on each reading: an alarm notifies when
    /// it appears, not again while it stays, and again if it clears and comes back.
    /// </summary>
    public void ReportAlarms(IReadOnlyList<StationAlarm> active)
    {
        ArgumentNullException.ThrowIfNull(active);
        var fresh = new List<StationAlarm>();
        lock (_gate)
        {
            var now = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in active)
                if (now.Add(a.Code) && !_alarms.Contains(a.Code)) fresh.Add(a);
            _alarms = now;
        }
        foreach (var a in fresh) Notify(TrayNotices.FromAlarm(a));
    }

    /// <summary>The page's test button: one notice whatever the switches say, so a missing one is Windows', not a switch.</summary>
    public void SendTest() => view.Notify(TrayNotices.Test);

    private void Notify(TrayNotice? notice)
    {
        if (notice is not null && settings.Current.Shows(notice.Kind)) view.Notify(notice);
    }
}
