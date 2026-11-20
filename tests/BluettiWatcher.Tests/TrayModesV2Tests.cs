using BluettiWatcher.Decision;
using BluettiWatcher.Modes;
using BluettiWatcher.Tests.Modes;
using BluettiWatcher.Tray;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

/// <summary>The tray over the v2 modes engine, and the station alarms the tray is fed each reading.</summary>
public sealed class TrayModesV2Tests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bw-tray-v2-").FullName;
    private readonly ModesEngine _engine;

    public TrayModesV2Tests()
    {
        var cfg = new StationConfig { DataDir = _dir, PowerMode = new PowerModeConfig { FlagPath = Path.Combine(_dir, "flag", "power-mode.json") } };
        _engine = new ModesEngine(cfg, new ModuleRegistry([new FakeModule(BuiltInModes.PowerPlanType, new FakeMachine())]), new ModesStore(_dir),
            new ModeJournal(Path.Combine(_dir, ModeJournal.FileName)), new FakeFlagWriter(), new ManualClock(T0), _ => { });
        _engine.Start(T0.AddDays(-1));
    }

    public void Dispose()
    {
        _engine.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Modes_ListEveryModeOfTheSettings()
    {
        var tray = new ModesTrayModes(_engine);
        Assert.Equal(["normal", "eco", "max"], tray.Modes.Select(m => m.Id));
        Assert.Null(tray.Current);
    }

    [Fact]
    public async Task SelectAsync_AModeThenAuto_HoldsItThenFreesIt()
    {
        var tray = new ModesTrayModes(_engine);

        await tray.SelectAsync("max", TestContext.Current.CancellationToken);
        Assert.Equal("max", tray.Current);

        await tray.SelectAsync(null, TestContext.Current.CancellationToken);
        Assert.Null(tray.Current);
        Assert.Equal(SelectionKind.Auto, _engine.Settings.Selection.Kind);
    }

    [Fact]
    public void ListOf_StationAlarmState_IsTheWholeActiveList()
    {
        var list = StationAlarm.ListOf(new StationAlarmState(["W1"], ["F7", "F8"], Hot: true, TempMax: 63));

        Assert.Equal(["fault:F7", "fault:F8", "warning:W1", "temperature"], list.Select(a => a.Code));
        Assert.Equal(StationAlarmKind.OverTemperature, list[^1].Kind);
        Assert.Contains("63 °C", list[^1].Text, StringComparison.Ordinal);
        Assert.Empty(StationAlarm.ListOf(new StationAlarmState([], [], Hot: false, TempMax: 30)));
    }

    [Fact]
    public void FromEvent_ModeChanged_IsAModeChangeNotice()
    {
        var n = TrayNotices.FromEvent("mode-changed", "eco (rule 1: mains off for 300 s)", 50);
        Assert.NotNull(n);
        Assert.Equal(NotificationKind.ModeChanges, n.Kind);
    }
}
