using System.Text;
using System.Text.RegularExpressions;
using BluettiWatcher.Decision;
using BluettiWatcher.Http;
using BluettiWatcher.Io;
using BluettiWatcher.Tray;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

/// <summary>The tray's tooltip, menu and notices follow the page's language (page-settings.json "lang").</summary>
public sealed partial class TrayLanguageTests : IDisposable
{
    private const int Port = 18940;
    private readonly string _dir = Directory.CreateTempSubdirectory("bw-tray-lang-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FakeView : ITrayView
    {
        public List<TrayNotice> Notices { get; } = [];
        public void Show(TraySnapshot snapshot) { }
        public void Notify(TrayNotice notice) => Notices.Add(notice);
    }

    private static TraySnapshot Snap(bool cut, double soc = 59) =>
        new(soc, cut ? TrayPower.Battery : TrayPower.Mains, 120, 300, "eco", ManualMode.Auto, cut ? 3.34 : null, T0, false, cut);

    private PageSettingsStore Page(string lang)
    {
        var store = new PageSettingsStore(Path.Combine(_dir, PageSettingsStore.FileName), _ => { });
        Put(store, lang);
        return store;
    }

    private static void Put(PageSettingsStore store, string lang)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = $"127.0.0.1:{Port}", ["X-Station"] = "1" };
        var put = new HttpRequest("PUT", PageSettingsStore.Route, new Dictionary<string, string>(), headers, Encoding.UTF8.GetBytes($$"""{"lang":"{{lang}}"}"""));
        Assert.Equal(200, store.Handle(put, Port).Status);
    }

    [Fact]
    public void Tooltip_PageInUkrainian_IsUkrainianOneFactPerLine()
    {
        var page = Page("uk");
        string tip = TrayText.Tooltip(Snap(cut: true), TimeZoneInfo.Utc, TrayWords.For(page.Lang));
        Assert.Equal("Батарея 59 %\nВід батареї\nВхід 120 Вт\nВихід 300 Вт\nРежим Еко, авто\nВистачить на 3 год 20 хв", tip);
    }

    [Fact]
    public void Tooltip_StaleInUkrainian_SaysSinceWhen()
    {
        string tip = TrayText.Tooltip(Snap(cut: false) with { Stale = true, ReadAt = T0.AddHours(3).AddMinutes(12) }, TimeZoneInfo.Utc, TrayWords.Uk);
        Assert.StartsWith("Батарея 59 %\nНемає даних з ", tip, StringComparison.Ordinal);
        Assert.DoesNotContain("Вт", tip, StringComparison.Ordinal);
    }

    [Fact]
    public void Menu_PageInUkrainian_NamesBuiltInModesInUkrainianAndKeepsACustomName()
    {
        var page = Page("uk");
        TrayMode[] modes = [new("normal", "Normal"), new("eco", "Eco"), new("max", "Max"), new("night", "Night")];
        var items = TrayMenu.Build(modes, null, TrayWords.For(page.Lang));
        Assert.Equal(["Авто", "Звичайний", "Еко", "Макс", "Night", "", "Відкрити панель", "Закрити значок"], items.Select(i => i.Text));
    }

    [Fact]
    public void Notices_PageSwitchedToUkrainian_TheNextNoticeIsUkrainian()
    {
        var page = Page("en");
        var view = new FakeView();
        var tray = new TrayController(view, new NotificationSettingsStore(Path.Combine(_dir, "notifications.json"), _ => { }), () => TrayWords.For(page.Lang));
        tray.Update(Snap(cut: false));
        tray.Update(Snap(cut: true));
        Put(page, "uk");
        tray.Update(Snap(cut: false, soc: 61));
        tray.SendTest();
        Assert.Equal(["Mains lost", "Мережа є", "Тестове сповіщення"], view.Notices.Select(n => n.Title));
        Assert.Equal("Станція знову від мережі. Батарея 61 %.", view.Notices[1].Text);
        Assert.Equal("Сповіщення Bluetti Watcher доходять до цього ПК.", view.Notices[2].Text);
    }

    [Fact]
    public void Notices_Ukrainian_MainsLostAndModeChangeNameTheBuiltInMode()
    {
        Assert.Equal("Станція працює від батареї. Батарея 59 %.", TrayNotices.FromCut(Snap(cut: false), Snap(cut: true), TrayWords.Uk)!.Text);
        var mode = TrayNotices.FromEvent("mode-changed", "eco (rule 1: mains off for 300 s)", 50, TrayWords.Uk)!;
        Assert.Equal(("Режим живлення", "Еко (rule 1: mains off for 300 s)"), (mode.Title, mode.Text));
        Assert.Equal("Night (manual)", TrayNotices.FromEvent("mode-changed", "Night (manual)", 50, TrayWords.Uk)!.Text);
    }

    /// <summary>Every tray text a language can show: tooltip, menu, each event's notice, alarms, the test notice.</summary>
    private static List<string> AllTexts(TrayWords w)
    {
        var texts = new List<string>
        {
            TrayText.Tooltip(Snap(cut: true), TimeZoneInfo.Utc, w),
            TrayText.Tooltip(Snap(cut: false) with { Stale = true }, TimeZoneInfo.Utc, w),
            TrayText.Tooltip(Snap(cut: false) with { Stale = true, ReadAt = null, Choice = ManualMode.Max }, TimeZoneInfo.Utc, w),
            TrayText.Tooltip(Snap(cut: false) with { Mode = "normal", Choice = ManualMode.Eco }, TimeZoneInfo.Utc, w),
        };
        texts.AddRange(TrayMenu.Build([new("normal", "Normal"), new("eco", "Eco"), new("max", "Max")], null, w).Select(i => i.Text));
        string[] kinds = ["eco-on", "mains-back", BluettiWatcher.Modes.Broker.BrokerVersion.StaleEvent, "mode-changed", "mode-dropped", "emergency-on",
            "stop", "hibernate-warning", "action-failed", "mode-refused", StateActions.GridHigh];
        var notices = kinds.Select(k => TrayNotices.FromEvent(k, "", 59, w)!).ToList();
        notices.Add(TrayNotices.FromCut(Snap(cut: false), Snap(cut: true), w)!);
        notices.Add(TrayNotices.FromCut(Snap(cut: true), Snap(cut: false), w)!);
        notices.AddRange(StationAlarm.ListOf(new StationAlarmState(["W1"], ["F7"], Hot: true, TempMax: 63)).Select(a => TrayNotices.FromAlarm(a, w)));
        notices.Add(TrayNotices.Test(w));
        Assert.All(notices, n => Assert.True(n.Title.Length <= TrayNotice.TitleMax && n.Text.Length <= TrayNotice.TextMax, n.Title));
        texts.AddRange(notices.SelectMany(n => new[] { n.Title, n.Text }));
        Assert.All(texts.Where(t => t.Contains('\n', StringComparison.Ordinal)), t => Assert.True(t.Length <= TrayText.TooltipMax, t));
        return texts;
    }

    [Fact]
    public void Texts_BothLanguages_HaveNoMiddleDotNoArrowNoLabelColon()
    {
        var all = AllTexts(TrayWords.En).Concat(AllTexts(TrayWords.Uk));
        Assert.DoesNotContain(all, t => t.Contains('·', StringComparison.Ordinal) || t.Contains("->", StringComparison.Ordinal)
            || t.Contains('→', StringComparison.Ordinal) || t.Contains(": ", StringComparison.Ordinal));
    }

    /// <summary>PageI18nTests' lists as escapes: the four Russian letters Ukrainian lacks, and Russian words (no, now, this, what, for, all).</summary>
    private static readonly System.Buffers.SearchValues<char> NotUkrainian = System.Buffers.SearchValues.Create("\u044B\u042B\u044D\u042D\u0451\u0401\u044A\u042A");

    [GeneratedRegex("(?<!\\p{L})(\u043D\u0435\u0442|\u0441\u0435\u0439\u0447\u0430\u0441|\u044D\u0442\u043E|\u0447\u0442\u043E|\u0434\u043B\u044F|\u0432\u0441\u0451|\u0432\u0441\u0435)(?!\\p{L})", RegexOptions.IgnoreCase)]
    private static partial Regex RussianWord();

    [Fact]
    public void Texts_Ukrainian_HoldNoRussianLetterOrWordAndNoEnglishFallback()
    {
        var uk = AllTexts(TrayWords.Uk);
        Assert.DoesNotContain(uk, t => t.AsSpan().IndexOfAny(NotUkrainian) >= 0 || RussianWord().IsMatch(t));
        // every text with words of its own differs from English (the engine detail is "" here, so a title stands in)
        var en = AllTexts(TrayWords.En);
        Assert.DoesNotContain(uk.Zip(en), p => p.First == p.Second && p.First.Any(char.IsLetter));
        // controls: the checks catch a Russian word and a Russian letter
        Assert.Matches(RussianWord(), "\u0434\u043B\u044F \u041F\u041A");
        Assert.True("\u044D\u0442\u043E".AsSpan().IndexOfAny(NotUkrainian) >= 0, "the letter list finds nothing");
    }
}
