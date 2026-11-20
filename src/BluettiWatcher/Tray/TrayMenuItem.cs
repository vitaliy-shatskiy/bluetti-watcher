namespace BluettiWatcher.Tray;

/// <summary>One line of the tray menu. <see cref="Radio"/> lines form one group: Auto and the modes.</summary>
internal sealed record TrayMenuItem(int Id, string Text, bool Radio, bool Checked)
{
    /// <summary>A separator line: no command, no text.</summary>
    public bool Separator => Id == 0;
}
