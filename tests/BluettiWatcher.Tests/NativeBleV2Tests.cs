using System.Text.Json;
using System.Threading.Channels;
using BluettiWatcher.Decision;
using BluettiWatcher.Io.Ble;

namespace BluettiWatcher.Tests;

public class V2ModelTests
{
    private static readonly string[] LibraryV2 =
    [
        "AC2A", "AC2P", "AC50B", "AC60", "AC60P", "AC70", "AC70P", "AC180", "AC180T", "AC180P", "AP300",
        "EL10", "EL30V2", "EL100V2", "EP600", "EP760", "EP800", "EP2000", "Handsfree 1", "PR30V2", "PR100V2",
    ];

    private static byte[] Body(int start, int quantity, Dictionary<int, ushort> values)
    {
        var body = new byte[2 * quantity];
        for (int i = 0; i < quantity; i++)
        {
            ushort v = values.GetValueOrDefault(start + i);
            body[2 * i] = (byte)(v >> 8);
            body[2 * i + 1] = (byte)v;
        }
        return body;
    }

    [Fact]
    public void The_v2_table_is_the_library_v2_split_and_the_native_reader_knows_every_model()
    {
        Assert.Equal(LibraryV2.Order(StringComparer.Ordinal), V2Models.All.Keys.Order(StringComparer.Ordinal));
        Assert.Empty(V2Models.All.Keys.Intersect(V1Models.All.Keys));
        Assert.Equal(29, BleModels.Names.Count);
        foreach (var name in LibraryV2)
        {
            Assert.True(BleModels.TryGet(name, out var m));
            Assert.Equal(2, m.IotVersion);
        }
        Assert.True(BleModels.TryGet("AC200PL", out var v1));
        Assert.Equal(1, v1.IotVersion);
        Assert.False(BleModels.TryGet("ac180", out _));
        Assert.False(BleModels.TryGet(null, out _));
    }

    /// <summary>READ ONLY, FOREVER: no V2 map holds a register at or over 2000 (the V2 switches start at 2011), and every plan is 0x03 reads under it.</summary>
    [Fact]
    public void No_v2_map_entry_reaches_the_v2_control_registers()
    {
        foreach (var (model, fields) in V2Models.All)
        {
            Assert.Contains(fields, f => f.Name == "BATTERY_SOC" && f.Address == 102);
            foreach (var f in fields) Assert.True(f.Address + f.Size - 1 < ModbusRead.ControlRegisterFloor, $"{model} {f.Name} at {f.Address}");
            foreach (var r in V2Models.Plan(fields))
            {
                Assert.True(r.Start + r.Quantity <= ModbusRead.ControlRegisterFloor);
                Assert.Equal(0x03, ModbusRead.Request(r.Start, r.Quantity).Frame.Span[1]);
            }
        }
    }

