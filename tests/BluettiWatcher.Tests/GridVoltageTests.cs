using System.Text.Json;
using BluettiWatcher.Decision;
using BluettiWatcher.Http;
using BluettiWatcher.Io;
using BluettiWatcher.Io.Ble;
using BluettiWatcher.Storage;
using Microsoft.Data.Sqlite;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

public class GridRegisterTests
{
    /// <summary>
    /// An AC200PL in bypass as the official app read it (70+87): 71 = 223 V out, 73/76/79 = 263 W, 74 = 500, 75 = 137,
    /// 77 = 2246 (224.6 V grid in), 80 = 500 (50.0 Hz grid in); 70 and 36..43 from the reader fixture.
    /// </summary>
    internal static readonly Dictionary<int, ushort> Captured = new()
    {
        [36] = 120, [37] = 0, [38] = 345, [39] = 12, [43] = 85, [70] = 1,
        [71] = 223, [73] = 263, [74] = 500, [75] = 137, [76] = 263, [77] = 2246, [79] = 263, [80] = 500,
    };

    internal static ushort[] Regs(int start, int quantity) => [.. Enumerable.Range(start, quantity).Select(a => Captured.GetValueOrDefault(a))];

    private static NativeBleReader Reader(IBleConnector c, List<string> log, string type = "AC200PL") =>
        new(new StationConfig { Mac = "AA:BB:CC:DD:EE:FF", Type = type, ReaderTimeoutSec = 10 }, c, log.Add, TimeSpan.FromMilliseconds(300));

    [Fact]
    public void Decode_Captured70Plus11Answer_GridVoltageAndFrequencyFromRegisters77And80()
    {
        var answer = new ResponseAssembler(ModbusRead.Request(70, 11));
        foreach (var chunk in ModbusFrameTests.Answer(Regs(70, 11)).Chunk(20)) answer.Feed(chunk);
        Assert.Equal(AssemblyState.Complete, answer.State);
        var v = V1Models.Decode(V1Models.All["AC200PL"], 70, answer.Body);
        Assert.Equal(224.6, v["GRID_INPUT_VOLTAGE"].Number);
        Assert.Equal(50.0, v["GRID_INPUT_FREQUENCY"].Number);
        Assert.Equal(223, v["INTERNAL_AC_VOLTAGE"].Number);   // volt stays the AC output
        Assert.Equal(50, v["INTERNAL_AC_FREQUENCY"].Number);
    }

    /// <summary>READ ONLY, FOREVER: the grid registers ride in the 70.. batch, one function-0x03 request under 2000.</summary>
    [Fact]
    public void Plan_Ac200pl_ReadsRegisters70To80InOneRequest()
    {
        var plan = BleModels.TryGet("AC200PL", out var model) ? model.Plan : [];
        Assert.Equal([(36, 8), (70, 11)], plan.Select(r => (r.Start, r.Quantity)));
        Assert.Equal("01030046000B", Convert.ToHexString(ModbusRead.Request(70, 11).Frame.ToArray()[..6]));
        Assert.All(plan, r => Assert.True(r.Start + r.Quantity <= ModbusRead.ControlRegisterFloor));
    }

