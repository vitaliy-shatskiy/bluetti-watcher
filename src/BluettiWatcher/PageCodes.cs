namespace BluettiWatcher;

/// <summary>A text the page shows, with the stable code the page translates it by; <see cref="Args"/> fill the code's {0}, {1}, ...</summary>
internal sealed record PageNote(string Code, string Text, params string[] Args);

/// <summary>
/// Every code the service sends the page in place of prose. The page's dictionary holds, in each language, <c>n_</c> + a note
/// code, <c>v_</c> + a value code, <c>om_</c> + an output mode, <c>st_</c> + an action state and <c>ck_</c> + a rule kind,
/// hyphens as underscores (a test checks).
/// </summary>
internal static class PageCodes
{
    // /api/now notes
    public const string StationSilent = "station-silent";
    public const string HibernateHeld = "hibernate-held";
    public const string MaxHeld = "max-held";
    public const string ReadingRejected = "reading-rejected";
    public const string ReadingRejectedIncomplete = "reading-rejected-incomplete";
    public const string ReadingRejectedImplausible = "reading-rejected-implausible";
    public const string WakeDisarmFailed = "wake-disarm-failed";
    // modeError and the POST's answer
    public const string EcoHeld = "eco-held";
    public const string AutoHeld = "auto-held";
    public const string FlagUnreadable = "flag-unreadable";
    public const string ModeDroppedBack = "mode-dropped-back";
    public const string ModeDroppedLost = "mode-dropped-lost";
    public const string ModeDropped = "mode-dropped";
    public const string ModeFailed = "mode-failed";
    public const string ModeOther = "mode-other";
    // modeHold: what holds the mode under the buttons, and until when
    public const string MaxPinnedCut = "max-pinned-cut";
    public const string MaxPinnedMains = "max-pinned-mains";
    public const string EcoPinnedCut = "eco-pinned-cut";
    public const string EcoPinnedMains = "eco-pinned-mains";
    public const string AutoNext = "auto-next";
    // the eco chip
    public const string EcoEmergency = "eco-emergency";
    public const string EcoEmergencyOver = "eco-emergency-over";
    public const string EcoMains = "eco-mains";
    public const string EcoUntil = "eco-until";
    // DTEK Watcher notes
    public const string DtekQueuesUnknown = "dtek-queues-unknown";
    public const string DtekNoSchedule = "dtek-no-schedule";
    public const string DtekStale = "dtek-stale";
    public const string DtekQueueNotFixed = "dtek-queue-not-fixed";
    public const string DtekGroupRefuted = "dtek-group-refuted";
    public const string DtekGroupNotHouse = "dtek-group-not-house";
    public const string DtekUpdateUnreadable = "dtek-update-unreadable";
    public const string DtekDatasetFailed = "dtek-dataset-failed";
    public const string DtekSiteBlocked = "dtek-site-blocked";
    public const string DtekSiteFailed = "dtek-site-failed";
    public const string DtekConfig = "dtek-config";
    public const string DtekNotStarted = "dtek-not-started";
    public const string DtekModuleError = "dtek-module-error";

    /// <summary>Every note code above (no reflection: the service is NativeAOT; a test compares it with the constants).</summary>
    public static IReadOnlyList<string> Notes { get; } =
    [
        StationSilent, HibernateHeld, MaxHeld, ReadingRejected, ReadingRejectedIncomplete, ReadingRejectedImplausible, WakeDisarmFailed, EcoHeld, AutoHeld, FlagUnreadable, ModeDroppedBack, ModeDroppedLost, ModeDropped,
        ModeFailed, ModeOther, MaxPinnedCut, MaxPinnedMains, EcoPinnedCut, EcoPinnedMains, AutoNext, EcoEmergency, EcoEmergencyOver, EcoMains, EcoUntil, DtekQueuesUnknown, DtekNoSchedule, DtekStale, DtekQueueNotFixed,
        DtekGroupRefuted, DtekGroupNotHouse, DtekUpdateUnreadable, DtekDatasetFailed, DtekSiteBlocked, DtekSiteFailed, DtekConfig, DtekNotStarted,
        DtekModuleError,
    ];

    /// <summary>
    /// Codes sent as values: DTEK states and sources, source states, read error classes, the page's modes, "battery lasts",
    /// the reading sources, the cloud's states and the sign-in's (GET /api/cloud), the fields a rejected reading lacked.
    /// </summary>
    public static IReadOnlyList<string> Values { get; } =
        ["off", "ok", "stale", "none", "blocked", "failed", "network", "format", "dataset", "site", "auto", "eco", "max", "yes", "no", "unknown",
         "bluetooth", "cloud", "signed-out", "sign-in-needed", "error", "idle", "waiting", "signed-in",
         "soc", "ac-input-power", "ac-output-power", "output-mode"];

    /// <summary>The station's output modes (bluetti_bt_lib enums/output_mode.py), CLOUD_MAINS (a cloud reading with AC input, design.md §4 AC2a) and BLE_MAINS (the same from a Bluetooth model with no output-mode field).</summary>
    public static IReadOnlyList<string> OutputModes { get; } = ["STOP", "INVERTER_OUTPUT", "BYPASS_OUTPUT_C", "BYPASS_OUTPUT_D", "LOAD_MATCHING", "CLOUD_MAINS", "BLE_MAINS"];

    /// <summary>The modes engine's action states as /api/modes sends them (<c>ActionStatus</c>; a test compares the enum).</summary>
    public static IReadOnlyList<string> ActionStatuses { get; } = ["unknown", "satisfied", "applied", "failed", "unsupported", "relinquished", "restore-failed"];

    /// <summary>The Auto rule kinds as /api/modes sends them (<c>ConditionKind</c>; a test compares the enum).</summary>
    public static IReadOnlyList<string> ConditionKinds { get; } = ["mainsOffFor", "mainsOnFor", "socBelow", "cutWithin", "timeWindow"];

    /// <summary>The page's dictionary keys for every code.</summary>
    public static IEnumerable<string> Keys() =>
        Notes.Select(c => "n_" + c).Concat(Values.Select(c => "v_" + c)).Concat(OutputModes.Select(c => "om_" + c))
            .Concat(ActionStatuses.Select(c => "st_" + c)).Concat(ConditionKinds.Select(c => "ck_" + c)).Select(k => k.Replace('-', '_'));
}