    [Fact]
    public void A_v2_plan_refuses_a_field_at_the_control_registers() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => V2Models.Plan([new V2Field("CTRL_AC", 2011, V2Kind.Bool)]));

    [Theory]
    [InlineData("AC180", "102+1 140+7")]
    [InlineData("AC50B", "102+1 140+7")]
    [InlineData("EP800", "102+1")]
    [InlineData("EP600", "102+1")]
    public void The_v2_poll_reads_the_battery_and_the_power_block(string model, string ranges)
    {
        Assert.True(BleModels.TryGet(model, out var m));
        Assert.Equal(ranges, string.Join(" ", m.Plan.Select(r => $"{r.Start}+{r.Quantity}")));
    }

    [Fact]
    public void Every_ac180_field_decodes_as_the_library_does()
    {
        var values = new Dictionary<int, ushort>
        {
            [102] = 64, [110] = 0x4341, [111] = 0x3831, [112] = 0x0030, [116] = 1, [117] = 2, [118] = 0, [119] = 0,
            [140] = 12, [142] = 345, [144] = 120, [146] = 7,
        };
        var cs = V2Models.Decode(V2Models.All["AC180"], 100, Body(100, 50, values));
        Assert.Equal(new FieldValue(64, null), cs["BATTERY_SOC"]);
        Assert.Equal(new FieldValue(null, "AC180"), cs["DEVICE_TYPE"]);   // bytes swapped per register: 41 43 31 38 30 00
        Assert.Equal(new FieldValue(null, "131073"), cs["DEVICE_SN"]);
        Assert.Equal(new FieldValue(12, null), cs["DC_OUTPUT_POWER"]);
        Assert.Equal(new FieldValue(345, null), cs["AC_OUTPUT_POWER"]);
        Assert.Equal(new FieldValue(120, null), cs["DC_INPUT_POWER"]);
        Assert.Equal(new FieldValue(7, null), cs["AC_INPUT_POWER"]);

        var detail = V2Models.Decode(V2Models.All["AC180"], 1213, Body(1213, 2, new() { [1213] = 523, [1214] = 41 }));
        Assert.Equal(52.3, detail["DC_INPUT_VOLTAGE"].Number);
        Assert.Equal(4.1, detail["DC_INPUT_CURRENT"].Number);
        // UIntField(AC_INPUT_VOLTAGE, 1314, 0.1): round(raw * 0.1, 2).
        Assert.Equal(230.1, V2Models.Decode(V2Models.All["AC180"], 1314, Body(1314, 1, new() { [1314] = 2301 }))["AC_INPUT_VOLTAGE"].Number);
        // The AC60 declares it with no multiplier: the raw value.
        Assert.Equal(2301, V2Models.Decode(V2Models.All["AC60"], 1314, Body(1314, 1, new() { [1314] = 2301 }))["AC_INPUT_VOLTAGE"].Number);
    }

    [Theory]
    [InlineData("EL10", 90, 1.5)]          // DecimalField(104, scale 0, multiplier 1/60): minutes to hours
    [InlineData("EL30V2", 10000, 167)]     // DecimalField(104, scale 4, multiplier 167)
    [InlineData("AC70", 125, 12.5)]        // DecimalField(104, scale 1)
    public void The_time_remaining_scales_differ_by_model(string model, int raw, double expected) =>
        Assert.Equal(expected, V2Models.Decode(V2Models.All[model], 104, Body(104, 1, new() { [104] = (ushort)raw }))["TIME_REMAINING"].Number);

    [Theory]
    [InlineData(100, 100.0)]
    [InlineData(101, null)]
    [InlineData(0xFFFF, null)]
    public void A_v2_battery_percent_over_100_is_dropped(int raw, double? expected)
    {
        var cs = V2Models.Decode(V2Models.All["AC180"], 102, Body(102, 1, new() { [102] = (ushort)raw }));
        Assert.Equal(expected, cs.TryGetValue("BATTERY_SOC", out var v) ? v.Number : null);
    }

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(1, 1.0)]
    [InlineData(2, null)]
    public void The_ac2a_output_flag_is_the_library_bool(int raw, double? expected)
    {
        var cs = V2Models.Decode(V2Models.All["AC2A"], 1509, Body(1509, 1, new() { [1509] = (ushort)raw }));
        Assert.True(cs.ContainsKey("AC_OUTPUT_ON"));
        Assert.Equal(expected, cs["AC_OUTPUT_ON"].Number);
    }
}

public class KeyExchangeTests
{
    private static byte[] Challenge(byte type = 1, byte[]? data = null)
    {
        byte[] body = [type, 4, .. data ?? [0x11, 0x22, 0x33, 0x44]];
        int sum = body.Sum(b => b);
        return [(byte)'*', (byte)'*', .. body, (byte)(sum >> 8), (byte)sum];
    }

    [Fact]
    public void A_challenge_is_a_pre_key_message_and_a_modbus_answer_is_not()
    {
        Assert.True(KeyExchange.IsPreKeyMessage(Challenge()));
        Assert.True(KeyExchange.IsPreKeyMessage(Challenge(3)));
        Assert.False(KeyExchange.IsPreKeyMessage(Challenge(2)));
        var bad = Challenge();
        bad[^1] ^= 1;
        Assert.False(KeyExchange.IsPreKeyMessage(bad));
        Assert.False(KeyExchange.IsPreKeyMessage(ModbusFrameTests.Answer(0x2A2A, 0x0104)));
        // A later piece of an answer that starts with the data bytes 2A 2A is not a key exchange (no valid sum).
        Assert.False(KeyExchange.IsPreKeyMessage([0x2A, 0x2A, 0x01, 0x04, 0, 0, 0, 0, 0x12, 0x34]));
        Assert.False(KeyExchange.IsPreKeyMessage([0x2A, 0x2A, 0x01]));
    }

