namespace BluettiWatcher.Tray;

/// <summary>One Windows notification: its group (the switch that allows it), title, text and icon.</summary>
internal sealed record TrayNotice(NotificationKind Kind, string Title, string Text, NoticeLevel Level)
{
    /// <summary>The shell's limits: 63 characters of title, 255 of text.</summary>
    public const int TitleMax = 63;
    public const int TextMax = 255;
}
