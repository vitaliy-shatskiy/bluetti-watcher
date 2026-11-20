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
}