    [Fact]
    public async Task An_encrypted_station_is_no_reading_with_a_clear_cause_and_nothing_else_is_written()
    {
        await using var link = new ScriptedLink(_ => [Challenge()]);
        var log = new List<string>();
        var cfg = new StationConfig { Mac = "AA:BB:CC:DD:EE:FF", Type = "AC180", BleBackend = "native", ReaderTimeoutSec = 10 };
        var r = await new NativeBleReader(cfg, new ScriptedConnector(() => new BleOpen(link, null)), log.Add, TimeSpan.FromMilliseconds(300)).ReadAsync(CancellationToken.None);
        Assert.Null(r);
        Assert.Single(link.Written);
        Assert.Contains("encrypted Bluetooth handshake", Assert.Single(log), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_challenge_sent_on_subscribe_stops_the_poll_before_any_request()
    {
        await using var link = new ScriptedLink(r => [ModbusFrameTests.Answer(new ushort[r.Quantity])]);
        var notes = typeof(ScriptedLink).GetField("_notes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(link) as Channel<byte[]>;
        notes!.Writer.TryWrite(Challenge());
        var log = new List<string>();
        var cfg = new StationConfig { Mac = "AA:BB:CC:DD:EE:FF", Type = "AC200PL", BleBackend = "native", ReaderTimeoutSec = 10 };
        Assert.Null(await new NativeBleReader(cfg, new ScriptedConnector(() => new BleOpen(link, null)), log.Add, TimeSpan.FromMilliseconds(300)).ReadAsync(CancellationToken.None));
        Assert.Empty(link.Written);
        Assert.Contains("unencrypted stations only", Assert.Single(log), StringComparison.Ordinal);
    }
}

public class NativeBleV2ReaderTests
{
    private static IEnumerable<byte[]> Station(ReadRequest r)
    {
        var values = new Dictionary<int, ushort> { [102] = 64, [140] = 12, [142] = 345, [144] = 120, [146] = 7, [141] = 0xFFFF };
        return ModbusFrameTests.Answer([.. Enumerable.Range(r.Start, r.Quantity).Select(a => values.GetValueOrDefault(a))]).Chunk(20);
    }

    [Theory]
    [InlineData("AC180", 7.0, 12.0)]
    [InlineData("AC50B", 7.0, 12.0)]
    [InlineData("Handsfree 1", 7.0, 12.0)]
    public async Task A_v2_poll_is_two_reads_and_the_reading_the_python_path_gives(string model, double inAc, double outDc)
    {
        await using var link = new ScriptedLink(Station);
        var conn = new ScriptedConnector(() => new BleOpen(link, null));
        var log = new List<string>();
        var cfg = new StationConfig { Mac = "AA:BB:CC:DD:EE:FF", Type = model, BleBackend = "native", ReaderTimeoutSec = 10 };
        var r = await new NativeBleReader(cfg, conn, log.Add, TimeSpan.FromMilliseconds(300)).ReadAsync(CancellationToken.None);
        // The AC50B declares no DC input power (devices/ac50b.py): null there, as the Python path gives.
        Assert.Equal(new Reading(64, inAc, 345, "BLE_MAINS", model == "AC50B" ? null : 120, outDc, null, null), r);
        Assert.Equal(["0103006600010000", "0103008C00070000"], link.Written.Select(w => Convert.ToHexString(w[..6]) + "0000"));
        Assert.Empty(log);
    }

    [Fact]
    public async Task The_ep800_has_no_mains_field_so_it_gives_no_reading_and_never_connects()
    {
        var conn = new ScriptedConnector(() => throw new InvalidOperationException("must not connect"));
        var log = new List<string>();
        var cfg = new StationConfig { Mac = "AA:BB:CC:DD:EE:FF", Type = "EP800", BleBackend = "native", ReaderTimeoutSec = 10 };
        Assert.Null(await new NativeBleReader(cfg, conn, log.Add, TimeSpan.FromMilliseconds(300)).ReadAsync(CancellationToken.None));
        Assert.Equal(0, conn.Opens);
        Assert.Contains("battery % only", Assert.Single(log), StringComparison.Ordinal);
    }
}

public class NativeBleV2ConfigTests
{
    private static StationConfig Load(string json) => JsonSerializer.Deserialize(json, StationJson.Default.StationConfig)!;

    [Theory]
    [InlineData("AC70")]
    [InlineData("Handsfree 1")]
    [InlineData("EP2000")]
    public void Native_with_a_v2_model_is_valid(string type)
    {
        var cfg = Load($$"""{ "mac": "AA:BB:CC:DD:EE:FF", "type": "{{type}}" }""");
        Assert.Null(cfg.BluetoothProblem);
        Assert.Null(cfg.PollProblem);
    }

    [Fact]
    public void An_unknown_type_names_the_v2_models_too()
    {
        var problem = Load("""{ "mac": "AA:BB:CC:DD:EE:FF", "type": "AC999" }""").BluetoothProblem;
        Assert.Contains("AC999", problem, StringComparison.Ordinal);
        Assert.Contains("AC180", problem, StringComparison.Ordinal);
        Assert.Contains("AC200PL", problem, StringComparison.Ordinal);
    }
}
