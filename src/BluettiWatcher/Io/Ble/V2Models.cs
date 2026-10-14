// Register maps and field decoding ported from bluetti-bt-lib 0.1.8 (base_devices/base_device_v2.py, devices/ac2a.py,
// ac2p.py, ac50b.py, ac60.py, ac60p.py, ac70.py, ac70p.py, ac180.py, ac180p.py, ac180t.py, ap300.py, el10.py, el30v2.py,
// el100v2.py, ep600.py, ep760.py, ep800.py, ep2000.py, handsfree1.py, pr30v2.py, pr100v2.py, fields/*.py),
// https://github.com/Patrick762/bluetti-bt-lib, under this notice:
//
// MIT License
//
// Copyright (c) 2025 Patrick762
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System.Globalization;
using System.Text;

namespace BluettiWatcher.Io.Ble;

/// <summary>How a V2 field's registers decode (the library's field classes that the V2 read maps use).</summary>
internal enum V2Kind
{
    /// <summary>UIntField: one big-endian u16; with a multiplier other than 1, round(raw x multiplier, 2).</summary>
    UInt,
    /// <summary>DecimalField: (raw / 10^scale) x multiplier.</summary>
    Decimal,
    /// <summary>BoolField: 0 false, 1 true, anything else the library's None.</summary>
    Bool,
    /// <summary>SwapStringField: the bytes of each register swapped, then ASCII with trailing NULs dropped.</summary>
    SwapAscii,
    /// <summary>SerialNumberField: word0 + word1 &lt;&lt; 16 + word2 &lt;&lt; 32 + word3 &lt;&lt; 48, 4 registers.</summary>
    SerialNumber,
}

/// <summary>
/// One V2 read field: the library's FieldName member, its first register, its size and its decoding. The multiplier is
/// the fraction <see cref="Multiplier"/> / <see cref="Divisor"/> (the library's 0.1 and 1/60 are 1/10 and 1/60).
/// </summary>
internal sealed record V2Field(string Name, int Address, V2Kind Kind, int Size = 1, int Scale = 0, int Multiplier = 1, int Divisor = 1, int? Min = null, int? Max = null);

/// <summary>
/// The IoT protocol V2 stations of the library and the fields they read. The V2 frame is the V1 frame (the library builds
/// both with registers/ReadableRegisters.py); only the register map differs. Only read fields under register
/// <see cref="ControlRegisterFloor"/>: the library's V2 switch and select fields (2011 AC, 2012 DC, 2014-2021 eco and
/// charging, 2067 display, 2208, 2246...), the settings next to them (2022, 2023, 2435-2438), the BMS version (6175) and the
/// Wi-Fi name (12002) are left out on purpose. No V2 model has a pack selector or battery-pack reads in the library.
/// </summary>
internal static class V2Models
{
    /// <summary>The first control register of the V2 stations (AC switch 2011): no V2 read reaches it.</summary>
    public const int ControlRegisterFloor = ModbusRead.ControlRegisterFloor;

    private static readonly V2Field[] Base =
    [
        new("DEVICE_TYPE", 110, V2Kind.SwapAscii, Size: 6),
        new("DEVICE_SN", 116, V2Kind.SerialNumber, Size: 4),
        new("BATTERY_SOC", 102, V2Kind.UInt, Min: 0, Max: 100),
    ];

    private static readonly V2Field DcOut = new("DC_OUTPUT_POWER", 140, V2Kind.UInt);
    private static readonly V2Field AcOut = new("AC_OUTPUT_POWER", 142, V2Kind.UInt);
    private static readonly V2Field DcIn = new("DC_INPUT_POWER", 144, V2Kind.UInt);
    private static readonly V2Field AcIn = new("AC_INPUT_POWER", 146, V2Kind.UInt);
    private static readonly V2Field[] Power = [DcOut, AcOut, DcIn, AcIn];

    private static V2Field Dec(string name, int address, int scale = 1) => new(name, address, V2Kind.Decimal, Scale: scale);
    private static V2Field UInt(string name, int address) => new(name, address, V2Kind.UInt);

    private static readonly V2Field TimeRemaining = Dec("TIME_REMAINING", 104);
    private static readonly V2Field AcInVoltageDec = Dec("AC_INPUT_VOLTAGE", 1314);
    /// <summary>UIntField(AC_INPUT_VOLTAGE, 1314, 0.1) of the AC180 and AC70P.</summary>
    private static readonly V2Field AcInVoltageTenth = new("AC_INPUT_VOLTAGE", 1314, V2Kind.UInt, Divisor: 10);

    /// <summary>The DC input, AC input and AC output details of the AC70, AC180T and Handsfree 1 (all DecimalField, scale 1).</summary>
    private static V2Field[] Details(V2Field acInVoltage) =>
    [
        Dec("DC_INPUT_VOLTAGE", 1213), Dec("DC_INPUT_CURRENT", 1214), Dec("AC_INPUT_FREQUENCY", 1300), acInVoltage,
        Dec("AC_INPUT_CURRENT", 1315), Dec("AC_OUTPUT_FREQUENCY", 1500), Dec("AC_OUTPUT_VOLTAGE", 1511),
    ];

