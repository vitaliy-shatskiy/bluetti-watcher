using BluettiWatcher.Modes;
using BluettiWatcher.Storage;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests.Modes;

/// <summary>A station emergency left in the flag is lifted once mains is back, on the legacy switch and under the v2 modes.</summary>
public sealed class ModesEmergencyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bw-modes-emerg-").FullName;
    private readonly ManualClock _clock = new(T0);
    private readonly QueueReader _reader = new();
    private readonly FakeActions _actions = new();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private StationConfig Config() => new()
    {
        DataDir = _dir, MainsLostSec = 300, MainsBackSec = 300,
        PowerMode = new PowerModeConfig { FlagPath = Path.Combine(_dir, "flag", "power-mode.json") },
    };

    private async Task FeedMainsAsync(StationService service)
    {
        foreach (var r in new[] { Grid(30), Grid(31), Grid(32), Grid(33), Grid(34), Grid(35) })
        {
            _reader.Next.Enqueue(r);
            await service.PollAsync(TestContext.Current.CancellationToken);
            _clock.Now = _clock.Now.AddMinutes(2);
        }
    }

    [Fact]
    public async Task Poll_StationEmergencyFlagAndMainsBackUnderV2_WritesTheModesFlagWord()
    {
        var m = new FakeMachine();
        var flag = new FakeFlagWriter();
        using var store = new StationStore(Path.Combine(_dir, "station.db"));
        var cfg = Config();
        cfg.Modes.Engine = ModesConfig.V2;
        using var engine = new ModesEngine(cfg, new ModuleRegistry([new FakeModule(BuiltInModes.PowerPlanType, m)]), new ModesStore(_dir),
            new ModeJournal(Path.Combine(_dir, ModeJournal.FileName)), flag, _clock, _ => { });
        engine.Start(T0.AddDays(-1));
        using var service = new StationService(cfg, _reader, _actions, new FakeHibernator(true), new FixedFlag(Flag("emergency", "station")), store, _clock, _ => { }) { Modes = engine };
        m.Values["plan"] = "high";

        await FeedMainsAsync(service);

        Assert.Contains(FlagWord.Normal, flag.Written);
        Assert.False(engine.Emergency);
    }

    // Positive control: the same feed on the legacy switch runs power-mode off.
    [Fact]
    public async Task Poll_StationEmergencyFlagAndMainsBackOnLegacy_RunsTheSwitchOff()
    {
        using var store = new StationStore(Path.Combine(_dir, "station.db"));
        using var service = new StationService(Config(), _reader, _actions, new FakeHibernator(true), new FixedFlag(Flag("emergency", "station")), store, _clock, _ => { });

        await FeedMainsAsync(service);

        Assert.Contains("off", _actions.Calls);
    }
}
