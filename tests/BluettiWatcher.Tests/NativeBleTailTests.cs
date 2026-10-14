using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using BluettiWatcher.Decision;
using BluettiWatcher.Http;
using BluettiWatcher.Io.Ble;
using BluettiWatcher.Storage;

namespace BluettiWatcher.Tests;

/// <summary>
/// Every connection's notifications on one channel: on WinRT a reconnect may reuse the same radio link, so an answer the
/// station sends late lands on the next connection. Each write is scripted: it may push any notifications (also late ones).
/// </summary>
internal sealed class SharedRadioLink(Channel<byte[]> radio, Action<ReadRequest, ChannelWriter<byte[]>> onWrite) : IBleLink
{
    public List<ReadRequest> Written { get; } = [];
    public bool Disposed { get; private set; }
    public ChannelReader<byte[]> Notifications => radio.Reader;

    public Task WriteAsync(ReadRequest request, CancellationToken ct)
    {
        Written.Add(request);
        onWrite(request, radio.Writer);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

internal sealed class TaskConnector(Func<int, Task<BleOpen>> open) : IBleConnector
{
    private int _opens;
    public int Opens => Volatile.Read(ref _opens);

    public Task<BleOpen> OpenAsync(ulong address, CancellationToken ct) => open(Interlocked.Increment(ref _opens));
}

public class NativeBleSingleFlightTests
{
    private static IEnumerable<byte[]> Good(ReadRequest r) =>
        ModbusFrameTests.Answer([.. Enumerable.Range(r.Start, r.Quantity).Select(a => a == 43 ? (ushort)85 : a == 70 ? (ushort)1 : (ushort)0)]).Chunk(20);

    [Fact]
    public async Task A_poll_while_an_abandoned_read_is_alive_starts_no_bluetooth_read_and_the_link_is_released_when_it_ends()
    {
        var ct = TestContext.Current.CancellationToken;
        var stuck = new TaskCompletionSource<BleOpen>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var late = new ScriptedLink(Good);
        var conn = new TaskConnector(n => n == 1 ? stuck.Task : Task.FromResult(new BleOpen(new ScriptedLink(Good), null)));
        var log = new ConcurrentQueue<string>();
        var reader = new NativeBleReader(new StationConfig { Mac = "AA:BB:CC:DD:EE:FF", Type = "AC200PL", ReaderTimeoutSec = 1 },
            conn, log.Enqueue, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(100));

        Assert.Null(await reader.ReadAsync(ct));   // the open never returns: abandoned at readerTimeoutSec + margin
        for (int i = 0; i < 3; i++) Assert.Null(await reader.ReadAsync(ct));
        Assert.Equal(1, conn.Opens);   // no second Bluetooth read while the first is alive
        Assert.Single(log, l => l.Contains("still running", StringComparison.Ordinal));   // said once, not per poll

        stuck.SetResult(new BleOpen(late, null));   // the stuck call returns at last
        Reading? again = null;
        for (var until = DateTime.UtcNow.AddSeconds(5); again is null && DateTime.UtcNow < until; await Task.Delay(20, ct))
            again = await reader.ReadAsync(ct);
        Assert.True(late.Disposed);   // the abandoned read released its link when it ended
        Assert.NotNull(again);   // and Bluetooth reads again
        Assert.Equal(2, conn.Opens);
    }
}

/// <summary>An encrypted station is stated in /api/health; it is information, not a problem: the service stays ok.</summary>
public sealed class EncryptedStationHealthTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-encrypted-test-" + Guid.NewGuid().ToString("N"));
    private readonly StationStore _store;
    private readonly StationService _service;
    private readonly StationConfig _cfg;

    public EncryptedStationHealthTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new StationStore(Path.Combine(_dir, "station.db"));
        _cfg = new StationConfig { DataDir = _dir, Mac = "AA:BB:CC:DD:EE:FF", Type = "AC180", ReaderTimeoutSec = 10 };
        _service = new StationService(_cfg, new QueueReader(), new FakeActions(), new FakeHibernator(true), new FixedFlag(FlagView.Normal), _store, new ManualClock(Fx.T0), _ => { });
    }

    public void Dispose()
    {
        _service.Dispose();
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private async Task<JsonElement> Health(NativeBleReader reader)
    {
        var api = new StationApi(_cfg, _service, _store, new ManualClock(Fx.T0), Fx.T0, bluetoothEncrypted: () => reader.StationEncrypted);
        var r = await api.HandleAsync(new HttpRequest("GET", "/api/health", new Dictionary<string, string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = "127.0.0.1:18940" }, []), CancellationToken.None);
        Assert.Equal(200, r.Status);
        return JsonDocument.Parse(r.Body).RootElement.Clone();
    }

