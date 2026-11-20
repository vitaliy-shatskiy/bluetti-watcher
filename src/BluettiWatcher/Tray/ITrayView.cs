namespace BluettiWatcher.Tray;

/// <summary>Where the tray's picture and notifications go: the shell icon (<see cref="TrayHost"/>), a fake in tests.</summary>
internal interface ITrayView
{
    /// <summary>Shows the snapshot; called from any thread, never blocks.</summary>
    void Show(TraySnapshot snapshot);

    /// <summary>Shows one notification; called from any thread, never blocks.</summary>
    void Notify(TrayNotice notice);
}
