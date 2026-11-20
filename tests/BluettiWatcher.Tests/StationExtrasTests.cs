using System.Text.Json;
using BluettiWatcher.Decision;
using BluettiWatcher.Http;
using BluettiWatcher.Io;
using BluettiWatcher.Io.Ble;
using BluettiWatcher.Storage;
using Microsoft.Data.Sqlite;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

/// <summary>V2 registers 100-105, 122-138 and 1151-1153 (docs/bluetti-protocol.md, sections 2.5 and 3), synthetic answers.</summary>
public class V2ExtrasDecodeTests
{
    private static byte[] Body(params ushort[] words) => [.. words.SelectMany(w => new[] { unchecked((byte)(w >> 8)), unchecked((byte)w) })];

    /// <summary>A 122+17 body: 122 = invPowerType (low byte), 126-129 warnings, 133-138 faults.</summary>
    internal static byte[] AlarmBody(byte powerType = 0, ushort warn1 = 0, ushort warn2 = 0, ushort fault1 = 0, ushort fault3 = 0)
    {
        var w = new ushort[17];
        w[0] = powerType;
        w[4] = warn1;
        w[5] = warn2;
        w[11] = fault1;
        w[13] = fault3;
        return Body(w);
    }

    [Fact]
    public void Decode_BatteryAndStationTime_ScaledAsTheApp()
    {
        var x = V2Extras.Decode(new Dictionary<string, byte[]> { [V2Extras.Battery] = Body(528, 153), [V2Extras.StationTime] = Body(0, 245) });
        Assert.Equal(52.8, x?.PackVolt);
        Assert.Equal(15.3, x?.PackAmp);
        Assert.Equal(0, x?.StationMinutesToFull);
        Assert.Equal(245, x?.StationMinutesToEmpty);
        Assert.Null(x?.Warnings);
        Assert.Null(x?.TempMax);
    }

    [Fact]
    public void DecodeAlarms_LowPowerStation_WarningsFrom113FaultsFrom1PrefixE()
    {
        var (warnings, faults) = V2Extras.DecodeAlarms(AlarmBody(warn1: 0b101, warn2: 1, fault1: 1 << 15, fault3: 1));
        Assert.Equal(["E113", "E115", "E129"], warnings);
        Assert.Equal(["E16", "E33"], faults);
    }

    [Fact]
    public void DecodeAlarms_HighPowerStation_PrefixBWarningsFrom97()
    {
        var (warnings, faults) = V2Extras.DecodeAlarms(AlarmBody(powerType: 3, warn1: 1, fault1: 2));
        Assert.Equal(["B97"], warnings);
        Assert.Equal(["B2"], faults);
    }

    [Fact]
    public void Decode_Temperatures_MinusFortyAndZeroIsNoSensor()
    {
        var x = V2Extras.Decode(new Dictionary<string, byte[]> { [V2Extras.Temperatures] = Body(0, 112, 65) });
        Assert.Null(x?.TempAmbient);
        Assert.Equal(72, x?.TempInverter);
        Assert.Equal(25, x?.TempPvDcDc);
        Assert.Equal(72, x?.TempMax);
    }

    [Fact]
    public void Decode_NothingAnswered_IsNull() => Assert.Null(V2Extras.Decode(new Dictionary<string, byte[]>()));

    /// <summary>READ ONLY, FOREVER: every extra is a 0x03 read under the control registers.</summary>
    [Fact]
    public void Blocks_AllUnderTheControlRegisters() =>
        Assert.All(V2Extras.Blocks, b => Assert.True(b.Start + b.Quantity <= ModbusRead.ControlRegisterFloor));
}

public class V2ExtrasReaderTests
{
    private static readonly Dictionary<int, ushort> Values = new()
    {
        [102] = 64, [140] = 12, [142] = 345, [144] = 120, [146] = 7,
        [100] = 528, [101] = 153, [105] = 245, [122] = 0, [126] = 1, [133] = 4, [1151] = 0, [1152] = 112, [1153] = 65,
    };

    private static IEnumerable<byte[]> Station(ReadRequest r) =>
        ModbusFrameTests.Answer([.. Enumerable.Range(r.Start, r.Quantity).Select(a => Values.GetValueOrDefault(a))]).Chunk(20);

    private static NativeBleReader Reader(IBleConnector c, List<string> log, string type = "AC180") =>
        new(new StationConfig { Mac = "AA:BB:CC:DD:EE:FF", Type = type, ReaderTimeoutSec = 10 }, c, log.Add, TimeSpan.FromMilliseconds(300));

