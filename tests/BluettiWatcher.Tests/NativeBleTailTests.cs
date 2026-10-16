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
        Assert.Single(log, l => l.Contains("still running", StringComparison.Ordinal));   // not repeated while it stays the same
        Assert.True(reader.BluetoothStalled);

        stuck.SetResult(new BleOpen(late, null));   // the stuck call returns at last
        Reading? again = null;
        for (var until = DateTime.UtcNow.AddSeconds(5); again is null && DateTime.UtcNow < until; await Task.Delay(20, ct))
            again = await reader.ReadAsync(ct);
        Assert.True(late.Disposed);   // the abandoned read released its link when it ended
        Assert.NotNull(again);   // and Bluetooth reads again
        Assert.Equal(2, conn.Opens);
        Assert.False(reader.BluetoothStalled);
    }

    /// <summary>
    /// A Bluetooth call that never completes, even after its cancellation, cannot turn Bluetooth off until a restart: past the
    /// cap (2 minutes here) the next poll reads again, and the hung read finishing later changes nothing for the newer ones.
    /// </summary>
    [Fact]
    public async Task A_read_that_never_ends_is_given_up_at_the_cap_and_its_late_end_touches_no_newer_read()
    {
        var ct = TestContext.Current.CancellationToken;
        var hung = new TaskCompletionSource<BleOpen>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hungToo = new TaskCompletionSource<BleOpen>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hungLink = new ScriptedLink(Good);
        var conn = new TaskConnector(n => n switch
        {
            1 => hung.Task,
            2 => Task.FromResult(new BleOpen(new ScriptedLink(Good), null)),
            _ => hungToo.Task,
        });
        var clock = new ManualClock(Fx.T0);
        var log = new ConcurrentQueue<string>();
        var reader = new NativeBleReader(new StationConfig { Mac = "AA:BB:CC:DD:EE:FF", Type = "AC200PL", ReaderTimeoutSec = 1 },
            conn, log.Enqueue, TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(100), clock);

        Assert.False(reader.BluetoothStalled);
        Assert.Null(await reader.ReadAsync(ct));   // read 1 hangs: abandoned at the hard bound
        Assert.True(reader.BluetoothStalled);
        clock.Now = Fx.T0.AddSeconds(119);
        Assert.Null(await reader.ReadAsync(ct));   // under the cap: a miss, no connection
        Assert.Equal(1, conn.Opens);

        clock.Now = Fx.T0.AddMinutes(2);   // the cap: max(4 x 1.1 s, 2 min)
        Assert.NotNull(await reader.ReadAsync(ct));   // read 2 connects and reads
        Assert.Equal(2, conn.Opens);
        Assert.False(reader.BluetoothStalled);
        Assert.Single(log, l => l.Contains("gave up waiting", StringComparison.Ordinal));

        Assert.Null(await reader.ReadAsync(ct));   // read 3 hangs as well
        Assert.True(reader.BluetoothStalled);
        hung.SetResult(new BleOpen(hungLink, null));   // read 1 ends at last
        for (var until = DateTime.UtcNow.AddSeconds(5); !hungLink.Disposed && DateTime.UtcNow < until;) await Task.Delay(20, ct);
        Assert.True(hungLink.Disposed);
        await Task.Delay(100, ct);
        Assert.True(reader.BluetoothStalled);   // read 1's end did not clear read 3's state
        Assert.Null(await reader.ReadAsync(ct));
        Assert.Equal(3, conn.Opens);   // still waiting for read 3: no new connection
        hungToo.SetResult(new BleOpen(null, "gone"));
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

    private async Task<JsonElement> Health(NativeBleReader reader, bool stalled = false)
    {
        var api = new StationApi(_cfg, _service, _store, new ManualClock(Fx.T0), Fx.T0, bluetoothEncrypted: () => reader.StationEncrypted, bluetoothStalled: () => stalled);
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
        Assert.False(health.GetProperty("bluetoothStalled").GetBoolean());
    }

    [Fact]
    public async Task Health_says_bluetooth_is_stalled_and_stays_ok()
    {
        var reader = new NativeBleReader(_cfg, new ScriptedConnector(() => new BleOpen(null, "unused")), _ => { });
        var health = await Health(reader, stalled: true);
        Assert.True(health.GetProperty("bluetoothStalled").GetBoolean());
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
        Assert.Equal(3, conn.Opens);   // 39 is asked again over a second connection and times out again
        Assert.True(links[0].Disposed);
    }

    /// <summary>A late answer that is already in before the next request is written settles its request: the next one reads normally.</summary>
    [Fact]
    public async Task A_late_answer_in_before_the_next_write_is_set_aside_and_the_next_register_reads()
    {
        var radio = Channel.CreateUnbounded<byte[]>();
        var links = new List<SharedRadioLink>();
        void Script(ReadRequest r, ChannelWriter<byte[]> w)
        {
            if (r.Start == 36 && r.Quantity > 1) { w.TryWrite(ModbusFrameTests.ExceptionAnswer(0x02)); return; }
            if (r.Start == 39 && links.Count == 1) return;   // only the first connection leaves 39 unanswered
            w.TryWrite(Answer(r.Start, r.Quantity));
        }
        var conn = new TaskConnector(n =>
        {
            if (n == 2) radio.Writer.TryWrite(Answer(39, 1));   // 39's answer lands while the new connection opens
            var l = new SharedRadioLink(radio, Script);
            links.Add(l);
            return Task.FromResult(new BleOpen(l, null));
        });
        var r = await Reader(conn, []).ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new Reading(85, 0, 345, "INVERTER_OUTPUT", 120, 12, 2301, 50), r);   // 39 asked again over the new connection
        // Settled before 39 was asked again and 43 asked: neither answer was set aside and asked again.
        Assert.Equal(2, links.SelectMany(l => l.Written).Count(w => w.Start == 39));
        Assert.Single(links.SelectMany(l => l.Written), w => w.Start == 43);
    }

    /// <summary>
    /// 39's answer never comes at all: the next one-register answer (43's own) may be 39's late one, so it is set aside and
    /// 43 asked again; the answer that follows can only be 43's, and the battery percent is read.
    /// </summary>
    [Fact]
    public async Task A_lost_one_register_answer_costs_one_more_request_not_the_next_field()
    {
        var radio = Channel.CreateUnbounded<byte[]>();
        void Script(ReadRequest r, ChannelWriter<byte[]> w)
        {
            if (r.Start == 36 && r.Quantity > 1) { w.TryWrite(ModbusFrameTests.ExceptionAnswer(0x02)); return; }
            if (r.Start == 39) return;   // lost: never answered
            w.TryWrite(Answer(r.Start, r.Quantity));
        }
        var links = new List<SharedRadioLink>();
        var conn = new TaskConnector(_ => { var l = new SharedRadioLink(radio, Script); links.Add(l); return Task.FromResult(new BleOpen(l, null)); });
        var r = await Reader(conn, []).ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new Reading(85, 0, 345, "INVERTER_OUTPUT", 120, null, 2301, 50), r);
        // 39 is asked twice (once more over a new connection), so 43's first two answers may be 39's late ones: set aside.
        Assert.Equal(3, links.SelectMany(l => l.Written).Count(w => w.Start == 43));
    }

    /// <summary>A late batch answer has another byte count than the next request's: it is refused and that connection ends.</summary>
    [Fact]
    public async Task A_late_answer_with_a_different_size_still_ends_the_connection()
    {
        var radio = Channel.CreateUnbounded<byte[]>();
        bool lateSent = false;
        void Script(ReadRequest r, ChannelWriter<byte[]> w)
        {
            if (r.Start == 36 && r.Quantity > 1) return;   // the batch times out
            if (r.Start == 70 && !lateSent) { lateSent = true; w.TryWrite(Answer(36, 8)); }   // its late answer, after 70+5 is written
            w.TryWrite(Answer(r.Start, r.Quantity));
        }
        var links = new List<SharedRadioLink>();
        var conn = new TaskConnector(_ => { var l = new SharedRadioLink(radio, Script); links.Add(l); return Task.FromResult(new BleOpen(l, null)); });
        var log = new List<string>();
        var r = await Reader(conn, log).ReadAsync(TestContext.Current.CancellationToken);
        Assert.Null(r);   // 36+8 timed out, 70+5 refused: no field at all, the 36+8 values never decoded as 70+5
        Assert.Equal(3, conn.Opens);   // 36+8 asked again over a second connection; 70+5's re-ask exceeds MaxReconnects
        Assert.All(links, l => Assert.True(l.Disposed));
        Assert.Equal([(36, 8)], links[0].Written.Select(w => (w.Start, w.Quantity)));
        Assert.Equal([(36, 8)], links[1].Written.Select(w => (w.Start, w.Quantity)));
        Assert.Equal([(70, 5)], links[2].Written.Select(w => (w.Start, w.Quantity)));
        Assert.Contains("byte count 16, expected 10", string.Join("\n", log), StringComparison.Ordinal);
    }

    /// <summary>
    /// A V2 station (AC180): battery % is register 102, read alone first; the fields of 140+7 fall back to one register each.
    /// Poll 1 ends with 146 (AC input W) unanswered. On poll 2 the radio link is reused and 146's late answer arrives right
    /// after the request for 102 is written: it must never be taken as the battery percent.
    /// </summary>
    [Fact]
    public async Task A_late_answer_from_the_previous_poll_is_never_taken_for_the_battery_percent()
    {
        var values = new Dictionary<int, ushort> { [102] = 64, [140] = 10, [142] = 345, [144] = 20, [146] = 77 };
        byte[] Of(int start, int quantity) => ModbusFrameTests.Answer([.. Enumerable.Range(start, quantity).Select(a => values.GetValueOrDefault(a))]);
        var radio = Channel.CreateUnbounded<byte[]>();
        int poll = 1;
        bool lateSent = false;
        void Script(ReadRequest r, ChannelWriter<byte[]> w)
        {
            if (poll == 1 && r.Start == 140 && r.Quantity > 1) { w.TryWrite(ModbusFrameTests.ExceptionAnswer(0x02)); return; }
            if (poll == 1 && r.Start == 146) return;   // not answered in time: the poll ends with it unanswered
            if (poll == 2 && r.Start == 102 && !lateSent) { lateSent = true; w.TryWrite(Of(146, 1)); }   // its late answer
            w.TryWrite(Of(r.Start, r.Quantity));
        }
        var conn = new TaskConnector(_ => Task.FromResult(new BleOpen(new SharedRadioLink(radio, Script), null)));
        var reader = new NativeBleReader(new StationConfig { Mac = "AA:BB:CC:DD:EE:FF", Type = "AC180", ReaderTimeoutSec = 10 }, conn, _ => { }, TimeSpan.FromMilliseconds(300));
        var first = await reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(64, first?.Soc);
        Assert.Null(first?.InAc);
        poll = 2;
        var r = await reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(r);
        Assert.Equal(64, r.Soc);   // never 77, register 146's value
        Assert.Equal(77, r.InAc);
        Assert.Equal(345, r.OutAc);
    }
}