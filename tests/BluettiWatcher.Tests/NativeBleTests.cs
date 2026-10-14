using System.Text;
using System.Threading.Channels;
using BluettiWatcher.Decision;
using BluettiWatcher.Io;
using BluettiWatcher.Io.Ble;

namespace BluettiWatcher.Tests;

public class ModbusFrameTests
{
    [Fact]
    public void Crc16_modbus_matches_the_check_value() =>
        Assert.Equal(0x4B37, ModbusCrc.Compute(Encoding.ASCII.GetBytes("123456789")));

    [Theory]
    [InlineData("010300000001", "840A")]   // the textbook read of holding register 0
    [InlineData("1103006B0003", "7687")]   // the Modbus spec's example (slave 17, registers 107-109)
    public void Crc16_modbus_known_frames_low_byte_first(string hex, string crcLowFirst)
    {
        ushort crc = ModbusCrc.Compute(Convert.FromHexString(hex));
        Assert.Equal(crcLowFirst, Convert.ToHexString([(byte)crc, (byte)(crc >> 8)]));
    }

    [Fact]
    public void A_read_request_is_unit_1_function_3_big_endian_address_and_quantity_then_the_crc()
    {
        var r = ModbusRead.Request(43, 1);
        byte[] head = [0x01, 0x03, 0x00, 0x2B, 0x00, 0x01];
        ushort crc = ModbusCrc.Compute(head);
        Assert.Equal([.. head, (byte)crc, (byte)(crc >> 8)], r.Frame.ToArray());
        Assert.Equal(7, r.ExpectedLength);
        Assert.Equal("010300000001840A", Convert.ToHexString(ModbusRead.Request(0, 1).Frame.Span));
    }

