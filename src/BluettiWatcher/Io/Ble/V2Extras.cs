using System.Globalization;
using BluettiWatcher.Decision;

namespace BluettiWatcher.Io.Ble;

/// <summary>
/// The optional V2 reads (docs/bluetti-protocol.md, sections 2.3, 2.4 and 3) and their decoding. V2 only: on a V1 station
/// (the AC200PL) registers 100-138 carry other data (105-120 are cell voltages), so asking them would decode wrong values.
/// All are function 0x03 reads under register 2000.
/// </summary>
internal static class V2Extras
{
    public const string Battery = "battery";
    public const string StationTime = "stationTime";
    public const string Alarms = "alarms";
    public const string Temperatures = "temperatures";

    /// <summary>In the order asked, after the reading's own reads.</summary>
    public static IReadOnlyList<ExtraBlock> Blocks { get; } =
    [
        new(Battery, 100, 2),
        new(StationTime, 104, 2),
        // 122 (invPowerType, low byte) picks the code table; 126-129 warnings, 133-138 faults.
        new(Alarms, 122, 17),
        new(Temperatures, 1151, 3),
    ];

    /// <summary>The answered blocks' bodies by block name to the values; null when none answered.</summary>
    public static StationExtras? Decode(IReadOnlyDictionary<string, byte[]> bodies)
    {
        if (bodies.Count == 0) return null;
        var x = new StationExtras();
        if (bodies.TryGetValue(Battery, out var b) && b.Length >= 4)
            x = x with { PackVolt = Word(b, 0) / 10.0, PackAmp = Word(b, 1) / 10.0 };
        if (bodies.TryGetValue(StationTime, out var t) && t.Length >= 4)
            x = x with { StationMinutesToFull = Word(t, 0), StationMinutesToEmpty = Word(t, 1) };
        if (bodies.TryGetValue(Alarms, out var a) && a.Length >= 34)
        {
            var (warnings, faults) = DecodeAlarms(a);
            x = x with { Warnings = warnings, Faults = faults };
        }
        if (bodies.TryGetValue(Temperatures, out var c) && c.Length >= 6)
            x = x with { TempAmbient = Celsius(Word(c, 0)), TempInverter = Celsius(Word(c, 1)), TempPvDcDc = Celsius(Word(c, 2)) };
        return x;
    }

    /// <summary>
    /// The app's getAlarmFaultInfo over the 122+17 body: word i (1-based) bit k (0 = least significant) is code
    /// prefix + (first + 16 x (i - 1) + k). invPowerType 3 = high-power table (prefix B, warnings from 97), else low-power
    /// (prefix E, warnings from 113); faults start at 1.
    /// </summary>
    // yagni: every set bit is a code; the app drops a bit whose table entry is empty, and those tables' texts were not resolved.
    internal static (IReadOnlyList<string> Warnings, IReadOnlyList<string> Faults) DecodeAlarms(byte[] body)
    {
        bool high = body[1] == 3;   // 122 low byte
        string prefix = high ? "B" : "E";
        int warnFirst = high ? 97 : 113;
        return (Codes(body, 126 - 122, 4, prefix, warnFirst), Codes(body, 133 - 122, 6, prefix, 1));
    }

    private static List<string> Codes(byte[] body, int firstWord, int words, string prefix, int first)
    {
        var codes = new List<string>();
        for (int i = 0; i < words; i++)
        {
            int word = Word(body, firstWord + i);
            for (int bit = 0; bit < 16; bit++)
                if ((word & (1 << bit)) != 0) codes.Add(string.Create(CultureInfo.InvariantCulture, $"{prefix}{first + 16 * i + bit}"));
        }
        return codes;
    }

    /// <summary>u16 - 40 °C; 0 = no sensor.</summary>
    private static double? Celsius(int raw) => raw == 0 ? null : raw - 40;

    private static int Word(byte[] body, int i) => (body[2 * i] << 8) | body[2 * i + 1];
}