    /// <summary>77 and 80 are proven on the AC200PL only: the AC200L and AC200M read as before, no grid field.</summary>
    [Theory]
    [InlineData("AC200L")]
    [InlineData("AC200M")]
    public void Plan_OtherAc200Models_Unchanged(string type)
    {
        Assert.True(BleModels.TryGet(type, out var model));
        Assert.Contains(new RegisterRange(70, 5), model.Plan);
        Assert.DoesNotContain(V1Models.All[type], f => f.Name.StartsWith("GRID_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadAsync_CapturedAnswers_ReadingCarriesGridVoltAndHzOnly()
    {
        await using var link = new ScriptedLink(r => ModbusFrameTests.Answer(Regs(r.Start, r.Quantity)).Chunk(20));
        var log = new List<string>();
        var r = await Reader(new ScriptedConnector(() => new BleOpen(link, null)), log).ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new Reading(85, 0, 345, "INVERTER_OUTPUT", 120, 12, 223, 50, GridVolt: 224.6, GridHz: 50.0), r);
        Assert.Null(r!.GridAmp);   // 73/75/76 are not proven grid current or power
        Assert.Null(r.GridW);
        Assert.Empty(log);
    }

    /// <summary>
    /// The reading ends with its last planned range: no optional request follows that could stall past the hard bound.
    /// The link answers the plan only; anything else written would hang until the hard timeout and lose the reading.
    /// </summary>
    [Fact]
    public async Task ReadAsync_ReadingComplete_NoRequestAfterTheLastPlannedRange()
    {
        Assert.True(BleModels.TryGet("AC200PL", out var model));
        await using var link = new ScriptedLink(r => model.Plan.Contains(new RegisterRange(r.Start, r.Quantity))
            ? [ModbusFrameTests.Answer(Regs(r.Start, r.Quantity))] : []);
        var reader = new NativeBleReader(new StationConfig { Mac = "AA:BB:CC:DD:EE:FF", Type = "AC200PL", ReaderTimeoutSec = 1 },
            new ScriptedConnector(() => new BleOpen(link, null)), _ => { }, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(200));
        var r = await reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(224.6, r?.GridVolt);
        Assert.Equal(model.Plan, link.Written.Select(w => new RegisterRange((w[2] << 8) | w[3], (w[4] << 8) | w[5])));
    }

    [Fact]
    public void GridHighName_IsOneConstantInTheSource()
    {
        var src = Path.Join(HookPowerModeTests.Repo, "src", "BluettiWatcher");
        var hits = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
            .Sum(f => File.ReadAllText(f).Split("\"grid-voltage-high\"").Length - 1);
        Assert.Equal(1, hits);
        Assert.Equal("grid-voltage-high", StateActions.GridHigh);
    }
}

public class GridVoltageAlarmTests
{
    [Fact]
    public void Step_AtTheLine_DoesNotFire() => Assert.Equal((false, false), GridVoltageAlarm.Step(false, 253.0));

    [Fact]
    public void Step_CrossingUp_FiresOnceAndHoldsWhileHovering()
    {
        bool high = false;
        var fired = new List<double>();
        foreach (double v in (double[])[252.0, 253.1, 255.0, 252.0, 253.5, 250.0, 254.0])
        {
            (high, bool fire) = GridVoltageAlarm.Step(high, v);
            if (fire) fired.Add(v);
        }
        Assert.Equal([253.1], fired);
        Assert.True(high);
    }

    [Fact]
    public void Step_UnderTheRearmLine_FiresAgainOnTheNextCrossing()
    {
        var (high, _) = GridVoltageAlarm.Step(true, 249.9);
        Assert.False(high);
        Assert.Equal((true, true), GridVoltageAlarm.Step(high, 253.2));
    }

    [Fact]
    public void Step_NoVoltage_ChangesNothing()
    {
        Assert.Equal((true, false), GridVoltageAlarm.Step(true, null));
        Assert.Equal((false, false), GridVoltageAlarm.Step(false, null));
    }
}

public sealed class GridVoltageServiceTests : IAsyncLifetime, IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-watcher-test-" + Guid.NewGuid().ToString("N"));
    private readonly StationStore _store;
    private readonly ManualClock _clock = new(T0);
    private readonly QueueReader _reader = new();
    private readonly FakeLineRunner _lines = new();
    private readonly List<string> _log = [];
    private readonly StationConfig _cfg;
    private readonly StateActions _onState;

    public GridVoltageServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new StationStore(Path.Combine(_dir, "station.db"));
        _cfg = new StationConfig { DataDir = _dir, Actions = new() { [StateActions.GridHigh] = "grid-cmd", ["eco"] = "eco-cmd" } };
        _onState = new StateActions(_cfg, _lines, _store, _clock, _ => { });
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _onState.IdleAsync();
        Dispose();
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static Reading Grid(double? volt) => new(80, 300, 220, "BYPASS_OUTPUT_C", GridVolt: volt, GridHz: 50.0, GridAmp: 1.4, GridW: 320);

    private StationService Service() => new(_cfg, _reader, new FakeActions(), new FakeHibernator(true), new FixedFlag(Station), _store, _clock, _log.Add, null, _onState);

    private async Task Feed(StationService s, params double?[] volts)
    {
        foreach (var v in volts)
        {
            _reader.Next.Enqueue(Grid(v));
            await s.PollAsync(TestContext.Current.CancellationToken);
            _clock.Now = _clock.Now.AddMinutes(1);
        }
    }

    private List<EventRow> HighEvents() => [.. _store.Events(0, long.MaxValue).Where(e => e.Kind == StateActions.GridHigh)];

    [Fact]
    public async Task PollAsync_VoltageCrossesUpAndHovers_OneEventOneActionThenRearms()
    {
        using var s = Service();
        await Feed(s, 240.0, 253.4, 256.0, null, 252.0, 253.6, 249.5, 254.2);
        await _onState.IdleAsync();
        var events = HighEvents();
        Assert.Equal(["grid input 253.4 V, above 253 V", "grid input 254.2 V, above 253 V"], events.Select(e => e.Detail));
        Assert.Equal(T0.AddMinutes(1).ToUnixTimeSeconds(), events[0].At);
        Assert.Equal(["grid-cmd", "grid-cmd"], _lines.Lines);
        Assert.Single(_log, l => l.Contains("re-armed", StringComparison.Ordinal));
    }

    /// <summary>A rejected reading (here no output mode) neither fires nor re-arms the alert.</summary>
    [Fact]
    public async Task PollAsync_RejectedReading_IsIgnoredByTheAlert()
    {
        using var s = Service();
        _reader.Next.Enqueue(Grid(260.0) with { Mode = null });
        await s.PollAsync(TestContext.Current.CancellationToken);
        Assert.Empty(HighEvents());
        await Feed(s, 255.0);
        _reader.Next.Enqueue(Grid(240.0) with { Mode = null });
        await s.PollAsync(TestContext.Current.CancellationToken);
        await Feed(s, 256.0);
        await _onState.IdleAsync();
        Assert.Equal(["grid input 255 V, above 253 V"], HighEvents().Select(e => e.Detail));
        Assert.Contains(_log, l => l.StartsWith("reading rejected", StringComparison.Ordinal));
        Assert.DoesNotContain(_log, l => l.Contains("re-armed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PollAsync_AlertFired_SurvivesARestartWithoutFiringAgain()
    {
        using (var s = Service()) await Feed(s, 255.0);
        using (var again = Service()) await Feed(again, 255.0, 254.0);
        Assert.Single(HighEvents());
    }

    [Fact]
    public async Task PollAsync_GridFields_AreStoredPerReadingAndShownInApiNow()
    {
        using var s = Service();
        await Feed(s, 231.5);
        var ct = TestContext.Current.CancellationToken;
        await using (var db = new SqliteConnection($"Data Source={Path.Combine(_dir, "station.db")};Pooling=False"))
        {
            await db.OpenAsync(ct);
            await using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT grid_volt, grid_hz, grid_amp, grid_w FROM reading";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            Assert.True(await r.ReadAsync(ct));
            Assert.Equal((231.5, 50.0, 1.4, 320.0), (r.GetDouble(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3)));
        }
        var api = new StationApi(_cfg, s, _store, _clock, T0);
        var host = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = $"127.0.0.1:{_cfg.Port}" };
        var resp = await api.HandleAsync(new HttpRequest("GET", "/api/now", new Dictionary<string, string>(), host, []), ct);
        Assert.Equal(200, resp.Status);
        using var json = JsonDocument.Parse(resp.Body);
        var last = json.RootElement.GetProperty("last");
        Assert.Equal(231.5, last.GetProperty("gridVolt").GetDouble());
        Assert.Equal(50.0, last.GetProperty("gridHz").GetDouble());
        Assert.Equal(1.4, last.GetProperty("gridAmp").GetDouble());
        Assert.Equal(320, last.GetProperty("gridW").GetDouble());
    }

    /// <summary>A database made before the grid columns gets them on open; its rows keep NULL.</summary>
    [Fact]
    public void Open_OldReadingTable_AddsTheGridColumns()
    {
        string path = Path.Combine(_dir, "old.db");
        using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE reading(at INTEGER NOT NULL, status INTEGER NOT NULL, soc REAL, in_ac REAL, in_dc REAL,
                  out_ac REAL, out_dc REAL, volt REAL, hz REAL, mode TEXT, reason TEXT);
                INSERT INTO reading(at, status, soc) VALUES (1, 0, 50);
                """;
            cmd.ExecuteNonQuery();
        }
        using (var store = new StationStore(path)) store.AddReading(T0, Grid(240.0), ReadingStatus.Accepted, "");
        using (var store = new StationStore(path)) Assert.Equal(2, store.Counts().Raw);   // opening again adds nothing twice
        using var check = new SqliteConnection($"Data Source={path};Pooling=False");
        check.Open();
        using var q = check.CreateCommand();
        q.CommandText = "SELECT grid_volt FROM reading ORDER BY at";
        using var r = q.ExecuteReader();
        Assert.True(r.Read());
        Assert.True(r.IsDBNull(0));
        Assert.True(r.Read());
        Assert.Equal(240.0, r.GetDouble(0));
    }
}