    /// <summary>EL10 and EL100V2: TIME_REMAINING is DecimalField(104, scale 0, multiplier 1/60).</summary>
    private static readonly V2Field[] El =
    [
        new("TIME_REMAINING", 104, V2Kind.Decimal, Divisor: 60), .. Power,
        AcInVoltageDec, Dec("AC_INPUT_CURRENT", 1315), Dec("AC_OUTPUT_VOLTAGE", 1511),
    ];

    private static readonly V2Field[] Pv =
    [
        UInt("PV_S1_POWER", 1212), Dec("PV_S1_VOLTAGE", 1213), Dec("PV_S1_CURRENT", 1214),
        UInt("PV_S2_POWER", 1220), Dec("PV_S2_VOLTAGE", 1221), Dec("PV_S2_CURRENT", 1222),
    ];

    /// <summary>Smart meter (SM), grid and AC output phase n of the EP600 and EP760: power UInt, voltage and current as the library declares.</summary>
    private static V2Field[] Meter(int n, int at) => [UInt($"SM_P{n}_POWER", at), Dec($"SM_P{n}_VOLTAGE", at + 1), UInt($"SM_P{n}_CURRENT", at + 2)];
    private static V2Field[] Grid(int n, int at) => [UInt($"GRID_P{n}_POWER", at), Dec($"GRID_P{n}_VOLTAGE", at + 1), Dec($"GRID_P{n}_CURRENT", at + 2)];
    private static V2Field[] AcPhase(int n, int at) => [UInt($"AC_P{n}_POWER", at), Dec($"AC_P{n}_VOLTAGE", at + 1), Dec($"AC_P{n}_CURRENT", at + 2)];

    private static IReadOnlyList<V2Field> Model(params V2Field[] own) => [.. Base.Concat(own).OrderBy(f => f.Address)];

    /// <summary>Model name (as the library's DEVICES keys, case-sensitive) to its read fields, sorted by address.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<V2Field>> All { get; } = new Dictionary<string, IReadOnlyList<V2Field>>(StringComparer.Ordinal)
    {
        ["AC2A"] = Model([.. Power, Dec("POWER_GENERATION", 154), new("AC_OUTPUT_ON", 1509, V2Kind.Bool)]),
        ["AC2P"] = Model([.. Power, Dec("POWER_GENERATION", 154)]),
        ["AC50B"] = Model(TimeRemaining, DcOut, AcOut, AcIn),
        ["AC60"] = Model([.. Power, UInt("AC_INPUT_VOLTAGE", 1314)]),
        ["AC60P"] = Model([.. Power, UInt("AC_INPUT_VOLTAGE", 1314)]),
        ["AC70"] = Model([TimeRemaining, .. Power, .. Details(AcInVoltageDec)]),
        ["AC70P"] = Model([.. Power, .. Details(AcInVoltageTenth)]),
        ["AC180"] = Model([.. Power, .. Details(AcInVoltageTenth)]),
        ["AC180T"] = Model([.. Power, .. Details(AcInVoltageDec)]),
        ["AC180P"] = Model([.. Power, AcInVoltageDec]),
        ["AP300"] = Model([.. Power, AcInVoltageDec]),
        ["EL10"] = Model(El),
        ["EL30V2"] = Model([new("TIME_REMAINING", 104, V2Kind.Decimal, Scale: 4, Multiplier: 167), .. Power, AcInVoltageDec]),
        ["EL100V2"] = Model(El),
        ["EP600"] = Model([Dec("POWER_GENERATION", 1202), .. Pv, .. Meter(1, 1228), .. Meter(2, 1236), .. Meter(3, 1244), Dec("GRID_FREQUENCY", 1300),
            .. Grid(1, 1313), .. Grid(2, 1319), .. Grid(3, 1325), Dec("AC_OUTPUT_FREQUENCY", 1500), .. AcPhase(1, 1510), .. AcPhase(2, 1517), .. AcPhase(3, 1524)]),
        ["EP760"] = Model([.. Pv, .. Meter(1, 1228), Dec("GRID_FREQUENCY", 1300), .. Grid(1, 1313), Dec("AC_OUTPUT_FREQUENCY", 1500), .. AcPhase(1, 1510)]),
        ["EP800"] = Model(),
        ["EP2000"] = Model([.. Pv, Dec("GRID_FREQUENCY", 1300), Dec("GRID_P1_VOLTAGE", 1314), Dec("GRID_P2_VOLTAGE", 1320), Dec("GRID_P3_VOLTAGE", 1326),
            Dec("AC_OUTPUT_FREQUENCY", 1500), Dec("AC_P1_VOLTAGE", 1511), Dec("AC_P2_VOLTAGE", 1518), Dec("AC_P3_VOLTAGE", 1525)]),
        ["Handsfree 1"] = Model([TimeRemaining, .. Power, .. Details(AcInVoltageDec)]),
        ["PR30V2"] = Model([.. Power, AcInVoltageDec]),
        ["PR100V2"] = Model([.. Power, AcInVoltageDec]),
    };

