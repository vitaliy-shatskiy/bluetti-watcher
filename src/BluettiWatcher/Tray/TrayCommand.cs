namespace BluettiWatcher.Tray;

/// <summary>What a chosen menu line does (<see cref="TrayMenu.Resolve"/>).</summary>
internal enum TrayCommand
{
    /// <summary>The menu closed with nothing chosen, or an id no line carries.</summary>
    None,
    /// <summary>Choose a mode; the mode id comes with it (null = Auto).</summary>
    SelectMode,
    OpenPanel,
    ExitTray,
}
