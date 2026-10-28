// Register maps and field decoding ported from bluetti-bt-lib 0.1.8 (base_devices/base_device_v1.py, devices/ac200l.py,
// ac200m.py, ac200pl.py, ac300.py, ac500.py, eb3a.py, ep500.py, ep500p.py, fields/*.py, enums/output_mode.py),
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
using BluettiWatcher.Decision;

namespace BluettiWatcher.Io.Ble;

/// <summary>How a field's registers decode (the library's field classes that the V1 read maps use).</summary>
internal enum FieldKind
{
    /// <summary>UIntField: one big-endian u16.</summary>
    UInt,
    /// <summary>DecimalField: (raw / 10^scale) x multiplier.</summary>
    Decimal,
    /// <summary>EnumField of OutputMode: the member name; an unknown value is null.</summary>
    OutputMode,
    /// <summary>VersionField: (word0 + word1 &lt;&lt; 16) / 100, 2 registers.</summary>
    Version,
    /// <summary>StringField: ASCII, trailing NULs dropped.</summary>
    Ascii,
    /// <summary>SerialNumberField: word0 + word1 &lt;&lt; 16 + word2 &lt;&lt; 32 + word3 &lt;&lt; 48, 4 registers.</summary>
    SerialNumber,
}

/// <summary>One read field: the library's FieldName member, its first register, its size in registers and its decoding.</summary>
internal sealed record V1Field(string Name, int Address, FieldKind Kind, int Size = 1, int Scale = 0, int Multiplier = 1, int? Min = null, int? Max = null);

/// <summary>A decoded value: a number, or text (an enum name, a device type, a serial); both null = the library's None.</summary>
internal readonly record struct FieldValue(double? Number, string? Text);

/// <summary>A contiguous block of registers read with one request.</summary>
internal readonly record struct RegisterRange(int Start, int Quantity);

/// <summary>
/// The IoT protocol V1 stations of the library and the fields they read. Only read fields under register 2000 (the maps end at register 88): the
/// library's switch and select fields (3001 UPS mode, 3007 AC, 3008 DC, 3060 power off, 3061 display...) and the
/// battery-pack reads of the AC300 (which need a write to the pack selector 3006) are left out on purpose.
/// </summary>
internal static class V1Models
{
    private static readonly V1Field[] Base =
    [
        new("DEVICE_TYPE", 10, FieldKind.Ascii, Size: 6),
        new("DEVICE_SN", 17, FieldKind.SerialNumber, Size: 4),
        new("BATTERY_SOC", 43, FieldKind.UInt, Min: 0, Max: 100),
        new("DC_INPUT_POWER", 36, FieldKind.UInt),
        new("AC_INPUT_POWER", 37, FieldKind.UInt),
        new("AC_OUTPUT_POWER", 38, FieldKind.UInt),
        new("DC_OUTPUT_POWER", 39, FieldKind.UInt),
    ];

    private static readonly V1Field[] Versions = [new("VER_ARM", 23, FieldKind.Version, Size: 2), new("VER_DSP", 25, FieldKind.Version, Size: 2)];

    /// <summary>AC200L, AC200M and AC200PL share these.</summary>
    private static readonly V1Field[] Ac200 =
    [
        new("AC_OUTPUT_MODE", 70, FieldKind.OutputMode),
        new("INTERNAL_AC_VOLTAGE", 71, FieldKind.Decimal, Scale: 1, Multiplier: 10),
        new("INTERNAL_AC_FREQUENCY", 74, FieldKind.Decimal, Scale: 2, Multiplier: 10),
        new("INTERNAL_DC_INPUT_VOLTAGE", 86, FieldKind.Decimal, Scale: 1),
        new("INTERNAL_DC_INPUT_POWER", 87, FieldKind.Decimal, Scale: 1, Multiplier: 10),
        new("INTERNAL_DC_INPUT_CURRENT", 88, FieldKind.Decimal, Scale: 2),
    ];

    /// <summary>
    /// The AC200PL's grid input, as the official app reads it in its 70+87 read (not in the library): 77 voltage and 80
    /// frequency, both / 10. Not the library's AC_INPUT_* (80 there is / 100): other names, so no other model reads them.
    /// </summary>
    private static readonly V1Field[] Ac200plGrid =
    [
        new("GRID_INPUT_VOLTAGE", 77, FieldKind.Decimal, Scale: 1),
        new("GRID_INPUT_FREQUENCY", 80, FieldKind.Decimal, Scale: 1),
    ];

