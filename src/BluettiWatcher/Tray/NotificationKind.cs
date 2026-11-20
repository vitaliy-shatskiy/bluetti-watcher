namespace BluettiWatcher.Tray;

/// <summary>The notification groups, each switched on or off in <c>notifications.json</c> (<see cref="NotificationSettings"/>).</summary>
internal enum NotificationKind
{
    /// <summary>Mains lost, mains back (the decision's confirmed cut).</summary>
    Mains,
    /// <summary>Modes the service changed by itself: eco on, modes lifted, a page choice dropped.</summary>
    ModeChanges,
    /// <summary>The battery levels: emergency, stop, the hibernate warning.</summary>
    LowBattery,
    /// <summary>A command that failed, a mode choice refused.</summary>
    Failures,
    /// <summary>Station faults and warnings, the grid voltage alert.</summary>
    Faults,
    /// <summary>The station reports over-temperature.</summary>
    OverTemperature,
}
