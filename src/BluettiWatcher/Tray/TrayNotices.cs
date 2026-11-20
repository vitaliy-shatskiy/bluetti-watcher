using BluettiWatcher.Io;

namespace BluettiWatcher.Tray;

/// <summary>
/// Which happenings become a notification, and its words. Stored events by their kind (<see cref="FromEvent"/>), the
/// confirmed cut's start and end (<see cref="FromCut"/>), station alarms (<see cref="FromAlarm"/>). Pure: the switches
/// are applied by <see cref="TrayController"/>; the language comes in as <see cref="TrayWords"/>.
/// </summary>
internal static class TrayNotices
{
    /// <summary>
    /// The notice for a stored event; null for a kind that shows none. <paramref name="soc"/> is the last fresh charge,
    /// named where the event's own detail carries a command line instead of the battery. The engine's details stay in
    /// its English.
    /// </summary>
    public static TrayNotice? FromEvent(string kind, string detail, double? soc, TrayWords w)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(w);
        detail ??= "";
        string battery = w.BatteryLead(soc);
        return kind switch
        {
            // Under the modes v2 the mode change that follows is the one notice of the transition.
            "eco-on" or "mains-back" when detail == StationService.ModesV2Detail => null,
            "eco-on" => Make(NotificationKind.ModeChanges, w.EcoOnTitle, battery + w.EcoOnText, NoticeLevel.Info),
            "mains-back" => Make(NotificationKind.ModeChanges, w.ModesLiftedTitle, w.ModesLiftedText, NoticeLevel.Info),
            Modes.Broker.BrokerVersion.StaleEvent => Make(NotificationKind.Failures, w.BrokerStaleTitle, Sentence(detail), NoticeLevel.Warning),
            "mode-changed" => Make(NotificationKind.ModeChanges, w.PowerModeTitle, w.ModeChange(detail), NoticeLevel.Info),
            "mode-dropped" => Make(NotificationKind.ModeChanges, w.ModeDroppedTitle, detail, NoticeLevel.Info),
            "emergency-on" => Make(NotificationKind.LowBattery, w.EmergencyTitle, battery + w.EmergencyText, NoticeLevel.Warning),
            "stop" => Make(NotificationKind.LowBattery, w.CriticalTitle, Sentence(detail), NoticeLevel.Error),
            "hibernate-warning" => Make(NotificationKind.LowBattery, w.HibernateTitle, Sentence(detail), NoticeLevel.Error),
            "action-failed" => Make(NotificationKind.Failures, w.ActionFailedTitle, detail, NoticeLevel.Error),
            "mode-refused" => Make(NotificationKind.Failures, w.ModeRefusedTitle, detail, NoticeLevel.Warning),
            StateActions.GridHigh => Make(NotificationKind.Faults, w.GridHighTitle, Sentence(detail), NoticeLevel.Warning),
            _ => null,
        };
    }

    /// <summary>The confirmed cut started or ended; null when it did not change or nothing came before (the first poll).</summary>
    public static TrayNotice? FromCut(TraySnapshot? before, TraySnapshot after, TrayWords w)
    {
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(w);
        if (before is null || before.Cut == after.Cut) return null;
        string battery = after.Stale ? "" : w.BatteryLead(after.Soc);
        return after.Cut
            ? Make(NotificationKind.Mains, w.MainsLostTitle, $"{w.MainsLostText} {battery}".Trim(), NoticeLevel.Warning)
            : Make(NotificationKind.Mains, w.MainsBackTitle, $"{w.MainsBackText} {battery}".Trim(), NoticeLevel.Info);
    }

    public static TrayNotice FromAlarm(StationAlarm alarm, TrayWords w)
    {
        ArgumentNullException.ThrowIfNull(alarm);
        ArgumentNullException.ThrowIfNull(w);
        return alarm.Kind switch
        {
            StationAlarmKind.OverTemperature => Make(NotificationKind.OverTemperature, w.OverTemperatureTitle, w.OverTemperatureText(alarm.Value), NoticeLevel.Error),
            StationAlarmKind.Fault => Make(NotificationKind.Faults, w.FaultTitle, w.FaultText(alarm.Value), NoticeLevel.Error),
            _ => Make(NotificationKind.Faults, w.WarningTitle, w.WarningText(alarm.Value), NoticeLevel.Warning),
        };
    }

    /// <summary>The page's test notification.</summary>
    public static TrayNotice Test(TrayWords w)
    {
        ArgumentNullException.ThrowIfNull(w);
        return Make(NotificationKind.ModeChanges, w.TestTitle, w.TestText, NoticeLevel.Info);
    }

    private static string Sentence(string detail) => detail.Length == 0 ? "" : char.ToUpperInvariant(detail[0]) + detail[1..] + ".";

    private static TrayNotice Make(NotificationKind kind, string title, string text, NoticeLevel level) =>
        new(kind, Cut(title, TrayNotice.TitleMax), Cut(text.Length == 0 ? title : text, TrayNotice.TextMax), level);

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