    private static (int, int) Range(byte[] w) => ((w[2] << 8) | w[3], (w[4] << 8) | w[5]);

    [Fact]
    public async Task ReadAsync_V2Station_ReadingCarriesTheExtrasAfterItsOwnReads()
    {
        await using var link = new ScriptedLink(Station);
        var log = new List<string>();
        var reader = Reader(new ScriptedConnector(() => new BleOpen(link, null)), log);
        var r = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal(64, r?.Soc);
        Assert.Equal(new StationExtras(52.8, 15.3, 0, 245, ["E113"], ["E3"], null, 72, 25), r?.Extras);
        Assert.Equal([(102, 1), (140, 7), (100, 2), (104, 2), (122, 17), (1151, 3)], link.Written.Select(Range));
        Assert.All(link.Written, w => Assert.Equal(0x03, w[1]));
        Assert.All(reader.StationBlocks(), b => Assert.Equal("supported", b.Value));
        Assert.Empty(log);
    }

    /// <summary>An exception 0x02 marks its block unsupported: the reading stands, and the block is not asked again.</summary>
    [Fact]
    public async Task ReadAsync_BlockRefused_ReadingStandsAndTheBlockIsNotAskedAgain()
    {
        await using var link = new ScriptedLink(r => r.Start == 1151 ? [ModbusFrameTests.ExceptionAnswer(0x02)] : Station(r));
        var log = new List<string>();
        var reader = Reader(new ScriptedConnector(() => new BleOpen(link, null)), log);
        var first = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal(64, first?.Soc);
        Assert.Null(first?.Extras?.TempMax);
        Assert.Equal(245, first?.Extras?.StationMinutesToEmpty);
        var second = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal(64, second?.Soc);
        Assert.Single(link.Written, w => Range(w) == (1151, 3));
        Assert.StartsWith("not supported: registers 1151+3", reader.StationBlocks().Single(b => b.Key == V2Extras.Temperatures).Value, StringComparison.Ordinal);
        Assert.Contains("station block temperatures not supported", Assert.Single(log), StringComparison.Ordinal);
    }

    /// <summary>
    /// A silent block is marked not supported after two polls in a row without its answer (a dropped link is not proof);
    /// it never fails the reading, and later polls skip it.
    /// </summary>
    [Fact]
    public async Task ReadAsync_BlockNeverAnswered_MarkedAfterTwoMissesInARowThenSkipped()
    {
        await using var link = new ScriptedLink(r => r.Start == 122 ? [] : Station(r));
        var conn = new ScriptedConnector(() => new BleOpen(link, null));
        var reader = Reader(conn, []);
        Assert.Equal(64, (await reader.ReadAsync(CancellationToken.None))?.Soc);
        Assert.Equal("not asked yet", reader.StationBlocks().Single(b => b.Key == V2Extras.Alarms).Value);
        Assert.Equal(64, (await reader.ReadAsync(CancellationToken.None))?.Soc);
        var third = await reader.ReadAsync(CancellationToken.None);
        Assert.Equal(64, third?.Soc);
        Assert.Equal(52.8, third?.Extras?.PackVolt);
        Assert.Equal(2, link.Written.Count(w => Range(w) == (122, 17)));
        Assert.StartsWith("not supported: registers 122+17", reader.StationBlocks().Single(b => b.Key == V2Extras.Alarms).Value, StringComparison.Ordinal);
        Assert.Equal(3, conn.Opens);   // one per poll: the timed-out block dropped its poll's link and no extra opens another
    }

    /// <summary>Misses count only in a row: an answer between two misses keeps the block supported.</summary>
    [Fact]
    public async Task ReadAsync_MissAnswerMiss_BlockStaysSupported()
    {
        int asked = 0;
        await using var link = new ScriptedLink(r => r.Start == 122 && asked++ != 1 ? [] : Station(r));
        var reader = Reader(new ScriptedConnector(() => new BleOpen(link, null)), []);
        for (int i = 0; i < 4; i++) Assert.Equal(64, (await reader.ReadAsync(CancellationToken.None))?.Soc);
        Assert.Equal(4, link.Written.Count(w => Range(w) == (122, 17)));
    }