    /// <summary>The AC input and PV fields of the AC300, AC500, EP500 and EP500P.</summary>
    private static readonly V1Field[] AcInPv =
    [
        new("AC_INPUT_VOLTAGE", 77, FieldKind.Decimal, Scale: 1),
        new("AC_INPUT_FREQUENCY", 80, FieldKind.Decimal, Scale: 2),
        new("PV_S1_VOLTAGE", 86, FieldKind.Decimal, Scale: 1),
        new("PV_S1_POWER", 87, FieldKind.Decimal, Scale: 1, Multiplier: 10),
        new("PV_S1_CURRENT", 88, FieldKind.Decimal, Scale: 2, Multiplier: 10),
    ];

    private static readonly V1Field[] Ep500 =
    [
        new("AC_OUTPUT_MODE", 70, FieldKind.OutputMode),
        new("AC_OUTPUT_VOLTAGE", 71, FieldKind.Decimal, Scale: 1),
        new("AC_OUTPUT_FREQUENCY", 74, FieldKind.Decimal, Scale: 2),
        .. AcInPv,
    ];

    private static IReadOnlyList<V1Field> Model(params V1Field[] own) => [.. Base.Concat(own).OrderBy(f => f.Address)];

    /// <summary>Model name (as the library's DEVICES keys, case-sensitive) to its read fields, sorted by address.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<V1Field>> All { get; } = new Dictionary<string, IReadOnlyList<V1Field>>(StringComparer.Ordinal)
    {
        ["AC200L"] = Model([.. Versions, .. Ac200]),
        ["AC200M"] = Model(Ac200),
        ["AC200PL"] = Model([.. Ac200, .. Ac200plGrid]),
        ["AC300"] = Model([.. Versions, new("POWER_GENERATION", 43, FieldKind.Decimal, Scale: 1), new("AC_OUTPUT_MODE", 70, FieldKind.OutputMode),
            new("INTERNAL_AC_VOLTAGE", 71, FieldKind.Decimal, Scale: 1), new("INTERNAL_AC_FREQUENCY", 74, FieldKind.Decimal, Scale: 2), .. AcInPv]),
        ["AC500"] = Model([new("AC_OUTPUT_MODE", 70, FieldKind.OutputMode), new("INTERNAL_AC_VOLTAGE", 71, FieldKind.Decimal, Scale: 1, Multiplier: 10),
            new("INTERNAL_AC_FREQUENCY", 74, FieldKind.Decimal, Scale: 2, Multiplier: 10), .. AcInPv]),
        ["EB3A"] = Model([.. Versions, new("AC_INPUT_VOLTAGE", 77, FieldKind.Decimal, Scale: 1), new("DC_INPUT_VOLTAGE", 86, FieldKind.Decimal, Scale: 2)]),
        ["EP500"] = Model(Ep500),
        ["EP500P"] = Model(Ep500),
    };


    /// <summary>The library's OutputMode enum (enums/output_mode.py).</summary>
    private static readonly string[] OutputModes = ["STOP", "INVERTER_OUTPUT", "BYPASS_OUTPUT_C", "BYPASS_OUTPUT_D", "LOAD_MATCHING"];

    /// <summary>The fields a <see cref="Reading"/> carries.</summary>
    private static readonly string[] ReadingNames =
        ["BATTERY_SOC", "AC_INPUT_POWER", "AC_OUTPUT_POWER", "AC_OUTPUT_MODE", "DC_INPUT_POWER", "DC_OUTPUT_POWER", "INTERNAL_AC_VOLTAGE", "INTERNAL_AC_FREQUENCY",
            "GRID_INPUT_VOLTAGE", "GRID_INPUT_FREQUENCY"];

    /// <summary>The largest run of unused registers a batched read spans rather than starting a new request.</summary>
    private const int MaxGap = 4;

    public static IReadOnlyList<V1Field> ReadingFields(IReadOnlyList<V1Field> model) => [.. model.Where(f => ReadingNames.Contains(f.Name, StringComparer.Ordinal))];

