using BluettiWatcher.Decision;
using BluettiWatcher.Io.Ble;

namespace BluettiWatcher.Tests;

/// <summary>A Bluetooth failure inside one poll is retried over Bluetooth before the poll is given to the cloud.</summary>
public class NativeBleRetryTests
{
    private static readonly Reading Full = new(85, 0, 345, "INVERTER_OUTPUT", 120, 12, 2301, 50, GridVolt: 224.6, GridHz: 50);

    private static ushort[] Regs(int start, int quantity)
    {
        var values = new Dictionary<int, ushort> { [36] = 120, [37] = 0, [38] = 345, [39] = 12, [43] = 85, [70] = 1, [71] = 2301, [74] = 500, [77] = 2246, [80] = 500 };
        return [.. Enumerable.Range(start, quantity).Select(a => values.GetValueOrDefault(a))];
    }

    private static IEnumerable<byte[]> Mtu20(ReadRequest r) => ModbusFrameTests.Answer(Regs(r.Start, r.Quantity)).Chunk(20);

    private static NativeBleReader Reader(IBleConnector c, List<string> log, int timeoutSec = 10, double commandSec = 0.3) =>
        new(new StationConfig { Mac = "AA:BB:CC:DD:EE:FF", Type = "AC200PL", ReaderTimeoutSec = timeoutSec }, c, log.Add, TimeSpan.FromSeconds(commandSec));

    [Theory]
    [InlineData("the station has no ff01/ff02 characteristic")]
    [InlineData("GATT Unreachable after 3 tries")]
    public async Task ReadAsync_FirstOpenFails_OpensOnceMoreAndReadsOverBluetooth(string failure)
    {
        await using var link = new ScriptedLink(Mtu20);
        var opens = new Queue<BleOpen>([new BleOpen(null, failure), new BleOpen(link, null)]);
        var conn = new ScriptedConnector(opens.Dequeue);
        var log = new List<string>();
        var r = await Reader(conn, log).ReadAsync(CancellationToken.None);
        Assert.Equal(Full, r);
        Assert.Equal(2, conn.Opens);
        Assert.Contains($"connection opened at the second try (first: {failure})", Assert.Single(log), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_EveryOpenFails_StopsAfterTheSecondOpen()
    {
        var conn = new ScriptedConnector(() => new BleOpen(null, "GATT Unreachable after 3 tries"));
        var log = new List<string>();
        Assert.Null(await Reader(conn, log).ReadAsync(CancellationToken.None));
        Assert.Equal(2, conn.Opens);
        Assert.Contains("GATT Unreachable", Assert.Single(log), StringComparison.Ordinal);
    }

    /// <summary>
    /// An open that never completes (a WinRT call ignoring its cancellation) is cut at its own cap, so the retry and the
    /// register reads still fit inside readerTimeoutSec instead of the whole budget going to the hung open.
    /// </summary>
    [Fact]
    public async Task ReadAsync_FirstOpenHangs_IsCutAtItsCapAndTheRetryReadsOverBluetooth()
    {
        await using var link = new ScriptedLink(Mtu20);
        var conn = new SequenceConnector(n => n == 1 ? new TaskCompletionSource<BleOpen>().Task : Task.FromResult(new BleOpen(link, null)));
        var log = new List<string>();
        var r = await Reader(conn, log, timeoutSec: 1).ReadAsync(CancellationToken.None);
        Assert.Equal(Full, r);
        Assert.Equal(2, conn.Opens);
        Assert.Contains("connection opened at the second try (first: the connection did not open within 0.4 s)", Assert.Single(log), StringComparison.Ordinal);
    }

    /// <summary>
    /// Both opens hang: each is cut at its cap. The clock fires the caps at once and never the poll's budget, so the outcome
    /// does not depend on how late a loaded machine runs a timer (two caps of 0.4 s left only 0.2 s to the 1 s budget).
    /// </summary>
    [Fact]
    public async Task ReadAsync_EveryOpenHangs_IsNoReadingNamingTheOpenCap()
    {
        var conn = new SequenceConnector(_ => new TaskCompletionSource<BleOpen>().Task);
        var log = new List<string>();
        var clock = new CapsOnlyClock(TimeSpan.FromSeconds(1));
        var reader = new NativeBleReader(new StationConfig { Mac = "AA:BB:CC:DD:EE:FF", Type = "AC200PL", ReaderTimeoutSec = 1 }, conn, log.Add,
            TimeSpan.FromSeconds(0.3), time: clock);
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(2, conn.Opens);
        Assert.Contains("no reading: the connection did not open within 0.4 s", Assert.Single(log), StringComparison.Ordinal);
        Assert.Contains(TimeSpan.FromSeconds(1), clock.Held);   // the poll's budget runs on the reader's clock
    }

    /// <summary>
    /// An open cut at its cap names the step it hung in (the last the connector reported): the log then tells a station
    /// Windows never connected to from a GATT setup that stalled after the connection.
    /// </summary>
    [Fact]
    public async Task ReadAsync_OpenHangsInAStep_TheMissNamesThatStep()
    {
        var conn = new SequenceConnector(_ => new TaskCompletionSource<BleOpen>().Task, "finding the device", "GATT services, try 2 (the last answered Unreachable)");
        var log = new List<string>();
        var reader = new NativeBleReader(new StationConfig { Mac = "AA:BB:CC:DD:EE:FF", Type = "AC200PL", ReaderTimeoutSec = 1 }, conn, log.Add,
            TimeSpan.FromSeconds(0.3), time: new CapsOnlyClock(TimeSpan.FromSeconds(1)));
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
        Assert.Contains("no reading: the connection did not open within 0.4 s (hung in: GATT services, try 2 (the last answered Unreachable))",
            Assert.Single(log), StringComparison.Ordinal);
    }

    /// <summary>A clock whose timers shorter than <c>hold</c> fire at once and whose longer ones never fire.</summary>
    private sealed class CapsOnlyClock(TimeSpan hold) : TimeProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentBag<TimeSpan> _held = [];

        public IReadOnlyCollection<TimeSpan> Held => _held;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime >= hold) _held.Add(dueTime);
            else if (dueTime != Timeout.InfiniteTimeSpan) ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new NoTimer();
        }

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>An open whose cap ends while it is still going: a link it hands over late is released, never left open.</summary>
    [Fact]
    public async Task ReadAsync_HungOpenCompletesLate_ItsLinkIsReleased()
    {
        var late = new TaskCompletionSource<BleOpen>();
        await using var lateLink = new ScriptedLink(Mtu20);
        await using var link = new ScriptedLink(Mtu20);
        var conn = new SequenceConnector(n => n == 1 ? late.Task : Task.FromResult(new BleOpen(link, null)));
        Assert.Equal(Full, await Reader(conn, [], timeoutSec: 1).ReadAsync(CancellationToken.None));
        late.SetResult(new BleOpen(lateLink, null));
        Assert.True(lateLink.Disposed);
    }

