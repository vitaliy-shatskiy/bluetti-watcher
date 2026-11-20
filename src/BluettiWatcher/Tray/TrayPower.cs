namespace BluettiWatcher.Tray;

/// <summary>What the last fresh reading says about the input: it picks the icon's colour.</summary>
internal enum TrayPower
{
    /// <summary>No reading, or the last one is too old to trust.</summary>
    Unknown,
    Mains,
    Battery,
}
