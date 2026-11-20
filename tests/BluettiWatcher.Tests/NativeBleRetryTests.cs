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
}