    /// <summary>On the AC200PL (V1) 100-138 are other data (cell voltages): never asked, reported as not on this protocol.</summary>
    [Fact]
    public async Task ReadAsync_V1Station_AsksNoExtraAndSaysWhy()
    {
        await using var link = new ScriptedLink(r => ModbusFrameTests.Answer(GridRegisterTests.Regs(r.Start, r.Quantity)).Chunk(20));
        var reader = Reader(new ScriptedConnector(() => new BleOpen(link, null)), [], "AC200PL");
        var r = await reader.ReadAsync(CancellationToken.None);
        Assert.Null(r?.Extras);
        Assert.Equal([(36, 8), (70, 11)], link.Written.Select(Range));
        Assert.All(reader.StationBlocks(), b => Assert.Equal("not on this protocol (V1)", b.Value));
        Assert.Equal(V2Extras.Blocks.Select(b => b.Name), reader.StationBlocks().Select(b => b.Key));
    }
}

public class StationAlarmsTests
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.Ordinal);

    private static StationExtras Codes(string[] warnings, string[] faults) => new(Warnings: warnings, Faults: faults);

    [Fact]
    public void Step_NewFaultAndWarning_OneEventEach()
    {
        var (active, _, events) = StationAlarms.Step(None, false, Codes(["E113"], ["E3"]), 60);
        Assert.Equal([new AlarmEvent(StationAlarms.Warning, "station warning E113"), new AlarmEvent(StationAlarms.Fault, "station fault E3")], events);
        Assert.True(active.SetEquals(["E113", "E3"]));
    }

    [Fact]
    public void Step_SameCodesAgain_NoEvent()
    {
        var (active, _, _) = StationAlarms.Step(None, false, Codes([], ["E3"]), 60);
        Assert.Empty(StationAlarms.Step(active, false, Codes([], ["E3"]), 60).Events);
    }

    [Fact]
    public void Step_CodeGone_ClearedEvent()
    {
        var (active, _, _) = StationAlarms.Step(None, false, Codes([], ["E3"]), 60);
        Assert.Equal([new AlarmEvent(StationAlarms.Cleared, "station code E3 cleared")], StationAlarms.Step(active, false, Codes([], []), 60).Events);
    }

    /// <summary>A poll without the alarms block (not read, refused) is no evidence the codes cleared.</summary>
    [Fact]
    public void Step_AlarmsNotRead_KeepsTheActiveCodes()
    {
        var (active, _, _) = StationAlarms.Step(None, false, Codes([], ["E3"]), 60);
        var (after, _, events) = StationAlarms.Step(active, false, new StationExtras(PackVolt: 52), 60);
        Assert.Empty(events);
        Assert.Contains("E3", after);
        Assert.Empty(StationAlarms.Step(after, false, null, 60).Events);
    }

    [Fact]
    public void Step_TemperatureCrossesAndHovers_OneEventThenRearmsFiveUnder()
    {
        bool hot = false;
        var fired = new List<double>();
        foreach (double t in (double[])[58, 61, 63, 59, 61, 54, 62])
        {
            (_, hot, var events) = StationAlarms.Step(None, hot, new StationExtras(TempInverter: t), 60);
            if (events.Count > 0) fired.Add(t);
        }
        Assert.Equal([61, 62], fired);
    }

    [Fact]
    public void Step_AtTheLimit_DoesNotFire() => Assert.Empty(StationAlarms.Step(None, false, new StationExtras(TempAmbient: 60), 60).Events);
}