    /// <summary>The fields as few contiguous reads: a gap of up to <see cref="MaxGap"/> registers is read through, no read is over <see cref="ModbusRead.MaxQuantity"/>.</summary>
    public static IReadOnlyList<RegisterRange> Plan(IEnumerable<V1Field> fields)
    {
        var ranges = new List<RegisterRange>();
        int start = -1, end = -1;
        foreach (var f in fields.OrderBy(f => f.Address))
        {
            int fEnd = f.Address + f.Size - 1;
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
    public static Dictionary<string, FieldValue> Decode(IReadOnlyList<V1Field> fields, int start, ReadOnlySpan<byte> body)
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

    private static readonly double[] Pow10 = [1, 10, 100, 1000];

    /// <summary>One field's value; null = out of its range (dropped, as the library's in_range).</summary>
    private static int Word(ReadOnlySpan<byte> data, int i) => (data[2 * i] << 8) | data[2 * i + 1];

    private static FieldValue? Parse(V1Field f, ReadOnlySpan<byte> data)
    {
        switch (f.Kind)
        {
            case FieldKind.UInt:
                int raw = Word(data, 0) * f.Multiplier;
                return (f.Min is { } min && raw < min) || (f.Max is { } max && raw > max) ? null : new FieldValue(raw, null);
            case FieldKind.Decimal:
                // One rounding: the exact quotient the library's Decimal gives, to the nearest double.
                return new FieldValue((double)(Word(data, 0) * f.Multiplier) / Pow10[f.Scale], null);
            case FieldKind.OutputMode:
                int mode = Word(data, 0);
                return new FieldValue(null, mode < OutputModes.Length ? OutputModes[mode] : null);
            case FieldKind.Version:
                return new FieldValue((Word(data, 0) + ((long)Word(data, 1) << 16)) / 100.0, null);
            case FieldKind.Ascii:
                var bytes = data.TrimEnd((byte)0);
                var sb = new StringBuilder(bytes.Length);
                foreach (byte b in bytes) if (b < 0x80) sb.Append((char)b);   // decode("ascii", errors="ignore")
                return new FieldValue(null, sb.ToString());
            case FieldKind.SerialNumber:
                ulong sn = 0;
                for (int i = 0; i < 4; i++) sn += (ulong)Word(data, i) << (16 * i);
                return new FieldValue(null, sn.ToString(CultureInfo.InvariantCulture));
            default:
                throw new ArgumentOutOfRangeException(nameof(f), f.Kind, "unknown field kind");
        }
    }

    /// <summary>The mode of a Bluetooth reading with AC input power above 0 W, for a model with no output-mode field (the cloud's CLOUD_MAINS).</summary>
    public const string DerivedMains = "BLE_MAINS";

    /// <summary>
    /// The reading from the decoded fields; no field at all = no answer. <paramref name="deriveMode"/> (a model with no output-mode
    /// field) derives the mode as the cloud reader does: AC input above 0 W = <see cref="DerivedMains"/>; 0 W in while the AC output
    /// feeds a load = INVERTER_OUTPUT; else unknown (null).
    /// </summary>
    public static Reading? ToReading(IReadOnlyDictionary<string, FieldValue> v, bool deriveMode = false)
    {
        if (v.Count == 0) return null;
        double? Num(string k) => v.TryGetValue(k, out var x) ? x.Number : null;
        double? inAc = Num("AC_INPUT_POWER"), outAc = Num("AC_OUTPUT_POWER");
        string? mode = deriveMode
            ? inAc > 0 ? DerivedMains : inAc == 0 && outAc > 0 ? "INVERTER_OUTPUT" : null
            : v.TryGetValue("AC_OUTPUT_MODE", out var m) ? m.Text : null;
        return new Reading(Num("BATTERY_SOC"), inAc, outAc, mode,
            Num("DC_INPUT_POWER"), Num("DC_OUTPUT_POWER"), Num("INTERNAL_AC_VOLTAGE"), Num("INTERNAL_AC_FREQUENCY"),
            GridVolt: Num("GRID_INPUT_VOLTAGE"), GridHz: Num("GRID_INPUT_FREQUENCY"));
    }
}

/// <summary>A Bluetooth address as six hex pairs separated by ':' or '-'.</summary>
internal static class BleAddress
{
    public static bool TryParse(string? text, out ulong address)
    {
        address = 0;
        if (text is null) return false;
        var parts = text.Trim().Split(':', '-');
        if (parts.Length != 6) return false;
        foreach (var p in parts)
        {
            if (p.Length != 2 || !byte.TryParse(p, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte b)) return false;
            address = (address << 8) | b;
        }
        return true;
    }
}
