using BluettiWatcher.Tray;

namespace BluettiWatcher.Tests;

// Toast or tray balloon: the toast while a Start-menu shortcut carries the app id, else the balloon (README, Tray).
public sealed class NoticeSenderTests
{
    private static readonly TrayNotice Lost = new(NotificationKind.Mains, "Mains lost", "Battery 60 %.", NoticeLevel.Warning);
    private static readonly TrayNotice Back = new(NotificationKind.Mains, "Mains is back", "Battery 61 %.", NoticeLevel.Info);

    private readonly List<string> _log = [];
    private readonly List<TrayNotice> _toasts = [];
    private readonly List<TrayNotice> _balloons = [];

    private NoticeSender Sender(Func<bool> registered, Action<TrayNotice>? toast = null) =>
        new(registered, toast ?? _toasts.Add, n => { _balloons.Add(n); return true; }, _log.Add);

    [Fact]
    public void Send_AppIdRegistered_ShowsAToastAndNoBalloon()
    {
        Sender(() => true).Send(Lost);

        Assert.Equal([Lost], _toasts);
        Assert.Empty(_balloons);
        Assert.Contains("notice: Mains Mains lost", _log);
    }

    [Fact]
    public void Send_NoShortcutWithTheAppId_FallsBackToTheBalloonAndSaysSoOnce()
    {
        var sender = Sender(() => false);

        sender.Send(Lost);
        sender.Send(Back);

        Assert.Empty(_toasts);
        Assert.Equal([Lost, Back], _balloons);
        Assert.Single(_log, l => l.Contains("tray balloons", StringComparison.Ordinal) && l.Contains(Toasts.AppId, StringComparison.Ordinal));
        Assert.Contains("notice: Mains Mains is back", _log);
    }

    [Fact]
    public void Send_ToastFails_FallsBackToTheBalloonWithTheReason()
    {
        Sender(() => true, _ => throw new InvalidOperationException("no notifier")).Send(Lost);

        Assert.Equal([Lost], _balloons);
        Assert.Contains(_log, l => l.Contains("tray balloons", StringComparison.Ordinal) && l.Contains("no notifier", StringComparison.Ordinal));
    }

    [Fact]
    public void Send_ShortcutAddedLater_SwitchesToToastsAndSaysSo()
    {
        bool registered = false;
        var sender = Sender(() => registered);

        sender.Send(Lost);
        registered = true;
        sender.Send(Back);

        Assert.Equal([Back], _toasts);
        Assert.Single(_log, l => l.Contains("Windows toasts", StringComparison.Ordinal));
    }

    [Fact]
    public void Xml_TitleAndTextWithMarkup_AreEscapedAndAClickOpensThePanel()
    {
        var n = new TrayNotice(NotificationKind.Failures, "A <b> & \"c\"", "x < y", NoticeLevel.Error);

        string xml = Toasts.Xml(n, new Uri("http://127.0.0.1:18940/"));

        var root = System.Xml.Linq.XDocument.Parse(xml).Root!;   // Parse throws on a document without a root
        Assert.Equal("protocol", (string?)root.Attribute("activationType"));
        Assert.Equal("http://127.0.0.1:18940/", (string?)root.Attribute("launch"));
        Assert.Equal(["A <b> & \"c\"", "x < y"], root.Descendants("text").Select(t => t.Value));
    }
}
