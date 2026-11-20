using BluettiWatcher.Decision;
using BluettiWatcher.Modes;
using BluettiWatcher.Storage;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests.Modes;

/// <summary>StationService with <c>modes.engine</c> = v2: the Auto rules switch the mode, the switch's eco never runs.</summary>
public sealed class ModesServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bw-modes-svc-").FullName;
    private readonly ManualClock _clock = new(T0);
    private readonly QueueReader _reader = new();
    private readonly FakeActions _actions = new();
    private readonly FakeMachine _m = new();
    private readonly FakeFlagWriter _flag = new();
    private readonly StationStore _store;
    private readonly ModesEngine _engine;
    private readonly StationService _service;

    public ModesServiceTests()
    {
        _store = new StationStore(Path.Combine(_dir, "station.db"));
        var cfg = new StationConfig { DataDir = _dir, MainsLostSec = 300, MainsBackSec = 300, PowerMode = new PowerModeConfig { FlagPath = Path.Combine(_dir, "flag", "power-mode.json") } };
        cfg.Modes.Engine = ModesConfig.V2;
        _engine = new ModesEngine(cfg, new ModuleRegistry([new FakeModule(BuiltInModes.PowerPlanType, _m)]), new ModesStore(_dir),
            new ModeJournal(Path.Combine(_dir, ModeJournal.FileName)), _flag, _clock, _ => { });
        _engine.Start(T0.AddDays(-1));
        _service = new StationService(cfg, _reader, _actions, new FakeHibernator(true), new FixedFlag(Flag()), _store, _clock, _ => { }) { Modes = _engine };
        _m.Values["plan"] = "high";
    }

    public void Dispose()
    {
        _service.Dispose();
        _engine.Dispose();
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private async Task Feed(params Reading[] readings)
    {
        foreach (var r in readings)
        {
            _reader.Next.Enqueue(r);
            await _service.PollAsync(TestContext.Current.CancellationToken);
            _clock.Now = _clock.Now.AddMinutes(2);
        }
    }

    [Fact]
    public async Task Poll_MainsOffPastMainsLostSec_EntersEcoWithoutTheSwitchEco()
    {
        await Feed(Grid(80), Bat(80), Bat(79), Bat(79), Bat(78));

        Assert.Equal("eco", _engine.Snapshot.ModeId);
        Assert.Equal("\"a1841308-3541-4fab-bc81-f71556f20b4a\"", _m.Text("plan"));
        Assert.DoesNotContain("eco", _actions.Calls);
        Assert.Contains(FlagWord.Eco, _flag.Written);
    }

    [Fact]
    public async Task Poll_MainsBackPastMainsBackSec_RestoresThePlanWithoutTheSwitchOff()
    {
        await Feed(Grid(80), Bat(80), Bat(79), Bat(79), Bat(78));
        await Feed(Grid(78), Grid(79), Grid(79), Grid(80));

        Assert.Equal("normal", _engine.Snapshot.ModeId);
        Assert.Equal("\"high\"", _m.Text("plan"));
        Assert.DoesNotContain("off", _actions.Calls);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Hibernate_AutoHibernateSetting_DecidesTheHibernation(bool autoHibernate, bool hibernates)
    {
        Assert.Equal((0, ""), await _engine.SetAutoHibernateAsync(autoHibernate, TestContext.Current.CancellationToken));
        var h = new FakeHibernator(true);
        var cfg = new StationConfig { DataDir = _dir, MainsLostSec = 300, MainsBackSec = 300 };
        using var s = new StationService(cfg, _reader, _actions, h, new FixedFlag(Flag()), _store, _clock, _ => { }) { Modes = _engine };
        _reader.Default = Bat(4);
        for (int i = 0; i < 15; i++)
        {
            await s.PollAsync(TestContext.Current.CancellationToken);
            _clock.Now = _clock.Now.AddMinutes(2);
        }
        Assert.Equal(hibernates, h.Calls > 0);
    }

    [Fact]
    public async Task Poll_AutoHibernateTurnedOnWhileTheLegacyFileExists_Hibernates()
    {
        // A 1.7.0 install that had no-hibernate: migrated to autoHibernate false, then the user turns it on in the page.
        await File.WriteAllTextAsync(Path.Combine(_dir, "no-hibernate"), "", TestContext.Current.CancellationToken);
        Assert.Equal((0, ""), await _engine.SetAutoHibernateAsync(true, TestContext.Current.CancellationToken));
        var h = new FakeHibernator(true);
        var cfg = new StationConfig { DataDir = _dir, MainsLostSec = 300, MainsBackSec = 300 };
        using var s = new StationService(cfg, _reader, _actions, h, new FixedFlag(Flag()), _store, _clock, _ => { }) { Modes = _engine };
        _reader.Default = Bat(4);
        for (int i = 0; i < 15; i++)
        {
            await s.PollAsync(TestContext.Current.CancellationToken);
            _clock.Now = _clock.Now.AddMinutes(2);
        }

        Assert.True(_engine.AutoHibernate);
        Assert.True(h.Calls > 0, "the API says autoHibernate true, the PC never hibernates");
    }

    [Fact]
    public void Start_LegacyNoHibernateFile_CarriedIntoTheSettingAndRenamed()
    {
        string sub = Directory.CreateDirectory(Path.Combine(_dir, "carry")).FullName;
        File.WriteAllText(Path.Combine(sub, "no-hibernate"), "");
        var cfg = new StationConfig { DataDir = sub };
        using var engine = new ModesEngine(cfg, new ModuleRegistry([new FakeModule(BuiltInModes.PowerPlanType, _m)]), new ModesStore(sub),
            new ModeJournal(Path.Combine(sub, ModeJournal.FileName)), _flag, _clock, _ => { });

        engine.Start(T0.AddDays(-1));

        Assert.False(engine.AutoHibernate);
        Assert.False(File.Exists(Path.Combine(sub, "no-hibernate")));
        Assert.True(File.Exists(Path.Combine(sub, "no-hibernate.migrated")));
        Assert.False(new ModesStore(sub).Load(() => new ModesSettings(), T0).Settings.AutoHibernate);
    }

    [Fact]
    public async Task Step_FlagFolderRefusedEveryPoll_AlertsOnceUntilItIsWritten()
    {
        var flag = new FakeFlagWriter { Fail = "the flag folder is not the user's" };
        var events = new List<string>();
        string sub = Directory.CreateDirectory(Path.Combine(_dir, "alert")).FullName;
        var cfg = new StationConfig { DataDir = sub };
        using var engine = new ModesEngine(cfg, new ModuleRegistry([new FakeModule(BuiltInModes.PowerPlanType, _m)]), new ModesStore(sub),
            new ModeJournal(Path.Combine(sub, ModeJournal.FileName)), flag, _clock, _ => { }, (k, _) => events.Add(k));
        engine.Start(T0.AddDays(-1));
        var input = new SelectorInput(true, TimeSpan.FromHours(1), 80, null, _clock.GetLocalNow());

        for (int i = 0; i < 5; i++) await engine.StepAsync(input, emergency: false, TestContext.Current.CancellationToken);
        flag.Fail = "";
        await engine.StepAsync(input, emergency: false, TestContext.Current.CancellationToken);
        flag.Fail = "the flag folder is not the user's";
        await engine.StepAsync(input, emergency: true, TestContext.Current.CancellationToken);
        await engine.StepAsync(input, emergency: false, TestContext.Current.CancellationToken);

        Assert.Equal(2, events.Count(e => e == "action-failed"));
        Assert.Equal([FlagWord.Normal], flag.Written);
    }

    [Fact]
    public async Task Start_ServiceRestartsInTheSameMode_NoModeChangedEvent()
    {
        string sub = Directory.CreateDirectory(Path.Combine(_dir, "restart")).FullName;
        var cfg = new StationConfig { DataDir = sub };
        var input = new SelectorInput(true, TimeSpan.FromHours(1), 80, null, _clock.GetLocalNow());
        var events = new List<string>();
        ModesEngine New() => new(cfg, new ModuleRegistry([new FakeModule(BuiltInModes.PowerPlanType, _m)]), new ModesStore(sub),
            new ModeJournal(Path.Combine(sub, ModeJournal.FileName)), new FakeFlagWriter(), _clock, _ => { }, (k, _) => events.Add(k));
        using (var first = New())
        {
            first.Start(T0.AddDays(-1));
            await first.StepAsync(input, emergency: false, TestContext.Current.CancellationToken);
        }
        Assert.Single(events, e => e == "mode-changed");
        events.Clear();

        using var second = New();
        second.Start(T0.AddDays(-1));
        await second.StepAsync(input, emergency: false, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("mode-changed", events);
    }

    // The notice is a banner of a few seconds: raised before the actions, it showed while a refresh-rate change had the
    // screens dark.
    [Fact]
    public async Task Select_ModeChange_TheEventComesAfterTheModesActionsRan()
    {
        string sub = Directory.CreateDirectory(Path.Combine(_dir, "after")).FullName;
        var cfg = new StationConfig { DataDir = sub };
        var planAtEvent = new List<string?>();
        using var engine = new ModesEngine(cfg, new ModuleRegistry([new FakeModule(BuiltInModes.PowerPlanType, _m)]), new ModesStore(sub),
            new ModeJournal(Path.Combine(sub, ModeJournal.FileName)), new FakeFlagWriter(), _clock, _ => { },
            (k, _) => { if (k == "mode-changed") planAtEvent.Add(_m.Text("plan")); });
        engine.Start(T0.AddDays(-1));
        await engine.StepAsync(new SelectorInput(true, TimeSpan.FromHours(1), 80, null, _clock.GetLocalNow()), emergency: false, TestContext.Current.CancellationToken);
        planAtEvent.Clear();

        await engine.SelectAsync(BuiltInModes.Eco, TestContext.Current.CancellationToken);
        await engine.Pending;

        Assert.Equal("\"a1841308-3541-4fab-bc81-f71556f20b4a\"", Assert.Single(planAtEvent));
    }

    private static List<Rule> CutRule() => [new() { Condition = new RuleCondition { Kind = ConditionKind.CutWithin, Minutes = 30 }, ModeId = BuiltInModes.Eco }];

    [Fact]
    public async Task Poll_CutWithinRuleAndANearCut_EntersEcoOnMains()
    {
        Assert.Equal((0, ""), await _engine.PutRulesAsync(CutRule(), TestContext.Current.CancellationToken));
        _service.NextCut = () => _clock.Now.AddMinutes(20);

        await Feed(Grid(80));

        Assert.Equal("eco", _engine.Snapshot.ModeId);
    }

    [Fact]
    public async Task Poll_CutWithinRuleAndAFailingSchedule_StaysNormal()
    {
        Assert.Equal((0, ""), await _engine.PutRulesAsync(CutRule(), TestContext.Current.CancellationToken));
        _service.NextCut = () => throw new InvalidOperationException("schedule module down");

        await Feed(Grid(80));

        Assert.Equal("normal", _engine.Snapshot.ModeId);
    }

    [Theory]
    [InlineData(false, 20.0, 20.0)]
    [InlineData(false, -5.0, 0.0)]
    [InlineData(true, 90.0, 0.0)]
    [InlineData(false, null, null)]
    public void MinutesToCut_ScheduleAndCut_AreMinutesOrUnknown(bool cutOn, double? inMinutes, double? expected) =>
        Assert.Equal(expected, StationService.MinutesToCut(cutOn, inMinutes is { } m ? T0.AddMinutes(m) : null, T0));
}