    [Fact]
    public async Task Health_says_the_station_is_encrypted_and_stays_ok()
    {
        byte[] body = [1, 4, 0x11, 0x22, 0x33, 0x44];
        int sum = body.Sum(b => b);
        byte[] challenge = [(byte)'*', (byte)'*', .. body, (byte)(sum >> 8), (byte)sum];
        var reader = new NativeBleReader(_cfg, new ScriptedConnector(() => new BleOpen(new ScriptedLink(_ => [challenge]), null)), _ => { }, TimeSpan.FromMilliseconds(300));

        Assert.False((await Health(reader)).GetProperty("bluetoothEncrypted").GetBoolean());   // present before anything is known
        Assert.Null(await reader.ReadAsync(TestContext.Current.CancellationToken));
        var health = await Health(reader);
        Assert.True(health.GetProperty("bluetoothEncrypted").GetBoolean());
        Assert.True(health.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, health.GetProperty("problem").ValueKind);
    }
}

public class NativeBleLateAnswerTests
{
    private static readonly Dictionary<int, ushort> Values = new() { [36] = 120, [37] = 0, [38] = 345, [39] = 12, [43] = 85, [70] = 1, [71] = 2301, [74] = 500 };

    private static byte[] Answer(int start, int quantity) =>
        ModbusFrameTests.Answer([.. Enumerable.Range(start, quantity).Select(a => Values.GetValueOrDefault(a))]);

    private static NativeBleReader Reader(TaskConnector conn, List<string> log) =>
        new(new StationConfig { Mac = "AA:BB:CC:DD:EE:FF", Type = "AC200PL", ReaderTimeoutSec = 10 }, conn, log.Add, TimeSpan.FromMilliseconds(300));

    /// <summary>
    /// The batch 36+8 is refused, so the fields are read one register each (byte count 2 for all). Register 39 (DC output W)
    /// gets no answer in time; its answer arrives on the next connection right after the request for 43 (battery %) is
    /// written. It must never be taken as the battery percent.
    /// </summary>
    [Fact]
    public async Task A_late_one_register_answer_on_a_reused_radio_link_is_never_taken_for_the_next_register()
    {
        var radio = Channel.CreateUnbounded<byte[]>();
        bool lateSent = false;
        void Script(ReadRequest r, ChannelWriter<byte[]> w)
        {
            if (r.Start == 36 && r.Quantity > 1) { w.TryWrite(ModbusFrameTests.ExceptionAnswer(0x02)); return; }
            if (r.Start == 39) return;   // held back: it arrives late
            if (r.Start == 43 && !lateSent) { lateSent = true; w.TryWrite(Answer(39, 1)); }   // the late answer to 39, after 43's request was written
            w.TryWrite(Answer(r.Start, r.Quantity));
        }
        var links = new List<SharedRadioLink>();
        var conn = new TaskConnector(_ => { var l = new SharedRadioLink(radio, Script); links.Add(l); return Task.FromResult(new BleOpen(l, null)); });
        var log = new List<string>();
        var r = await Reader(conn, log).ReadAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(r);
        Assert.Equal(85, r.Soc);   // never 12, register 39's value
        Assert.Equal(new Reading(85, 0, 345, "INVERTER_OUTPUT", 120, null, 2301, 50), r);
        Assert.Equal(2, conn.Opens);
        Assert.True(links[0].Disposed);
    }

    /// <summary>A late answer that is already in before the next request is written settles its request: the next one reads normally.</summary>
    [Fact]
    public async Task A_late_answer_in_before_the_next_write_is_set_aside_and_the_next_register_reads()
    {
        var radio = Channel.CreateUnbounded<byte[]>();
        void Script(ReadRequest r, ChannelWriter<byte[]> w)
        {
            if (r.Start == 36 && r.Quantity > 1) { w.TryWrite(ModbusFrameTests.ExceptionAnswer(0x02)); return; }
            if (r.Start == 39) return;
            w.TryWrite(Answer(r.Start, r.Quantity));
        }
        var conn = new TaskConnector(n =>
        {
            if (n == 2) radio.Writer.TryWrite(Answer(39, 1));   // 39's answer lands while the new connection opens
            return Task.FromResult(new BleOpen(new SharedRadioLink(radio, Script), null));
        });
        var r = await Reader(conn, []).ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new Reading(85, 0, 345, "INVERTER_OUTPUT", 120, null, 2301, 50), r);
    }

    /// <summary>A late batch answer has another byte count than the next request's: it is refused, never decoded as that range.</summary>
    [Fact]
    public async Task A_late_answer_with_a_different_size_still_ends_the_connection()
    {
        var radio = Channel.CreateUnbounded<byte[]>();
        void Script(ReadRequest r, ChannelWriter<byte[]> w)
        {
            if (r.Start == 36 && r.Quantity > 1) return;   // the batch times out
            if (r.Start == 70) w.TryWrite(Answer(36, 8));   // its late answer, after the next request is written
            w.TryWrite(Answer(r.Start, r.Quantity));
        }
        var conn = new TaskConnector(_ => Task.FromResult(new BleOpen(new SharedRadioLink(radio, Script), null)));
        var log = new List<string>();
        var r = await Reader(conn, log).ReadAsync(TestContext.Current.CancellationToken);
        Assert.Null(r?.Soc);
        Assert.Null(r?.Mode);   // 70+5 was never filled from the 36+8 answer
    }
}
