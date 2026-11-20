using System.Text;
using System.Text.Json;
using BluettiWatcher.Http;
using BluettiWatcher.Io;
using BluettiWatcher.Tray;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

// Which happenings become a Windows notification, the switches, notifications.json and its API (README, Tray; api.md).
public sealed class TrayNotificationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-watcher-test-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];
    private readonly FakeView _view = new();

    public TrayNotificationTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string SettingsFile => Path.Combine(_dir, "notifications.json");

    private NotificationSettingsStore Store() => new(SettingsFile, _log.Add);

    private sealed class FakeView : ITrayView
    {
        public List<TraySnapshot> Shown { get; } = [];
        public List<TrayNotice> Notices { get; } = [];
        public void Show(TraySnapshot snapshot) => Shown.Add(snapshot);
        public void Notify(TrayNotice notice) => Notices.Add(notice);
    }

    private static TraySnapshot Snap(bool cut, double soc = 40, bool stale = false) =>
        new(soc, cut ? TrayPower.Battery : TrayPower.Mains, 0, 200, "normal", Decision.ManualMode.Auto, null, T0, stale, cut);

    [Theory]
    [InlineData("eco-on", "ModeChanges", "Eco mode on", "Info")]
    [InlineData("mains-back", "ModeChanges", "Modes lifted", "Info")]
    [InlineData("mode-dropped", "ModeChanges", "Mode choice dropped", "Info")]
    [InlineData("emergency-on", "LowBattery", "Battery emergency", "Warning")]
    [InlineData("stop", "LowBattery", "Battery critical", "Error")]
    [InlineData("hibernate-warning", "LowBattery", "The PC will hibernate", "Error")]
    [InlineData("action-failed", "Failures", "Action failed", "Error")]
    [InlineData("mode-refused", "Failures", "Mode refused", "Warning")]
    [InlineData(StateActions.GridHigh, "Faults", "Grid voltage high", "Warning")]
    public void Each_notified_event_maps_to_its_group_title_and_icon(string kind, string group, string title, string level)
    {
        var n = TrayNotices.FromEvent(kind, "battery 9 % - the flag says critical", 9, TrayWords.En);
        Assert.NotNull(n);
        Assert.Equal(group, n.Kind.ToString());
        Assert.Equal(title, n.Title);
        Assert.Equal(level, n.Level.ToString());
        Assert.NotEmpty(n.Text);
    }

    [Theory]
    [InlineData("mode")]
    [InlineData("mode-applied")]
    [InlineData("hibernate")]
    [InlineData("hibernate-refused")]
    [InlineData("config-warning")]
    [InlineData("")]
    public void Other_events_notify_nothing(string kind) => Assert.Null(TrayNotices.FromEvent(kind, "x", 50, TrayWords.En));

    [Theory]
    [InlineData("eco-on", "eco (mains off for 5 min)")]
    [InlineData("mains-back", "normal (mains on for 5 min)")]
    public void Under_the_modes_v2_a_station_transition_notifies_once_by_its_mode_change(string station, string mode)
    {
        // The two events StationService stores for one transition under v2.
        var notices = new[] { TrayNotices.FromEvent(station, StationService.ModesV2Detail, 50, TrayWords.En), TrayNotices.FromEvent("mode-changed", mode, 50, TrayWords.En) };

        Assert.Equal("Power mode", Assert.Single(notices, n => n is not null)!.Title);
    }

    [Fact]
    public void The_events_text_is_the_detail_and_never_a_command_line_for_eco_or_emergency()
    {
        Assert.Equal("Battery 9 % - the flag says critical.", TrayNotices.FromEvent("stop", "battery 9 % - the flag says critical", 9, TrayWords.En)!.Text);
        string eco = TrayNotices.FromEvent("eco-on", @"pwsh.exe -File C:\x\power-mode.ps1 eco", 42, TrayWords.En)!.Text;
        Assert.StartsWith("Battery 42 %.", eco, StringComparison.Ordinal);
        Assert.DoesNotContain("pwsh", eco, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_detail_is_cut_to_the_shell_limit()
    {
        var n = TrayNotices.FromEvent("action-failed", new string('x', 1000), null, TrayWords.En)!;
        Assert.Equal(TrayNotice.TextMax, n.Text.Length);
        Assert.EndsWith("…", n.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Mains_lost_and_back_follow_the_confirmed_cut_not_the_first_snapshot()
    {
        var tray = new TrayController(_view, Store(), () => TrayWords.En);
        tray.Update(Snap(cut: true));                 // the first snapshot after a start: a cut already on says nothing
        tray.Update(Snap(cut: true, soc: 39));        // no change
        tray.Update(Snap(cut: false, soc: 39));
        tray.Update(Snap(cut: true, soc: 60));
        Assert.Equal(4, _view.Shown.Count);
        Assert.Equal(["Mains is back", "Mains lost"], _view.Notices.Select(n => n.Title));
        Assert.All(_view.Notices, n => Assert.Equal(NotificationKind.Mains, n.Kind));
        Assert.Contains("Battery 60 %", _view.Notices[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stale_charge_is_not_named_in_a_notice()
    {
        var tray = new TrayController(_view, Store(), () => TrayWords.En);
        tray.Update(Snap(cut: false));
        tray.Update(Snap(cut: true, soc: 77, stale: true));
        tray.OnEvent("eco-on", "cmd");
        Assert.All(_view.Notices, n => Assert.DoesNotContain("77", n.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void A_switched_off_group_shows_nothing_and_the_others_still_show()
    {
        File.WriteAllText(SettingsFile, """{ "failures": false }""");
        var tray = new TrayController(_view, Store(), () => TrayWords.En);
        tray.OnEvent("action-failed", "eco-on failed");
        tray.OnEvent("mode-refused", "Max held back");
        tray.OnEvent("stop", "battery 9 %");
        Assert.Equal(["Battery critical"], _view.Notices.Select(n => n.Title));
    }

    [Fact]
    public void A_switch_change_applies_to_the_next_notice_without_a_restart()
    {
        var store = Store();
        var tray = new TrayController(_view, store, () => TrayWords.En);
        using (var off = JsonDocument.Parse("""{ "lowBattery": false }""")) Assert.Null(store.Update(off.RootElement));
        tray.OnEvent("stop", "battery 9 %");
        Assert.Empty(_view.Notices);
    }

    [Fact]
    public void An_alarm_notifies_when_it_appears_not_while_it_stays_and_again_after_it_cleared()
    {
        var tray = new TrayController(_view, Store(), () => TrayWords.En);
        var hot = new StationAlarm("temp", "Battery 61 °C", StationAlarmKind.OverTemperature);
        var fault = new StationAlarm("f12", "Inverter fault 12", StationAlarmKind.Fault);
        tray.ReportAlarms([hot]);
        tray.ReportAlarms([hot, fault, fault]);
        tray.ReportAlarms([fault]);
        tray.ReportAlarms([]);
        tray.ReportAlarms([hot]);
        Assert.Equal(["Station over temperature", "Station fault", "Station over temperature"], _view.Notices.Select(n => n.Title));
        Assert.Equal([NotificationKind.OverTemperature, NotificationKind.Faults, NotificationKind.OverTemperature], _view.Notices.Select(n => n.Kind));
    }

    [Fact]
    public void Over_temperature_has_its_own_switch()
    {
        File.WriteAllText(SettingsFile, """{ "overTemperature": false }""");
        var tray = new TrayController(_view, Store(), () => TrayWords.En);
        tray.ReportAlarms([new StationAlarm("temp", "hot", StationAlarmKind.OverTemperature), new StationAlarm("w1", "fan", StationAlarmKind.Warning)]);
        Assert.Equal(["Station warning"], _view.Notices.Select(n => n.Title));
    }

    [Fact]
    public void A_stored_event_reaches_the_tray_through_the_store_hook()
    {
        var tray = new TrayController(_view, Store(), () => TrayWords.En);
        using (var db = new BluettiWatcher.Storage.StationStore(Path.Combine(_dir, "station.db")))
        {
            db.EventAdded = tray.OnEvent;
            db.AddEvent(T0, "mode-refused", "Max held back: battery emergency");
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Assert.Equal("Max held back: battery emergency", Assert.Single(_view.Notices).Text);
    }

    [Fact]
    public void Without_a_file_every_group_is_on()
    {
        var s = Store().Current;
        Assert.All(NotificationSettings.Keys, k => Assert.True(s.IsOn(k.Kind)));
        Assert.False(File.Exists(SettingsFile));
    }

    [Fact]
    public void An_unreadable_file_is_logged_and_every_group_is_on()
    {
        File.WriteAllText(SettingsFile, "{ not json");
        Assert.True(Store().Current.Failures);
        Assert.Contains(_log, l => l.Contains("unreadable", StringComparison.Ordinal));
        File.WriteAllText(SettingsFile, """{ "mains": "no" }""");
        Assert.True(Store().Current.Mains);
        Assert.Contains(_log, l => l.Contains("ignored", StringComparison.Ordinal));
    }

    [Fact]
    public void An_update_is_saved_and_read_back_by_the_next_start()
    {
        using (var doc = JsonDocument.Parse("""{ "mains": false, "faults": false }""")) Assert.Null(Store().Update(doc.RootElement));
        var again = Store().Current;
        Assert.Equal(new NotificationSettings(Mains: false, Faults: false), again);
        Assert.False(File.Exists(SettingsFile + ".tmp"));
    }

    [Theory]
    [InlineData("""{ "mains": false, "sound": true }""", "unknown notification")]
    [InlineData("""{ "mains": false, "faults": 0 }""", "must be true or false")]
    [InlineData("""[ true ]""", "JSON object")]
    public void A_bad_update_changes_nothing(string body, string why)
    {
        var store = Store();
        using var doc = JsonDocument.Parse(body);
        Assert.Contains(why, store.Update(doc.RootElement), StringComparison.Ordinal);
        Assert.True(store.Current.Mains);   // all or nothing: the valid key in the same body is not applied
        Assert.False(File.Exists(SettingsFile));
    }

    private const int Port = 18940;

    private static HttpRequest Req(string method, string? body, params (string, string)[] headers)
    {
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = $"127.0.0.1:{Port}" };
        foreach (var (k, v) in headers) h[k] = v;
        return new HttpRequest(method, NotificationSettingsApi.Route, new Dictionary<string, string>(), h, Encoding.UTF8.GetBytes(body ?? ""));
    }

    [Fact]
    public void The_api_reads_every_group()
    {
        var r = NotificationSettingsApi.Handle(Req("GET", null), Store(), Port);
        Assert.Equal(200, r.Status);
        Assert.Equal("""{"mains":true,"modeChanges":true,"lowBattery":true,"failures":true,"faults":true,"overTemperature":true}""", Encoding.UTF8.GetString(r.Body));
    }

    [Fact]
    public void The_api_switches_a_group_behind_the_write_guard()
    {
        var store = Store();
        Assert.Equal(403, NotificationSettingsApi.Handle(Req("PUT", """{ "mains": false }"""), store, Port).Status);
        Assert.Equal(403, NotificationSettingsApi.Handle(Req("PUT", """{ "mains": false }""", ("X-Station", "1"), ("Origin", "http://evil.example")), store, Port).Status);
        Assert.True(store.Current.Mains);
        var ok = NotificationSettingsApi.Handle(Req("PUT", """{ "mains": false }""", ("X-Station", "1")), store, Port);
        Assert.Equal(200, ok.Status);
        Assert.Contains("\"mains\":false", Encoding.UTF8.GetString(ok.Body), StringComparison.Ordinal);
        Assert.False(store.Current.Mains);
    }

    [Fact]
    public async Task The_station_api_serves_the_route_only_with_a_settings_store()
    {
        var cfg = new StationConfig { DataDir = _dir, Port = Port };
        var clock = new ManualClock(T0);
        using (var db = new BluettiWatcher.Storage.StationStore(Path.Combine(_dir, "station.db")))
        using (var service = new StationService(cfg, new QueueReader(), new FakeActions(), new FakeHibernator(true), new FixedFlag(Flag()), db, clock, _ => { }))
        {
            var with = new StationApi(cfg, service, db, clock, T0, notificationSettings: Store());
            var without = new StationApi(cfg, service, db, clock, T0);
            Assert.Equal(200, (await with.HandleAsync(Req("PUT", """{ "faults": false }""", ("X-Station", "1")), CancellationToken.None)).Status);
            Assert.Equal(404, (await without.HandleAsync(Req("GET", null), CancellationToken.None)).Status);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Assert.False(Store().Current.Faults);
    }

    [Fact]
    public void The_test_notice_shows_even_with_every_group_switched_off()
    {
        var store = Store();
        using (var doc = JsonDocument.Parse("""{ "mains": false, "modeChanges": false, "lowBattery": false, "failures": false, "faults": false, "overTemperature": false }"""))
            Assert.Null(store.Update(doc.RootElement));
        new TrayController(_view, store, () => TrayWords.En).SendTest();
        Assert.Equal("Test notification", Assert.Single(_view.Notices).Title);
    }

    private static HttpRequest TestReq(string method, params (string, string)[] headers)
    {
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = $"127.0.0.1:{Port}" };
        foreach (var (k, v) in headers) h[k] = v;
        return new HttpRequest(method, NotificationSettingsApi.TestRoute, new Dictionary<string, string>(), h, []);
    }

    [Fact]
    public void The_test_route_sends_once_behind_the_write_guard()
    {
        int sent = 0;
        void Send() => sent++;
        Assert.Equal(403, NotificationSettingsApi.HandleTest(TestReq("POST"), Send, Port).Status);
        Assert.Equal(403, NotificationSettingsApi.HandleTest(TestReq("POST", ("X-Station", "1"), ("Origin", "http://evil.example")), Send, Port).Status);
        Assert.Equal(405, NotificationSettingsApi.HandleTest(TestReq("GET", ("X-Station", "1")), Send, Port).Status);
        Assert.Equal(0, sent);
        Assert.Equal(200, NotificationSettingsApi.HandleTest(TestReq("POST", ("X-Station", "1")), Send, Port).Status);
        Assert.Equal(1, sent);
    }

    [Fact]
    public void The_test_route_without_a_tray_icon_says_so() =>
        Assert.Equal(503, NotificationSettingsApi.HandleTest(TestReq("POST", ("X-Station", "1")), null, Port).Status);

    [Fact]
    public async Task The_station_api_routes_the_test_to_its_sender()
    {
        var cfg = new StationConfig { DataDir = _dir, Port = Port };
        var clock = new ManualClock(T0);
        int sent = 0;
        using (var db = new BluettiWatcher.Storage.StationStore(Path.Combine(_dir, "station.db")))
        using (var service = new StationService(cfg, new QueueReader(), new FakeActions(), new FakeHibernator(true), new FixedFlag(Flag()), db, clock, _ => { }))
        {
            var api = new StationApi(cfg, service, db, clock, T0, notificationSettings: Store()) { SendTestNotice = () => sent++ };
            Assert.Equal(200, (await api.HandleAsync(TestReq("POST", ("X-Station", "1")), CancellationToken.None)).Status);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Assert.Equal(1, sent);
    }

    [Theory]
    [InlineData("PUT", "{ nope", 400)]
    [InlineData("PUT", """{ "mains": 1 }""", 400)]
    [InlineData("POST", """{ "mains": false }""", 405)]
    [InlineData("DELETE", null, 405)]
    public void The_api_refuses_bad_bodies_and_other_methods(string method, string? body, int status) =>
        Assert.Equal(status, NotificationSettingsApi.Handle(Req(method, body, ("X-Station", "1")), Store(), Port).Status);
}