    /// <summary>The fields read before the poll's budget ran out are this poll's own: they make a partial reading, never nothing.</summary>
    [Fact]
    public async Task ReadAsync_BudgetRunsOutAfterTheFirstBlock_KeepsThePartialReadingAndNamesTheWait()
    {
        await using var link = new ScriptedLink(r => r.Start == 36 ? Mtu20(r) : []);
        var log = new List<string>();
        var r = await Reader(new ScriptedConnector(() => new BleOpen(link, null)), log, timeoutSec: 1, commandSec: 5).ReadAsync(CancellationToken.None);
        Assert.Equal(85, r?.Soc);
        Assert.Equal(345, r?.OutAc);
        Assert.Null(r?.GridVolt);
        Assert.Contains("partial reading: readerTimeoutSec (1 s) ran out waiting for registers 70+11", Assert.Single(log), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_BudgetRunsOutBeforeAnyAnswer_IsNoReadingNamingTheWait()
    {
        await using var link = new ScriptedLink(_ => []);
        var log = new List<string>();
        Assert.Null(await Reader(new ScriptedConnector(() => new BleOpen(link, null)), log, timeoutSec: 1, commandSec: 5).ReadAsync(CancellationToken.None));
        Assert.Contains("no reading: readerTimeoutSec (1 s) ran out waiting for registers 36+8", Assert.Single(log), StringComparison.Ordinal);
    }

    /// <summary>A connector answering each open (numbered from 1) with the task the script gives; a never-completing task ignores its cancellation.</summary>
    private sealed class SequenceConnector(Func<int, Task<BleOpen>> open, params string[] stages) : IBleConnector
    {
        public int Opens { get; private set; }

        public Task<BleOpen> OpenAsync(ulong address, Action<string> stage, CancellationToken ct)
        {
            foreach (var s in stages) stage(s);
            return open(++Opens);
        }
    }
}