public sealed class StationExtrasServiceTests : IAsyncLifetime, IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-watcher-test-" + Guid.NewGuid().ToString("N"));
    private readonly StationStore _store;
    private readonly ManualClock _clock = new(T0);
    private readonly QueueReader _reader = new();
    private readonly FakeLineRunner _lines = new();
    private readonly StationConfig _cfg;
    private readonly StateActions _onState;

    public StationExtrasServiceTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new StationStore(Path.Combine(_dir, "station.db"));
        _cfg = new StationConfig { DataDir = _dir, StationTempHighC = 50, Actions = new() { [StationAlarms.Fault] = "fault-cmd" } };
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

    private static readonly StationExtras Extras = new(52.8, 15.3, 0, 245, [], ["E3"], 30, 55, 25);
    private static Reading WithExtras(StationExtras? x) => new(80, 300, 220, "BYPASS_OUTPUT_C", Extras: x);

    private StationService Service() => new(_cfg, _reader, new FakeActions(), new FakeHibernator(true), new FixedFlag(Station), _store, _clock, _ => { }, null, _onState);

    [Fact]
    public async Task PollAsync_NewFaultAndHotStation_EventRowsAndTheFaultAction()
    {
        using var s = Service();
        foreach (var x in (StationExtras?[])[Extras, Extras, Extras with { Faults = [] }])
        {
            _reader.Next.Enqueue(WithExtras(x));
            await s.PollAsync(TestContext.Current.CancellationToken);
            _clock.Now = _clock.Now.AddMinutes(1);
        }
        await _onState.IdleAsync();
        var events = _store.Events(0, long.MaxValue).Where(e => e.Kind.StartsWith("station-", StringComparison.Ordinal))
            .OrderBy(e => e.At).ThenBy(e => e.Kind, StringComparer.Ordinal).Select(e => (e.Kind, e.Detail));
        Assert.Equal([(StationAlarms.Fault, "station fault E3"), (StationAlarms.Hot, "station temperature 55 °C, above 50 °C"), (StationAlarms.Cleared, "station code E3 cleared")], events);
        Assert.Equal(["fault-cmd"], _lines.Lines);
    }

    [Fact]
    public async Task PollAsync_Extras_StoredPerReadingAndShownInApiNowAndHealth()
    {
        using var s = Service();
        _reader.Next.Enqueue(WithExtras(Extras));
        var ct = TestContext.Current.CancellationToken;
        await s.PollAsync(ct);
        await using (var db = new SqliteConnection($"Data Source={Path.Combine(_dir, "station.db")};Pooling=False"))
        {
            await db.OpenAsync(ct);
            await using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT pack_volt, pack_amp, station_min_full, station_min_empty, temp_ambient, temp_inverter, temp_pv FROM reading";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            Assert.True(await r.ReadAsync(ct));
            Assert.Equal([52.8, 15.3, 0, 245, 30, 55, 25], Enumerable.Range(0, 7).Select(r.GetDouble));
        }
        var api = new StationApi(_cfg, s, _store, _clock, T0, stationBlocks: () => [KeyValuePair.Create(V2Extras.Alarms, "not supported: registers 122+17, exception 0x02")]);
        var host = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = $"127.0.0.1:{_cfg.Port}" };
        var now = await api.HandleAsync(new HttpRequest("GET", "/api/now", new Dictionary<string, string>(), host, []), ct);
        using (var json = JsonDocument.Parse(now.Body))
        {
            var station = json.RootElement.GetProperty("last").GetProperty("station");
            Assert.Equal(52.8, station.GetProperty("packVolt").GetDouble());
            Assert.Equal(245, station.GetProperty("minutesToEmpty").GetDouble());
            Assert.Equal("E3", station.GetProperty("faults")[0].GetString());
            Assert.Equal(55, station.GetProperty("tempInverter").GetDouble());
            Assert.True(station.TryGetProperty("emptyGapHours", out _));
        }
        var health = await api.HandleAsync(new HttpRequest("GET", "/api/health", new Dictionary<string, string>(), host, []), ct);
        using var h = JsonDocument.Parse(health.Body);
        Assert.Equal("not supported: registers 122+17, exception 0x02", h.RootElement.GetProperty("stationBlocks").GetProperty(V2Extras.Alarms).GetString());
    }

    /// <summary>A database from before the station columns (grid columns already there) gets them on open; its rows stay as they were.</summary>
    [Fact]
    public void Open_DatabaseBeforeTheStationColumns_KeepsItsRowsAndAddsTheColumns()
    {
        string path = Path.Combine(_dir, "old.db");
        using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE reading(at INTEGER NOT NULL, status INTEGER NOT NULL, soc REAL, in_ac REAL, in_dc REAL,
                  out_ac REAL, out_dc REAL, volt REAL, hz REAL, mode TEXT, reason TEXT, grid_volt REAL, grid_hz REAL);
                INSERT INTO reading(at, status, soc, grid_volt) VALUES (1, 0, 50, 231.5);
                CREATE TABLE event(at INTEGER NOT NULL, kind TEXT NOT NULL, detail TEXT NOT NULL);
                INSERT INTO event(at, kind, detail) VALUES (1, 'eco-on', 'kept');
                """;
            cmd.ExecuteNonQuery();
        }
        using (var store = new StationStore(path)) store.AddReading(T0, WithExtras(Extras), ReadingStatus.Accepted, "");
        using (var store = new StationStore(path))
        {
            Assert.Equal(2, store.Counts().Raw);   // opening again adds nothing twice
            Assert.Equal("kept", Assert.Single(store.Events(0, 2)).Detail);
        }
        using var check = new SqliteConnection($"Data Source={path};Pooling=False");
        check.Open();
        using var q = check.CreateCommand();
        q.CommandText = "SELECT soc, grid_volt, pack_volt, temp_pv FROM reading ORDER BY at";
        using var r = q.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal((50.0, 231.5), (r.GetDouble(0), r.GetDouble(1)));
        Assert.True(r.IsDBNull(2));
        Assert.True(r.Read());
        Assert.Equal((52.8, 25.0), (r.GetDouble(2), r.GetDouble(3)));
    }
}
