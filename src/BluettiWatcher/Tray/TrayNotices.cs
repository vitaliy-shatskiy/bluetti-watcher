using BluettiWatcher.Io;

namespace BluettiWatcher.Tray;

/// <summary>
/// Which happenings become a notification, and its words. Stored events by their kind (<see cref="FromEvent"/>), the
/// confirmed cut's start and end (<see cref="FromCut"/>), station alarms (<see cref="FromAlarm"/>). Pure: the switches
/// are applied by <see cref="TrayController"/>.
/// </summary>
internal static class TrayNotices
{
    /// <summary>
    /// The notice for a stored event; null for a kind that shows none. <paramref name="soc"/> is the last fresh charge,
    /// named where the event's own detail carries a command line instead of the battery.
    /// </summary>
    public static TrayNotice? FromEvent(string kind, string detail, double? soc)
    {
        ArgumentNullException.ThrowIfNull(kind);
        detail ??= "";
        string battery = soc is { } v ? $"Battery {TrayIconRenderer.Text(v)} %. " : "";
        return kind switch
        {
            // Under the modes v2 the mode change that follows is the one notice of the transition.
            "eco-on" or "mains-back" when detail == StationService.ModesV2Detail => null,
            "eco-on" => Make(NotificationKind.ModeChanges, "Eco mode on", $"{battery}The PC runs on the battery: eco is on.", NoticeLevel.Info),
            "mains-back" => Make(NotificationKind.ModeChanges, "Modes lifted", "Mains is back: eco and the battery levels are lifted.", NoticeLevel.Info),
            Modes.Broker.BrokerVersion.StaleEvent => Make(NotificationKind.Failures, "Admin broker out of date", Sentence(detail), NoticeLevel.Warning),
            Modes.ModesEngine.ModeChosenEvent => null,   // the user made it: he knows
            "mode-changed" =>Make(NotificationKind.ModeChanges, "Power mode", detail, NoticeLevel.Info),
            "mode-dropped" =>Make(NotificationKind.ModeChanges, "Mode choice dropped", detail, NoticeLevel.Info),
            "emergency-on" => Make(NotificationKind.LowBattery, "Battery emergency", $"{battery}Emergency mode is on.", NoticeLevel.Warning),
            "stop" => Make(NotificationKind.LowBattery, "Battery critical", Sentence(detail), NoticeLevel.Error),
            "hibernate-warning" => Make(NotificationKind.LowBattery, "The PC will hibernate", Sentence(detail), NoticeLevel.Error),
            "action-failed" => Make(NotificationKind.Failures, "Action failed", detail, NoticeLevel.Error),
            "mode-refused" => Make(NotificationKind.Failures, "Mode refused", detail, NoticeLevel.Warning),
            StateActions.GridHigh => Make(NotificationKind.Faults, "Grid voltage high", Sentence(detail), NoticeLevel.Warning),
            _ => null,
        };
    }

    /// <summary>The confirmed cut started or ended; null when it did not change or nothing came before (the first poll).</summary>
    public static TrayNotice? FromCut(TraySnapshot? before, TraySnapshot after)
    {
        ArgumentNullException.ThrowIfNull(after);
        if (before is null || before.Cut == after.Cut) return null;
        string battery = after.Soc is { } v && !after.Stale ? $"Battery {TrayIconRenderer.Text(v)} %." : "";
        return after.Cut
            ? Make(NotificationKind.Mains, "Mains lost", $"The station runs on its battery. {battery}".Trim(), NoticeLevel.Warning)
            : Make(NotificationKind.Mains, "Mains is back", $"The station is on mains again. {battery}".Trim(), NoticeLevel.Info);
    }

    public static TrayNotice FromAlarm(StationAlarm alarm)
    {
        ArgumentNullException.ThrowIfNull(alarm);
        return alarm.Kind switch
        {
            StationAlarmKind.OverTemperature => Make(NotificationKind.OverTemperature, "Station over temperature", alarm.Text, NoticeLevel.Error),
            StationAlarmKind.Fault => Make(NotificationKind.Faults, "Station fault", alarm.Text, NoticeLevel.Error),
            _ => Make(NotificationKind.Faults, "Station warning", alarm.Text, NoticeLevel.Warning),
        };
    }

    /// <summary>The page's test notification.</summary>
    public static readonly TrayNotice Test = Make(NotificationKind.ModeChanges, "Test notification", "Bluetti Watcher notifications reach this PC.", NoticeLevel.Info);

    private static string Sentence(string detail) => detail.Length == 0 ? "" : char.ToUpperInvariant(detail[0]) + detail[1..] + ".";

    private static TrayNotice Make(NotificationKind kind, string title, string text, NoticeLevel level) =>
        new(kind, Cut(title, TrayNotice.TitleMax), Cut(text.Length == 0 ? title : text, TrayNotice.TextMax), level);

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
