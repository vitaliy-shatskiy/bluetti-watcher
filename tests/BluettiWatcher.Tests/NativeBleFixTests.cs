using System.Threading.Channels;
using BluettiWatcher.Decision;
using BluettiWatcher.Io;
using BluettiWatcher.Io.Ble;

namespace BluettiWatcher.Tests;

/// <summary>A link that scripts its write: it may throw, drop the link (complete the channel with a cause) or answer.</summary>
internal sealed class FlakyLink(Func<ReadRequest, ChannelWriter<byte[]>, Task> onWrite) : IBleLink
{
    private readonly Channel<byte[]> _notes = Channel.CreateUnbounded<byte[]>();
    public List<ReadRequest> Written { get; } = [];
    public bool Disposed { get; private set; }
    public ChannelReader<byte[]> Notifications => _notes.Reader;

    public async Task WriteAsync(ReadRequest request, CancellationToken ct)
    {
        Written.Add(request);
        await onWrite(request, _notes.Writer);
    }

    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

public class NativeBleModeTests
{
    private static StationConfig Cfg(string type) => new() { Mac = "AA:BB:CC:DD:EE:FF", Type = type, ReaderTimeoutSec = 10 };

    private static IEnumerable<byte[]> Answer(ReadRequest r, Dictionary<int, ushort> values) =>
        ModbusFrameTests.Answer([.. Enumerable.Range(r.Start, r.Quantity).Select(a => values.GetValueOrDefault(a))]).Chunk(20);