    /// <summary>READ ONLY, FOREVER: the encoder builds function 0x03 and nothing else.</summary>
    [Fact]
    public void The_encoder_refuses_every_function_code_but_read_holding_registers()
    {
        for (int f = 0; f <= 255; f++)
        {
            if (f == 0x03) { Assert.Equal(8, ModbusRead.Encode((byte)f, 36, 8).Length); continue; }
            byte code = (byte)f;
            Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRead.Encode(code, 36, 1));
        }
    }

    /// <summary>READ ONLY, FOREVER: no request reaches the control registers (2000 and up).</summary>
    [Theory]
    [InlineData(2000, 1)]
    [InlineData(3007, 1)]
    [InlineData(2011, 1)]
    [InlineData(1999, 2)]
    [InlineData(0, 0)]
    [InlineData(0, 33)]
    [InlineData(-1, 1)]
    public void The_encoder_refuses_control_registers_and_bad_quantities(int start, int quantity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRead.Request(start, quantity));

    [Fact]
    public void A_request_may_end_just_under_the_control_registers() =>
        Assert.Equal(7, ModbusRead.Request(1999, 1).ExpectedLength);

    /// <summary>READ ONLY, FOREVER: no code can build a request around a frame of its own; only the encoder makes one.</summary>
    [Fact]
    public void A_read_request_has_no_constructor_outside_itself() =>
        Assert.All(typeof(ReadRequest).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic),
            c => Assert.True(c.IsPrivate, c.ToString()));

    /// <summary>
    /// READ ONLY, FOREVER: the WinRT link writes two things to the station, the read request (ff02) and the notification
    /// subscription (ff01's client configuration descriptor, Notify); nothing on close.
    /// </summary>
    [Fact]
    public void The_winrt_link_writes_only_the_request_and_the_subscription()
    {
        string src = File.ReadAllText(Path.Combine(Repo(), "src", "BluettiWatcher", "Io", "Ble", "WinRtBleLink.cs"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(src, @"\.Write\w*Async\("), m => m.Value == ".WriteValueWithResultAsync(");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(src, @"\.Write\w*Async\("), m => m.Value == ".WriteClientCharacteristicConfigurationDescriptorAsync(");
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(src, @"\.Write\w*Async\("));
        Assert.DoesNotContain("DescriptorValue.None", src, StringComparison.Ordinal);
        Assert.DoesNotContain("DescriptorValue.Indicate", src, StringComparison.Ordinal);
    }

    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    internal static byte[] Answer(params ushort[] words)
    {
        var f = new List<byte> { 0x01, 0x03, (byte)(2 * words.Length) };
        foreach (var w in words) { f.Add((byte)(w >> 8)); f.Add((byte)w); }
        ushort crc = ModbusCrc.Compute([.. f]);
        f.Add((byte)crc); f.Add((byte)(crc >> 8));
        return [.. f];
    }

    internal static byte[] ExceptionAnswer(byte code)
    {
        byte[] head = [0x01, 0x83, code];
        ushort crc = ModbusCrc.Compute(head);
        return [.. head, (byte)crc, (byte)(crc >> 8)];
    }

    [Fact]
    public void A_response_split_over_notifications_is_reassembled()
    {
        var req = ModbusRead.Request(36, 8);
        var answer = Answer(1, 2, 3, 4, 5, 6, 7, 85);
        Assert.Equal(21, answer.Length);
        var a = new ResponseAssembler(req);
        Assert.Equal(AssemblyState.NeedMore, a.Feed(answer.AsSpan(0, 20)));
        Assert.Equal(AssemblyState.Complete, a.Feed(answer.AsSpan(20)));
        Assert.Equal(answer[3..^2], a.Body);
    }

    [Fact]
    public void A_response_in_single_bytes_is_reassembled()
    {
        var answer = Answer(7, 8);
        var a = new ResponseAssembler(ModbusRead.Request(70, 2));
        for (int i = 0; i < answer.Length - 1; i++) Assert.Equal(AssemblyState.NeedMore, a.Feed(answer.AsSpan(i, 1)));
        Assert.Equal(AssemblyState.Complete, a.Feed(answer.AsSpan(answer.Length - 1)));
        Assert.Equal(new byte[] { 0, 7, 0, 8 }, a.Body);
    }

    [Fact]
    public void A_byte_count_that_does_not_match_the_request_is_invalid()
    {
        var a = new ResponseAssembler(ModbusRead.Request(36, 8));
        Assert.Equal(AssemblyState.Invalid, a.Feed(Answer(1, 2)));
        Assert.Contains("byte count", a.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_response_longer_than_expected_is_invalid()
    {
        var a = new ResponseAssembler(ModbusRead.Request(43, 1));
        Assert.Equal(AssemblyState.Invalid, a.Feed([.. Answer(85), 0x00]));
        Assert.Contains("longer", a.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bad_crc_is_invalid()
    {
        var answer = Answer(85);
        answer[^1] ^= 0xFF;
        var a = new ResponseAssembler(ModbusRead.Request(43, 1));
        Assert.Equal(AssemblyState.Invalid, a.Feed(answer));
        Assert.Contains("CRC", a.Error, StringComparison.Ordinal);
        Assert.Null(a.Body);
    }

    [Fact]
    public void A_modbus_exception_reply_is_named_with_its_code()
    {
        var a = new ResponseAssembler(ModbusRead.Request(36, 8));
        Assert.Equal(AssemblyState.NeedMore, a.Feed(ExceptionAnswer(0x02).AsSpan(0, 2)));
        Assert.Equal(AssemblyState.Exception, a.Feed(ExceptionAnswer(0x02).AsSpan(2)));
        Assert.Equal((byte)0x02, a.ExceptionCode);
        Assert.Contains("exception 0x02", a.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_exception_reply_with_a_bad_crc_is_invalid_not_an_exception()
    {
        var bad = ExceptionAnswer(0x02);
        bad[^2] ^= 0x01;
        var a = new ResponseAssembler(ModbusRead.Request(36, 8));
        Assert.Equal(AssemblyState.Invalid, a.Feed(bad));
    }

    [Theory]
    [InlineData(new byte[] { 0x02, 0x03, 0x02, 0x00, 0x55, 0x00, 0x00 })]   // another unit
    [InlineData(new byte[] { 0x01, 0x06, 0x00, 0x2B, 0x00, 0x01, 0x00, 0x00 })]   // a write echo
    public void A_frame_that_is_not_a_read_answer_is_invalid(byte[] frame)
    {
        var a = new ResponseAssembler(ModbusRead.Request(43, 1));
        Assert.Equal(AssemblyState.Invalid, a.Feed(frame));
    }
}

public class V1ModelTests
{
    /// <summary>READ ONLY, FOREVER: no ported map holds a control register (2000 and up), and every model plans only 0x03 reads under 2000.</summary>
    [Fact]
    public void No_map_entry_reaches_the_control_registers()
    {
        Assert.Equal(8, V1Models.All.Count);
        foreach (var (model, fields) in V1Models.All)
        {
            Assert.NotEmpty(fields);
            foreach (var f in fields) Assert.True(f.Address + f.Size - 1 < ModbusRead.ControlRegisterFloor, $"{model} {f.Name} at {f.Address}");
            foreach (var r in V1Models.Plan(fields)) Assert.Equal(0x03, ModbusRead.Request(r.Start, r.Quantity).Frame.Span[1]);
        }
    }

    [Fact]
    public void The_v1_and_v2_model_lists_are_the_library_split()
    {
        Assert.Equal(["AC200L", "AC200M", "AC200PL", "AC300", "AC500", "EB3A", "EP500", "EP500P"], V1Models.All.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(21, V2Models.All.Count);
        Assert.Contains("AC70", V2Models.All.Keys);
        Assert.Contains("Handsfree 1", V2Models.All.Keys);
        Assert.Empty(V2Models.All.Keys.Intersect(V1Models.All.Keys));
    }

    [Fact]
    public void The_ac200pl_poll_is_two_batched_reads_of_the_reading_fields()
    {
        var plan = V1Models.Plan(V1Models.ReadingFields(V1Models.All["AC200PL"]));
        Assert.Equal([(36, 8), (70, 5)], plan.Select(r => (r.Start, r.Quantity)));
    }

    [Fact]
    public void A_gap_too_wide_starts_a_new_range()
    {
        var all = V1Models.All["AC200PL"];
        var plan = V1Models.Plan(all.Where(f => f.Address >= 36).ToList());
        Assert.Equal([(36, 8), (70, 5), (86, 3)], plan.Select(r => (r.Start, r.Quantity)));
    }

    /// <summary>Registers 10..88 with the AC200PL values at their addresses; every other register is 0.</summary>
    private static byte[] Ac200plBody()
    {
        var regs = new ushort[79];   // 10..88
        void Set(int addr, ushort v) => regs[addr - 10] = v;
        var type = Encoding.ASCII.GetBytes("AC200PL\0\0\0\0\0");
        for (int i = 0; i < 6; i++) Set(10 + i, (ushort)((type[2 * i] << 8) | type[2 * i + 1]));
        Set(17, 0x5678); Set(18, 0x1234); Set(19, 0x0001); Set(20, 0x0000);
        Set(36, 120); Set(37, 0); Set(38, 345); Set(39, 12); Set(43, 85);
        Set(70, 1); Set(71, 2301); Set(74, 500);
        Set(86, 523); Set(87, 118); Set(88, 225);
        return [.. regs.SelectMany(r => new[] { (byte)(r >> 8), (byte)r })];
    }

    [Fact]
    public void Every_ac200pl_field_decodes_as_the_library_does()
    {
        var v = V1Models.Decode(V1Models.All["AC200PL"], 10, Ac200plBody());
        Assert.Equal("AC200PL", v["DEVICE_TYPE"].Text);
        Assert.Equal((0x5678UL + (0x1234UL << 16) + (1UL << 32)).ToString(System.Globalization.CultureInfo.InvariantCulture), v["DEVICE_SN"].Text);
        Assert.Equal(120, v["DC_INPUT_POWER"].Number);
        Assert.Equal(0, v["AC_INPUT_POWER"].Number);
        Assert.Equal(345, v["AC_OUTPUT_POWER"].Number);
        Assert.Equal(12, v["DC_OUTPUT_POWER"].Number);
        Assert.Equal(85, v["BATTERY_SOC"].Number);
        Assert.Equal("INVERTER_OUTPUT", v["AC_OUTPUT_MODE"].Text);
        Assert.Equal(2301, v["INTERNAL_AC_VOLTAGE"].Number);        // scale 1, multiplier 10: raw
        Assert.Equal(50, v["INTERNAL_AC_FREQUENCY"].Number);        // scale 2, multiplier 10: raw / 10
        Assert.Equal(52.3, v["INTERNAL_DC_INPUT_VOLTAGE"].Number);  // scale 1: raw / 10
        Assert.Equal(118, v["INTERNAL_DC_INPUT_POWER"].Number);     // scale 1, multiplier 10: raw
        Assert.Equal(2.25, v["INTERNAL_DC_INPUT_CURRENT"].Number);  // scale 2: raw / 100
        Assert.Equal(13, v.Count);   // the library's 17 AC200PL fields less its 4 control fields (3007, 3008, 3060, 3061)
    }

    [Fact]
    public void A_field_only_partly_in_the_body_is_not_decoded()
    {
        var body = Ac200plBody()[(2 * (17 - 10))..(2 * (19 - 10))];   // registers 17-18 of the 4-register serial
        Assert.Empty(V1Models.Decode(V1Models.All["AC200PL"], 17, body));
    }

    [Theory]
    [InlineData(101, null)]
    [InlineData(100, 100.0)]
    [InlineData(0, 0.0)]
    public void A_battery_percent_over_100_is_dropped(int raw, double? expected)
    {
        var v = V1Models.Decode(V1Models.All["AC200PL"], 43, [(byte)(raw >> 8), (byte)raw]);
        Assert.Equal(expected, v.TryGetValue("BATTERY_SOC", out var x) ? x.Number : null);
    }

    [Theory]
    [InlineData(0, "STOP")]
    [InlineData(2, "BYPASS_OUTPUT_C")]
    [InlineData(3, "BYPASS_OUTPUT_D")]
    [InlineData(4, "LOAD_MATCHING")]
    [InlineData(5, null)]
    public void The_output_mode_maps_the_library_enum_and_an_unknown_value_is_null(int raw, string? expected) =>
        Assert.Equal(expected, V1Models.Decode(V1Models.All["AC200PL"], 70, [0, (byte)raw])["AC_OUTPUT_MODE"].Text);

    [Fact]
    public void The_reading_takes_its_eight_fields()
    {
        var v = V1Models.Decode(V1Models.All["AC200PL"], 10, Ac200plBody());
        Assert.Equal(new Reading(85, 0, 345, "INVERTER_OUTPUT", 120, 12, 2301, 50), V1Models.ToReading(v));
    }

    [Fact]
    public void No_field_is_no_reading() => Assert.Null(V1Models.ToReading(new Dictionary<string, FieldValue>()));

    [Fact]
    public void The_ac300_scales_differ_from_the_ac200pl()
    {
        var v = V1Models.Decode(V1Models.All["AC300"], 71, [0x08, 0xFD, 0, 0, 0, 0, 0x13, 0x88]);   // 71 = 2301, 74 = 5000
        Assert.Equal(230.1, v["INTERNAL_AC_VOLTAGE"].Number);
        Assert.Equal(50, v["INTERNAL_AC_FREQUENCY"].Number);
    }
}

public sealed class NativeBleConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-watcher-test-cfg-" + Guid.NewGuid().ToString("N"));

    public NativeBleConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Loaded as the service loads it; the data directory is a temp one, so no cloud token is found unless the JSON names one.</summary>
    private StationConfig Load(string json, params string[] args)
    {
        string file = Path.Combine(_dir, "config.json");
        File.WriteAllText(file, json);
        return StationConfig.Load(["--config", file, "--data-dir", _dir, .. args]);
    }

    [Fact]
    public void A_v1_model_with_an_address_is_polled_over_bluetooth()
    {
        var cfg = Load("""{ "mac": "AA:BB:CC:DD:EE:FF" }""");
        Assert.Equal("AC200PL", cfg.Type);
        Assert.Null(cfg.BluetoothProblem);
        Assert.Null(cfg.PollProblem);
        Assert.Null(cfg.ConfigProblem);
        Assert.False(cfg.CloudOnly);
        Assert.Null(cfg.IgnoredKeysNote);
    }

    /// <summary>An old config.json still loads: the Python reader's keys are accepted, ignored and named once in the start log.</summary>
    [Theory]
    [InlineData("python")]
    [InlineData("native")]
    [InlineData("bleak")]
    public void The_old_python_keys_are_accepted_ignored_and_named(string backend)
    {
        var cfg = Load($$"""{ "mac": "AA:BB:CC:DD:EE:FF", "bleBackend": "{{backend}}", "readerPath": "C:\\x\\.venv\\Scripts\\bluetti-read.exe" }""");
        Assert.Null(cfg.ConfigProblem);
        Assert.Null(cfg.PollProblem);
        Assert.Equal("bleBackend, readerPath (or --reader) ignored: since 1.1.0 the built-in Bluetooth reader is the only one (no Python)", cfg.IgnoredKeysNote);
    }

    [Fact]
    public void The_old_reader_flag_is_accepted_and_named()
    {
        var cfg = Load("""{ "mac": "AA:BB:CC:DD:EE:FF" }""", "--reader", @"C:\x\bluetti-read.exe");
        Assert.Null(cfg.ConfigProblem);
        Assert.StartsWith("readerPath (or --reader) ignored", cfg.IgnoredKeysNote, StringComparison.Ordinal);
    }

    /// <summary>The AC300 is read over Bluetooth like every V1 model: its V1 fields, never its battery packs (read only: no pack selector write).</summary>
    [Fact]
    public void The_ac300_is_a_plain_v1_model()
    {
        var cfg = Load("""{ "mac": "AA:BB:CC:DD:EE:FF", "type": "AC300" }""");
        Assert.Null(cfg.ConfigProblem);
        Assert.Null(cfg.PollProblem);
        Assert.DoesNotContain(V1Models.All["AC300"], f => f.Address >= ModbusRead.ControlRegisterFloor);
    }

    [Fact]
    public void An_unknown_model_without_the_cloud_is_not_polled_and_names_the_models_and_the_cloud()
    {
        var cfg = Load("""{ "mac": "AA:BB:CC:DD:EE:FF", "type": "AC999" }""");
        Assert.Equal($"type \"AC999\" is not a model Bluetooth reads ({string.Join(", ", BleModels.Names)}): only the cloud fallback can read it (README, Optional modules)", cfg.BluetoothProblem);
        Assert.Equal(cfg.BluetoothProblem, cfg.PollProblem);
        Assert.Contains("AC999", cfg.ConfigProblem, StringComparison.Ordinal);
        Assert.False(cfg.CloudOnly);
    }

    [Fact]
    public void An_unknown_model_with_the_cloud_is_polled_through_the_cloud_alone_without_an_address()
    {
        var cfg = Load("""{ "type": "AC999", "cloudEnabled": true }""");
        Assert.True(cfg.CloudOnly);
        Assert.Null(cfg.PollProblem);
        Assert.Equal($"type \"AC999\" is not a model Bluetooth reads ({string.Join(", ", BleModels.Names)}): the cloud fallback alone reads it", cfg.ConfigProblem);
    }

    [Fact]
    public void A_v1_model_needs_its_address_even_with_the_cloud()
    {
        var cfg = Load("""{ "cloudEnabled": true }""");
        Assert.False(cfg.CloudOnly);
        Assert.Contains("mac is not set", cfg.PollProblem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "mac": "AA:BB:CC:DD:EE:FF", "type": "AC999" }""", "\"AC999\" is not a model Bluetooth reads")]
    [InlineData("""{ "mac": "not-a-mac" }""", "mac \"not-a-mac\" is not a Bluetooth address")]
    public void An_unknown_model_or_a_bad_address_is_named(string json, string named)
    {
        var cfg = Load(json);
        Assert.Contains(named, cfg.BluetoothProblem, StringComparison.Ordinal);
        Assert.NotNull(cfg.PollProblem);
    }

    [Fact]
    public void The_model_problem_joins_the_missing_mac_problem()
    {
        var cfg = Load("""{ "type": "AC999" }""");
        Assert.Contains("mac is not set", cfg.ConfigProblem, StringComparison.Ordinal);
        Assert.Contains("AC999", cfg.ConfigProblem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF", 0xAABBCCDDEEFFUL)]
    [InlineData("aa-bb-cc-dd-ee-0f", 0xAABBCCDDEE0FUL)]
    public void A_bluetooth_address_parses(string text, ulong expected)
    {
        Assert.True(BleAddress.TryParse(text, out var a));
        Assert.Equal(expected, a);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AA:BB:CC:DD:EE")]
    [InlineData("AA:BB:CC:DD:EE:GG")]
    [InlineData("AABBCCDDEEFF")]
    public void A_bad_address_does_not_parse(string? text) => Assert.False(BleAddress.TryParse(text, out _));
}

/// <summary>A scripted GATT link: each read request is answered by the script; every written frame is kept.</summary>
internal sealed class ScriptedLink(Func<ReadRequest, IEnumerable<byte[]>> script) : IBleLink
{
    private readonly Channel<byte[]> _notes = Channel.CreateUnbounded<byte[]>();
    public List<byte[]> Written { get; } = [];
    public bool Disposed { get; private set; }
    public ChannelReader<byte[]> Notifications => _notes.Reader;

    public Task WriteAsync(ReadRequest request, CancellationToken ct)
    {
        Written.Add(request.Frame.ToArray());
        foreach (var chunk in script(request)) _notes.Writer.TryWrite(chunk);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

/// <summary>A link whose answers arrive after a delay each (zero = at once); a late one lands whenever it is due, as the radio delivers it.</summary>
internal sealed class DelayedLink(Func<ReadRequest, (TimeSpan Delay, byte[][] Chunks)> script) : IBleLink
{
    private readonly Channel<byte[]> _notes = Channel.CreateUnbounded<byte[]>();
    public List<ReadRequest> Written { get; } = [];
    public bool Disposed { get; private set; }
    public ChannelReader<byte[]> Notifications => _notes.Reader;

    public Task WriteAsync(ReadRequest request, CancellationToken ct)
    {
        Written.Add(request);
        var (delay, chunks) = script(request);
        if (delay <= TimeSpan.Zero) foreach (var c in chunks) _notes.Writer.TryWrite(c);
        else _ = Task.Delay(delay, CancellationToken.None).ContinueWith(_ => { foreach (var c in chunks) _notes.Writer.TryWrite(c); }, TaskScheduler.Default);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
}

/// <summary>A connector whose open never finishes and ignores its cancellation, as a stuck WinRT call can.</summary>
internal sealed class HangingConnector : IBleConnector
{
    public Task<BleOpen> OpenAsync(ulong address, CancellationToken ct) => new TaskCompletionSource<BleOpen>().Task;
}

internal sealed class ScriptedConnector(Func<BleOpen> open) : IBleConnector
{
    public int Opens { get; private set; }
    public ulong? Address { get; private set; }

    public Task<BleOpen> OpenAsync(ulong address, CancellationToken ct)
    {
        Opens++;
        Address = address;
        return Task.FromResult(open());
    }
}

public class NativeBleReaderTests
{
    private static StationConfig Cfg(string type = "AC200PL") => new() { Mac = "AA:BB:CC:DD:EE:FF", Type = type, ReaderTimeoutSec = 10 };

    private static ushort[] Regs(int start, int quantity)
    {
        var values = new Dictionary<int, ushort> { [36] = 120, [37] = 0, [38] = 345, [39] = 12, [43] = 85, [70] = 1, [71] = 2301, [74] = 500 };
        return [.. Enumerable.Range(start, quantity).Select(a => values.GetValueOrDefault(a))];
    }

    /// <summary>Answers each request in 20-byte notifications, as a 23-byte ATT MTU delivers them.</summary>
    private static IEnumerable<byte[]> Mtu20(ReadRequest r) => ModbusFrameTests.Answer(Regs(r.Start, r.Quantity)).Chunk(20);

    private static NativeBleReader Reader(IBleConnector c, List<string> log, string type = "AC200PL") =>
        new(Cfg(type), c, log.Add, TimeSpan.FromMilliseconds(300));

    [Fact]
    public async Task One_poll_is_one_connection_two_reads_and_the_full_reading()
    {
        await using var link = new ScriptedLink(Mtu20);
        var conn = new ScriptedConnector(() => new BleOpen(link, null));
        var log = new List<string>();
        var r = await Reader(conn, log).ReadAsync(CancellationToken.None);
        Assert.Equal(new Reading(85, 0, 345, "INVERTER_OUTPUT", 120, 12, 2301, 50), r);
        Assert.Equal(1, conn.Opens);
        Assert.Equal(0xAABBCCDDEEFFUL, conn.Address);
        Assert.Equal(["0103002400080000", "0103004600050000"], link.Written.Select(w => Convert.ToHexString(w[..6]) + "0000"));
        Assert.All(link.Written, w => Assert.Equal(0x03, w[1]));
        Assert.True(link.Disposed);
        Assert.Empty(log);
    }

    [Fact]
    public async Task Unreachable_is_no_reading_logged_once_and_the_cloud_answers()
    {
        var conn = new ScriptedConnector(() => new BleOpen(null, "GATT Unreachable"));
        var log = new List<string>();
        var reader = Reader(conn, log);
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
        Assert.Single(log);
        Assert.Contains("GATT Unreachable", log[0], StringComparison.Ordinal);
        var viaFallback = await new FallbackReader(reader, new QueueReader { Default = new Reading(50, 0, 10, "INVERTER_OUTPUT") }).ReadAsync(CancellationToken.None);
        Assert.Equal("cloud", viaFallback?.Source);
    }

    [Fact]
    public async Task A_recovery_after_a_miss_is_logged()
    {
        bool up = false;
        var conn = new ScriptedConnector(() => up ? new BleOpen(new ScriptedLink(Mtu20), null) : new BleOpen(null, "GATT Unreachable"));
        var log = new List<string>();
        var reader = Reader(conn, log);
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
        up = true;
        Assert.NotNull(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(2, log.Count);
        Assert.Contains("reading again", log[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connector_that_throws_is_no_reading_not_an_exception()
    {
        var conn = new ScriptedConnector(() => throw new InvalidOperationException("radio off"));
        var log = new List<string>();
        Assert.Null(await Reader(conn, log).ReadAsync(CancellationToken.None));
        Assert.Contains("radio off", log[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_silent_station_times_out_per_request_and_is_no_reading()
    {
        await using var link = new ScriptedLink(_ => []);
        var log = new List<string>();
        Assert.Null(await Reader(new ScriptedConnector(() => new BleOpen(link, null)), log).ReadAsync(CancellationToken.None));
        Assert.Equal(2, link.Written.Count);
        Assert.Contains("no complete answer", log[0], StringComparison.Ordinal);
        Assert.True(link.Disposed);
    }

    [Fact]
    public async Task An_exception_reply_to_a_batch_falls_back_to_one_read_per_field()
    {
        await using var link = new ScriptedLink(r => r.Quantity > 1 ? [ModbusFrameTests.ExceptionAnswer(0x02)] : Mtu20(r));
        var log = new List<string>();
        var r = await Reader(new ScriptedConnector(() => new BleOpen(link, null)), log).ReadAsync(CancellationToken.None);
        Assert.Equal(new Reading(85, 0, 345, "INVERTER_OUTPUT", 120, 12, 2301, 50), r);
        Assert.Equal(2 + 8, link.Written.Count);
    }

    [Fact]
    public async Task A_corrupt_answer_leaves_its_fields_null_and_the_rest_stands()
    {
        await using var link = new ScriptedLink(r =>
        {
            var a = ModbusFrameTests.Answer(Regs(r.Start, r.Quantity));
            if (r.Start == 70) a[^1] ^= 0xFF;
            return [a];
        });
        var log = new List<string>();
        var r = await Reader(new ScriptedConnector(() => new BleOpen(link, null)), log).ReadAsync(CancellationToken.None);
        Assert.Equal(new Reading(85, 0, 345, null, 120, 12, null, null), r);
        Assert.Contains("CRC", log[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stale_notification_from_before_the_request_is_dropped()
    {
        await using var link = new ScriptedLink(Mtu20);
        var conn = new ScriptedConnector(() => new BleOpen(link, null));
        var stale = typeof(ScriptedLink).GetField("_notes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(link) as Channel<byte[]>;
        stale!.Writer.TryWrite([0x01, 0x03, 0x02]);
        var r = await Reader(conn, []).ReadAsync(CancellationToken.None);
        Assert.Equal(85, r?.Soc);
    }

    /// <summary>
    /// One-register reads all look alike (byte count 2): the answer to a request that timed out must never be taken as
    /// the next request's. After a timeout the connection is dropped and the next request goes over a new one.
    /// </summary>
    [Fact]
    public async Task A_late_answer_never_fills_the_next_request()
    {
        await using var first = new DelayedLink(r => r.Quantity > 1 ? (TimeSpan.Zero, [ModbusFrameTests.ExceptionAnswer(0x02)])
            : r.Start == 36 ? (TimeSpan.FromMilliseconds(400), [ModbusFrameTests.Answer(Regs(r.Start, r.Quantity))])
            : (TimeSpan.FromMilliseconds(150), [ModbusFrameTests.Answer(Regs(r.Start, r.Quantity))]));
        await using var second = new DelayedLink(r => (TimeSpan.Zero, [.. Mtu20(r)]));
        var links = new Queue<IBleLink>([first, second]);
        var conn = new ScriptedConnector(() => new BleOpen(links.Dequeue(), null));
        var log = new List<string>();
        var r = await Reader(conn, log).ReadAsync(CancellationToken.None);
        // DC_INPUT_POWER (36) timed out: null, never another field's value; every other field is its own.
        Assert.Equal(new Reading(85, 0, 345, "INVERTER_OUTPUT", null, 12, 2301, 50), r);
        Assert.Equal(2, conn.Opens);
        Assert.True(first.Disposed);
        Assert.Equal(36, first.Written[^1].Start);
        Assert.Contains("registers 36+1", string.Join("\n", log), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_invalid_answer_drops_the_connection_too()
    {
        await using var first = new DelayedLink(r => (TimeSpan.Zero, [r.Start == 36 ? [0x01, 0x03, 0x04, 0x00] : ModbusFrameTests.Answer(Regs(r.Start, r.Quantity))]));
        await using var second = new DelayedLink(r => (TimeSpan.Zero, [.. Mtu20(r)]));
        var links = new Queue<IBleLink>([first, second]);
        var conn = new ScriptedConnector(() => new BleOpen(links.Dequeue(), null));
        var r = await Reader(conn, []).ReadAsync(CancellationToken.None);
        Assert.Equal(new Reading(null, null, null, "INVERTER_OUTPUT", null, null, 2301, 50), r);
        Assert.Equal(2, conn.Opens);
        Assert.True(first.Disposed);
        Assert.Single(first.Written);
    }

    /// <summary>A WinRT call that ignores its cancellation cannot hang the poll loop: the whole read is bounded.</summary>
    [Fact]
    public async Task A_bluetooth_call_that_never_returns_is_a_miss_within_the_reader_timeout()
    {
        var log = new List<string>();
        var reader = new NativeBleReader(new StationConfig { Mac = "AA:BB:CC:DD:EE:FF", Type = "AC200PL", ReaderTimeoutSec = 1 }, new HangingConnector(), log.Add);
        var read = reader.ReadAsync(CancellationToken.None);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Same(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)));
        Assert.Null(await read);
        Assert.InRange(sw.Elapsed, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(6));
        Assert.Contains("did not return", log[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_type_never_connects()
    {
        var conn = new ScriptedConnector(() => throw new InvalidOperationException("must not connect"));
        var log = new List<string>();
        Assert.Null(await Reader(conn, log, "AC999").ReadAsync(CancellationToken.None));
        Assert.Equal(0, conn.Opens);
        Assert.Contains("AC999", log[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_eb3a_reads_only_the_base_block()
    {
        await using var link = new ScriptedLink(Mtu20);
        var r = await Reader(new ScriptedConnector(() => new BleOpen(link, null)), [], "EB3A").ReadAsync(CancellationToken.None);
        Assert.Equal(new Reading(85, 0, 345, "INVERTER_OUTPUT", 120, 12, null, null), r);   // no mode field: derived, as the cloud does
        Assert.Single(link.Written);
    }
}
