namespace BluettiWatcher.Tray;

/// <summary>
/// The tray menu as data: Auto and the modes as one radio group (consecutive ids from <see cref="AutoId"/>, the shell's
/// radio check needs a range), a separator, "Open panel", "Exit tray". The host draws it and hands back the chosen id.
/// </summary>
internal static class TrayMenu
{
    public const int AutoId = 1;
    public const int OpenPanelId = 1000;
    public const int ExitTrayId = 1001;
    /// <summary>Mode ids run from <c>AutoId + 1</c>; the range stays under <see cref="OpenPanelId"/>.</summary>
    public const int MaxModes = OpenPanelId - AutoId - 1;

    public static IReadOnlyList<TrayMenuItem> Build(IReadOnlyList<TrayMode> modes, string? current)
    {
        ArgumentNullException.ThrowIfNull(modes);
        if (modes.Count > MaxModes) throw new ArgumentOutOfRangeException(nameof(modes), modes.Count, $"at most {MaxModes} modes");
        // A current id no mode carries (a mode removed meanwhile) checks nothing rather than a wrong line.
        var items = new List<TrayMenuItem>(modes.Count + 4) { new(AutoId, "Auto", true, current is null) };
        for (int i = 0; i < modes.Count; i++)
            items.Add(new TrayMenuItem(AutoId + 1 + i, modes[i].Name, true, string.Equals(modes[i].Id, current, StringComparison.Ordinal)));
        items.Add(new TrayMenuItem(0, "", false, false));
        items.Add(new TrayMenuItem(OpenPanelId, "Open panel", false, false));
        items.Add(new TrayMenuItem(ExitTrayId, "Exit tray", false, false));
        return items;
    }

    /// <summary>The chosen id as a command; for <see cref="TrayCommand.SelectMode"/> the mode id (null = Auto).</summary>
    public static (TrayCommand Command, string? ModeId) Resolve(int id, IReadOnlyList<TrayMode> modes)
    {
        ArgumentNullException.ThrowIfNull(modes);
        return id switch
        {
            AutoId => (TrayCommand.SelectMode, null),
            OpenPanelId => (TrayCommand.OpenPanel, null),
            ExitTrayId => (TrayCommand.ExitTray, null),
            _ when id > AutoId && id - AutoId - 1 < modes.Count => (TrayCommand.SelectMode, modes[id - AutoId - 1].Id),
            _ => (TrayCommand.None, null),
        };
    }
}