    private static async Task<Reading?> Read(string type, Dictionary<int, ushort> values)
    {
        await using var link = new ScriptedLink(r => Answer(r, values));
        var reader = new NativeBleReader(Cfg(type), new ScriptedConnector(() => new BleOpen(link, null)), _ => { }, TimeSpan.FromMilliseconds(300));
        return await reader.ReadAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>V2 AC180: 102 SOC, 142 AC out, 146 AC in. V1 EB3A: 43 SOC, 38 AC out, 37 AC in.</summary>
    public static TheoryData<string, int, int, int, double, string?, bool> Cases => new()
    {
        // type, soc register, ac-out register, ac-in register, ac in W, expected mode, expected grid on
        { "AC180", 102, 142, 146, 250, "BLE_MAINS", true },
        { "AC180", 102, 142, 146, 0, "INVERTER_OUTPUT", false },
        { "EB3A", 43, 38, 37, 250, "BLE_MAINS", true },
        { "EB3A", 43, 38, 37, 0, "INVERTER_OUTPUT", false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task A_model_without_an_output_mode_gets_the_derived_one_and_the_decision_accepts_it(
        string type, int socReg, int outReg, int inReg, double inAc, string? mode, bool gridOn)
    {
        var r = await Read(type, new() { [socReg] = 64, [outReg] = 345, [inReg] = (ushort)inAc });
        Assert.NotNull(r);
        Assert.Equal(mode, r.Mode);
        // Through the real decision, not only the reader: the reading is accepted (no "output mode" missing) and the grid is known.
        Assert.Equal("", StationDecision.Check(r, new StationState(), Fx.T0, Fx.Defaults()));
        var d = Fx.Step([r], cfg: Fx.Defaults());
        Assert.Equal("", d[0].Rejected);
        Assert.Equal(gridOn ? Grid.On : Grid.Off, StationDecision.GridOf(r));
    }

    [Fact]
    public async Task No_ac_in_and_no_load_has_no_derived_mode_as_the_cloud_reader()
    {
        var r = await Read("AC180", new() { [102] = 64 });
        Assert.Null(r?.Mode);
    }

    [Fact]
    public async Task A_model_with_its_own_mode_field_keeps_it()
    {
        // AC200PL: 70 = 2 (BYPASS_OUTPUT_C) with 250 W in: the station's own mode, not the derived one.
        var r = await Read("AC200PL", new() { [43] = 85, [37] = 250, [38] = 345, [70] = 2 });
        Assert.Equal("BYPASS_OUTPUT_C", r?.Mode);
    }

    [Theory]
    [InlineData("EP600")]
    [InlineData("EP760")]
    [InlineData("EP800")]
    [InlineData("EP2000")]
    public async Task A_battery_only_model_gives_no_reading_so_the_cloud_answers_and_it_is_said_once(string type)
    {
        var conn = new ScriptedConnector(() => throw new InvalidOperationException("must not connect"));
        var log = new List<string>();
        var reader = new NativeBleReader(Cfg(type), conn, log.Add, TimeSpan.FromMilliseconds(300));
        Assert.Null(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Null(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, conn.Opens);
        Assert.Contains($"this model ({type}) reports battery % only over Bluetooth; mains detection needs the cloud fallback", Assert.Single(log), StringComparison.Ordinal);
        var viaCloud = await new FallbackReader(reader, new QueueReader { Default = new Reading(50, 0, 10, "INVERTER_OUTPUT") }).ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("cloud", viaCloud?.Source);
    }

    [Fact]
    public void The_derived_mode_is_a_page_code()
    {
        Assert.Contains(V1Models.DerivedMains, PageCodes.OutputModes);
        Assert.Contains("om_" + V1Models.DerivedMains, PageCodes.Keys());
    }
}

public class NativeBleLinkLossTests
{
    private static StationConfig Cfg() => new() { Mac = "AA:BB:CC:DD:EE:FF", Type = "AC200PL", ReaderTimeoutSec = 10 };

    private static IEnumerable<byte[]> Good(ReadRequest r) =>
        ModbusFrameTests.Answer([.. Enumerable.Range(r.Start, r.Quantity).Select(a => a == 43 ? (ushort)85 : (ushort)0)]).Chunk(20);

    [Fact]
    public async Task A_failed_write_ends_that_connection_and_the_next_request_goes_over_a_new_one()
    {
        await using var first = new FlakyLink((_, _) => throw new IOException("GATT write failed"));
        await using var second = new ScriptedLink(Good);
        var links = new Queue<IBleLink>([first, second]);
        var conn = new ScriptedConnector(() => new BleOpen(links.Dequeue(), null));
        var log = new List<string>();
        var r = await new NativeBleReader(Cfg(), conn, log.Add, TimeSpan.FromMilliseconds(300)).ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, conn.Opens);
        Assert.True(first.Disposed);
        Assert.Single(first.Written);
        // The lost request, as in a timeout, is asked again over the new connection, then the second range.
        Assert.Equal([36, 70], second.Written.Select(w => (w[2] << 8) | w[3]));   // the frames' start registers
        Assert.NotNull(r);
        Assert.Equal(85, r.Soc);
        Assert.Empty(log);
    }

    [Fact]
    public async Task A_link_that_drops_under_the_request_is_reconnected_like_a_timeout()
    {
        await using var first = new FlakyLink((_, notes) => { notes.TryComplete(new IOException("the station disconnected")); return Task.CompletedTask; });
        await using var second = new ScriptedLink(Good);
        var links = new Queue<IBleLink>([first, second]);
        var conn = new ScriptedConnector(() => new BleOpen(links.Dequeue(), null));
        var log = new List<string>();
        var r = await new NativeBleReader(Cfg(), conn, log.Add, TimeSpan.FromMilliseconds(300)).ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, conn.Opens);
        Assert.True(first.Disposed);
        Assert.NotNull(r);
        Assert.Equal(85, r.Soc);   // asked again over the new connection
        Assert.Empty(log);
    }

    [Fact]
    public async Task Reconnects_stay_inside_the_per_poll_budget()
    {
        var conn = new ScriptedConnector(() => new BleOpen(new FlakyLink((_, _) => throw new IOException("GATT write failed")), null));
        var log = new List<string>();
        Assert.Null(await new NativeBleReader(Cfg(), conn, log.Add, TimeSpan.FromMilliseconds(300)).ReadAsync(TestContext.Current.CancellationToken));
        Assert.True(conn.Opens <= 3);
    }

    [Fact]
    public async Task A_miss_that_differs_only_in_its_byte_counts_is_logged_once()
    {
        int n = 2;
        var conn = new ScriptedConnector(() =>
        {
            int bytes = n++;
            return new BleOpen(new ScriptedLink(r => [ModbusFrameTests.Answer([.. new ushort[r.Quantity]])[..bytes]]), null);
        });
        var log = new List<string>();
        var reader = new NativeBleReader(Cfg(), conn, log.Add, TimeSpan.FromMilliseconds(150));
        for (int i = 0; i < 3; i++) Assert.Null(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Single(log);
        Assert.Contains("of 21 bytes", log[0], StringComparison.Ordinal);   // the first line keeps its detail
    }
}

public class EncryptedStationMemoryTests
{
    private static byte[] Challenge()
    {
        byte[] body = [1, 4, 0x11, 0x22, 0x33, 0x44];
        int sum = body.Sum(b => b);
        return [(byte)'*', (byte)'*', .. body, (byte)(sum >> 8), (byte)sum];
    }

    private static StationConfig Cfg() => new() { Mac = "AA:BB:CC:DD:EE:FF", Type = "AC180", ReaderTimeoutSec = 10 };

    [Fact]
    public async Task An_encrypted_station_is_not_connected_to_again_and_the_cloud_answers()
    {
        var conn = new ScriptedConnector(() => new BleOpen(new ScriptedLink(_ => [Challenge()]), null));
        var log = new List<string>();
        var reader = new NativeBleReader(Cfg(), conn, log.Add, TimeSpan.FromMilliseconds(300));
        Assert.Null(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, conn.Opens);
        for (int i = 0; i < 3; i++) Assert.Null(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, conn.Opens);   // no connection at all after the first
        Assert.Single(log);
        Assert.Contains("encrypted Bluetooth handshake", log[0], StringComparison.Ordinal);
        var viaCloud = await new FallbackReader(reader, new QueueReader { Default = new Reading(50, 0, 10, "INVERTER_OUTPUT") }).ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("cloud", viaCloud?.Source);
    }

    [Theory]
    [InlineData(1)]   // "*" | "*" 01 04 ...
    [InlineData(2)]   // "**" | 01 04 ...
    [InlineData(3)]   // "**" 01 | 04 ...
    [InlineData(7)]   // everything but the last byte | the last byte
    public async Task A_handshake_split_over_notifications_is_still_caught(int cut)
    {
        var c = Challenge();
        var conn = new ScriptedConnector(() => new BleOpen(new ScriptedLink(_ => [c[..cut], c[cut..]]), null));
        var log = new List<string>();
        Assert.Null(await new NativeBleReader(Cfg(), conn, log.Add, TimeSpan.FromMilliseconds(300)).ReadAsync(TestContext.Current.CancellationToken));
        Assert.Contains("encrypted Bluetooth handshake", Assert.Single(log), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_handshake_in_single_bytes_is_still_caught()
    {
        var c = Challenge();
        var conn = new ScriptedConnector(() => new BleOpen(new ScriptedLink(_ => c.Select(b => new[] { b })), null));
        var log = new List<string>();
        Assert.Null(await new NativeBleReader(Cfg(), conn, log.Add, TimeSpan.FromMilliseconds(300)).ReadAsync(TestContext.Current.CancellationToken));
        Assert.Contains("encrypted Bluetooth handshake", Assert.Single(log), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Held_notifications_that_turn_out_plain_are_passed_on_in_order()
    {
        await using var inner = new ScriptedLink(_ => []);
        await using var guard = new KeyExchangeGuardLink(inner);
        var notes = typeof(ScriptedLink).GetField("_notes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(inner) as Channel<byte[]>;
        notes!.Writer.TryWrite([0x2A]);
        notes.Writer.TryWrite([0x2A, 0x07, 1, 2, 3, 4, 5]);   // "**" then type 7: not a key exchange
        Assert.Equal([0x2A], await guard.Notifications.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal([0x2A, 0x07, 1, 2, 3, 4, 5], await guard.Notifications.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_pre_key_message_is_found_at_the_start_of_the_bytes_received()
    {
        var c = Challenge();
        Assert.True(KeyExchange.StartsWithPreKeyMessage(c));
        Assert.True(KeyExchange.StartsWithPreKeyMessage([.. c, 0xAA]));
        Assert.False(KeyExchange.StartsWithPreKeyMessage(c.AsSpan(0, c.Length - 1)));
        Assert.False(KeyExchange.StartsWithPreKeyMessage(ModbusFrameTests.Answer(0x2A2A, 0x0104)));
    }
}

public sealed class CloudAfterStartTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bw-cloud-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task A_cloud_token_made_while_the_service_runs_turns_the_cloud_on_without_a_restart()
    {
        var cfg = new StationConfig { DataDir = _dir };
        var cloud = new QueueReader { Default = new Reading(50, 0, 10, "INVERTER_OUTPUT") };
        var reader = new FallbackReader(new QueueReader(), cloud, () => cfg.CloudOn);
        Assert.False(cfg.CloudOn);
        Assert.Null(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, cloud.Reads);   // no token: the cloud is not asked
        await File.WriteAllTextAsync(cfg.CloudTokenFile, "{}", TestContext.Current.CancellationToken);
        var r = await reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("cloud", r?.Source);
        Assert.Equal(1, cloud.Reads);
    }
}

public class FallbackIncompleteTests
{
    private static string Check(Reading r) => StationDecision.Check(r, new StationState(), Fx.T0, Fx.Defaults());

    private static readonly Reading Partial = new(64, 7, 345, null, Source: null);   // no output mode: the decision rejects it
    private static readonly Reading Good = new(50, 0, 10, "INVERTER_OUTPUT");

    [Fact]
    public async Task A_partial_bluetooth_reading_and_a_good_cloud_reading_use_the_cloud()
    {
        var cloud = new QueueReader { Default = Good };
        var log = new List<string>();
        var r = await new FallbackReader(new QueueReader { Default = Partial }, cloud, () => true, Check, log.Add).ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("cloud", r?.Source);
        Assert.Equal(50, r?.Soc);
        Assert.Equal(1, cloud.Reads);
    }

    [Fact]
    public async Task Both_incomplete_returns_the_bluetooth_reading_so_the_miss_is_recorded()
    {
        var cloud = new QueueReader { Default = new Reading(50, null, null, null) };
        var r = await new FallbackReader(new QueueReader { Default = Partial }, cloud, () => true, Check, _ => { }).ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("bluetooth", r?.Source);
        Assert.Equal(64, r?.Soc);
        Assert.Equal(1, cloud.Reads);
    }

    [Fact]
    public async Task A_cloud_with_no_reading_returns_the_bluetooth_reading()
    {
        var r = await new FallbackReader(new QueueReader { Default = Partial }, new QueueReader(), () => true, Check, _ => { }).ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("bluetooth", r?.Source);
    }

    [Fact]
    public async Task With_the_cloud_off_the_partial_bluetooth_reading_is_returned_unchanged()
    {
        var cloud = new QueueReader { Default = Good };
        var r = await new FallbackReader(new QueueReader { Default = Partial }, cloud, () => false, Check, _ => { }).ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("bluetooth", r?.Source);
        Assert.Equal(0, cloud.Reads);
    }

    [Fact]
    public async Task A_complete_bluetooth_reading_never_asks_the_cloud()
    {
        var cloud = new QueueReader { Default = Good };
        var r = await new FallbackReader(new QueueReader { Default = new Reading(64, 0, 345, "INVERTER_OUTPUT") }, cloud, () => true, Check, _ => { }).ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("bluetooth", r?.Source);
        Assert.Equal(0, cloud.Reads);
    }

    [Fact]
    public async Task The_answering_source_is_logged_once_per_state_change()
    {
        var bt = new QueueReader { Default = Partial };
        var log = new List<string>();
        var reader = new FallbackReader(bt, new QueueReader { Default = Good }, () => true, Check, log.Add);
        for (int i = 0; i < 3; i++) await reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Single(log);
        Assert.Contains("cloud", log[0], StringComparison.Ordinal);
        Assert.Contains("output mode", log[0], StringComparison.Ordinal);
        bt.Default = new Reading(64, 0, 345, "INVERTER_OUTPUT");
        await reader.ReadAsync(TestContext.Current.CancellationToken);
        await reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, log.Count);
        Assert.Contains("Bluetooth", log[1], StringComparison.Ordinal);
    }
}