    /// <summary>The fields a <see cref="Decision.Reading"/> carries ; V2 maps have no output mode or internal AC fields.</summary>
    private static readonly string[] ReadingNames =
        ["BATTERY_SOC", "AC_INPUT_POWER", "AC_OUTPUT_POWER", "AC_OUTPUT_MODE", "DC_INPUT_POWER", "DC_OUTPUT_POWER", "INTERNAL_AC_VOLTAGE", "INTERNAL_AC_FREQUENCY"];

    /// <summary>The largest run of unused registers a batched read spans rather than starting a new request (as the V1 plan).</summary>
    private const int MaxGap = 4;

    public static IReadOnlyList<V2Field> ReadingFields(IReadOnlyList<V2Field> model) => [.. model.Where(f => ReadingNames.Contains(f.Name, StringComparer.Ordinal))];

    /// <summary>
    /// The fields as few contiguous reads: a gap of up to <see cref="MaxGap"/> registers is read through, no read is over
    /// <see cref="ModbusRead.MaxQuantity"/>, and none reaches <see cref="ControlRegisterFloor"/> (refused, not trimmed).
    /// </summary>
    public static IReadOnlyList<RegisterRange> Plan(IEnumerable<V2Field> fields)
    {
        var ranges = new List<RegisterRange>();
        int start = -1, end = -1;
        foreach (var f in fields.OrderBy(f => f.Address))
        {
            int fEnd = f.Address + f.Size - 1;
            if (f.Address < 0 || fEnd >= ControlRegisterFloor)
                throw new ArgumentOutOfRangeException(nameof(fields), f.Address, string.Create(CultureInfo.InvariantCulture, $"read only: V2 registers {ControlRegisterFloor} and up are never touched ({f.Name})"));
            if (start >= 0 && f.Address - end - 1 <= MaxGap && Math.Max(end, fEnd) - start + 1 <= ModbusRead.MaxQuantity)
            {
                end = Math.Max(end, fEnd);
                continue;
            }
            if (start >= 0) ranges.Add(new(start, end - start + 1));
            start = f.Address;
            end = fEnd;
        }
        if (start >= 0) ranges.Add(new(start, end - start + 1));
        return ranges;
    }

    /// <summary>
    /// The library's parse: every field wholly inside the registers read from <paramref name="start"/> is decoded; a value
    /// outside its field's range is dropped.
    /// </summary>
    public static Dictionary<string, FieldValue> Decode(IReadOnlyList<V2Field> fields, int start, ReadOnlySpan<byte> body)
    {
        int count = body.Length / 2;
        var parsed = new Dictionary<string, FieldValue>(StringComparer.Ordinal);
        foreach (var f in fields)
        {
            if (f.Address < start || f.Address + f.Size - 1 >= start + count) continue;
            var data = body.Slice(2 * (f.Address - start), 2 * f.Size);
            if (Parse(f, data) is { } value) parsed[f.Name] = value;
        }
        return parsed;
    }

    private static readonly double[] Pow10 = [1, 10, 100, 1000, 10000];

    private static int Word(ReadOnlySpan<byte> data, int i) => (data[2 * i] << 8) | data[2 * i + 1];

    /// <summary>One field's value; null = out of its range (dropped, as the library's in_range).</summary>
    private static FieldValue? Parse(V2Field f, ReadOnlySpan<byte> data)
    {
        switch (f.Kind)
        {
            case V2Kind.UInt:
                int raw = Word(data, 0);
                // UIntField: round(raw * multiplier, 2) only when the multiplier is not 1.
                double value = f.Multiplier == 1 && f.Divisor == 1 ? raw : Math.Round((double)raw * f.Multiplier / f.Divisor, 2, MidpointRounding.ToEven);
                return (f.Min is { } min && value < min) || (f.Max is { } max && value > max) ? null : new FieldValue(value, null);
            case V2Kind.Decimal:
                // One rounding: the exact quotient the library's Decimal gives, to the nearest double.
                return new FieldValue((double)((long)Word(data, 0) * f.Multiplier) / (f.Divisor * Pow10[f.Scale]), null);
            case V2Kind.Bool:
                int b = Word(data, 0);
                return new FieldValue(b is 0 or 1 ? b : null, null);
            case V2Kind.SwapAscii:
                var swapped = new byte[data.Length];
                for (int i = 0; i + 1 < data.Length; i += 2)
                {
                    swapped[i] = data[i + 1];
                    swapped[i + 1] = data[i];
                }
                var bytes = swapped.AsSpan().TrimEnd((byte)0);
                var sb = new StringBuilder(bytes.Length);
                foreach (byte c in bytes) if (c < 0x80) sb.Append((char)c);   // decode("ascii", errors="ignore")
                return new FieldValue(null, sb.ToString());
            case V2Kind.SerialNumber:
                ulong sn = 0;
                for (int i = 0; i < 4; i++) sn += (ulong)Word(data, i) << (16 * i);
                return new FieldValue(null, sn.ToString(CultureInfo.InvariantCulture));
            default:
                throw new ArgumentOutOfRangeException(nameof(f), f.Kind, "unknown field kind");
        }
    }
}
