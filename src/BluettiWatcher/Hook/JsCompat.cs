using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BluettiWatcher.Hook;

/// <summary>
/// The JavaScript semantics the hook's texts and records depend on (it was power-mode.js, and its outputs stay byte for
/// byte): String() of a JSON value, JSON.stringify of a string, String.prototype.trim, Math.round, Number to string,
/// JSON.parse (no BOM, the last of two equal keys), and the `since` grammar.
/// </summary>
internal static partial class JsCompat
{
    /// <summary>UTF-8 that keeps a BOM as U+FEFF and replaces a bad byte with U+FFFD, as node's 'utf8' decoding does.</summary>
    internal static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 1000 };

    [GeneratedRegex(@"^([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2})(?::([0-9]{2})(?:\.[0-9]+)?)?(Z|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex SinceRegex();

    /// <summary>JSON.parse: strict JSON; a leading U+FEFF is a syntax error there, so it is one here.</summary>
    internal static JsonDocument Parse(string text)
    {
        if (text.Length > 0 && text[0] == '\uFEFF') throw new JsonException("a byte order mark is not JSON");
        return JsonDocument.Parse(text, ParseOptions);
    }

    /// <summary><c>obj.name</c>: null when <paramref name="obj"/> is no object or has no such key; of two equal keys the last.</summary>
    internal static JsonElement? Get(JsonElement? obj, string name)
    {
        if (obj is not { ValueKind: JsonValueKind.Object } o) return null;
        JsonElement? found = null;
        foreach (var p in o.EnumerateObject())
        {
            if (p.NameEquals(name)) found = p.Value;
        }
        return found;
    }

    /// <summary>The value when it is a JSON string (<c>typeof x === 'string'</c>), else null.</summary>
    internal static string? Str(JsonElement? e) => e is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;

    /// <summary>The value when it is a JSON number (<c>typeof x === 'number'</c>; 1e400 is Infinity), else null.</summary>
    internal static double? Num(JsonElement? e) =>
        e is { ValueKind: JsonValueKind.Number } n ? double.Parse(n.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture) : null;

    /// <summary><c>String(x)</c> of a JSON value; null (an absent key) is "undefined".</summary>
    internal static string ToJsString(JsonElement? e)
    {
        if (e is not { } v) return "undefined";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => NumberToString(Num(v) ?? double.NaN),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            // Array.prototype.join: null elements print as "", everything else as its String().
            JsonValueKind.Array => string.Join(',', v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Null ? "" : ToJsString(x))),
            _ => "[object Object]",
        };
    }

    /// <summary>Number.prototype.toString(): the shortest digits that round-trip, in JavaScript's notation.</summary>
    internal static string NumberToString(double v)
    {
        if (double.IsNaN(v)) return "NaN";
        if (double.IsInfinity(v)) return v > 0 ? "Infinity" : "-Infinity";
        if (v == 0) return "0";
        string r = Math.Abs(v).ToString("R", CultureInfo.InvariantCulture);
        int e = r.IndexOf('E', StringComparison.Ordinal);
        int exp = e < 0 ? 0 : int.Parse(r[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        string mant = e < 0 ? r : r[..e];
        int dot = mant.IndexOf('.', StringComparison.Ordinal);
        string digits = dot < 0 ? mant : mant.Remove(dot, 1);
        int n = (dot < 0 ? mant.Length : dot) + exp;   // value = 0.digits x 10^n
        int lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') lead++;
        digits = digits[lead..].TrimEnd('0');
        n -= lead;
        int k = digits.Length;
        string body;
        if (k <= n && n <= 21) body = digits + new string('0', n - k);
        else if (0 < n && n <= 21) body = $"{digits[..n]}.{digits[n..]}";
        else if (-6 < n && n <= 0) body = $"0.{new string('0', -n)}{digits}";
        else
        {
            int e1 = n - 1;
            string sign = e1 < 0 ? "-" : "+";
            string tail = string.Create(CultureInfo.InvariantCulture, $"e{sign}{Math.Abs(e1)}");
            body = k == 1 ? digits + tail : $"{digits[0]}.{digits[1..]}{tail}";
        }
        return v < 0 ? "-" + body : body;
    }

    /// <summary><c>Math.round</c>: the nearest integer, a half towards +Infinity.</summary>
    internal static double Round(double x)
    {
        double f = Math.Floor(x);
        return x - f >= 0.5 ? f + 1 : f;
    }

    /// <summary><c>Math.round(x * 100) / 100</c> printed as JavaScript prints it.</summary>
    internal static string Hundredths(double x) => NumberToString(Round(x * 100) / 100);

    /// <summary>String.prototype.trim: ECMAScript white space and line terminators (U+FEFF included, U+0085 not).</summary>
    internal static string Trim(string s)
    {
        int a = 0, b = s.Length;
        while (a < b && IsJsSpace(s[a])) a++;
        while (b > a && IsJsSpace(s[b - 1])) b--;
        return s[a..b];
    }

    private static bool IsJsSpace(char c) =>
        c is '\t' or '\n' or '\v' or '\f' or '\r' or ' ' or '\u00A0' or '\uFEFF' or '\u2028' or '\u2029'
        || (c != '\u0085' && CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator);

    /// <summary>JSON.stringify of a string: quotes, backslash, control characters and lone surrogates escaped, the rest literal.</summary>
    internal static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) Hex(sb, c);
                    else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[i + 1]); i++; }
                    else if (char.IsSurrogate(c)) Hex(sb, c);
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    private static void Hex(StringBuilder sb, char c) => sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));

    /// <summary>
    /// <c>since</c>: YYYY-MM-DDTHH:MM[:SS[.f+]] followed by Z or +HH:MM / -HH:MM, every field in range (year 0 to 9999,
    /// proleptic Gregorian, the offset up to 14:59); the instant in Unix milliseconds, else null.
    /// </summary>
    internal static long? ParseSince(string text)
    {
        var m = SinceRegex().Match(text);
        if (!m.Success) return null;
        int I(int g) => int.Parse(m.Groups[g].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
        int y = I(1), mo = I(2), d = I(3), h = I(4), mi = I(5), s = m.Groups[6].Success ? I(6) : 0;
        if (mo is < 1 or > 12 || d < 1 || d > DaysIn(y, mo) || h > 23 || mi > 59 || s > 59) return null;
        long t = (DaysFromCivil(y, mo, d) * 86400L + h * 3600L + mi * 60L + s) * 1000L;
        string z = m.Groups[7].Value;
        if (z == "Z") return t;
        int oh = int.Parse(z.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        int om = int.Parse(z.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        if (oh > 14 || om > 59) return null;
        return t - (z[0] == '-' ? -1 : 1) * (oh * 60L + om) * 60000L;
    }

    private static bool Leap(long y) => y % 4 == 0 && (y % 100 != 0 || y % 400 == 0);

    private static int DaysIn(int y, int m) => m == 2 ? (Leap(y) ? 29 : 28) : m is 4 or 6 or 9 or 11 ? 30 : 31;

    /// <summary>Days since 1970-01-01 of a proleptic Gregorian date (H. Hinnant's days_from_civil).</summary>
    private static long DaysFromCivil(long y, long m, long d)
    {
        y -= m <= 2 ? 1 : 0;
        long era = (y >= 0 ? y : y - 399) / 400;
        long yoe = y - era * 400;
        long doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;
        long doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        return era * 146097 + doe - 719468;
    }

    /// <summary>The civil date of a day count since 1970-01-01 (H. Hinnant's civil_from_days).</summary>
    private static (long Y, int M, int D) CivilFromDays(long z)
    {
        z += 719468;
        long era = (z >= 0 ? z : z - 146096) / 146097;
        long doe = z - era * 146097;
        long yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
        long doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
        long mp = (5 * doy + 2) / 153;
        int d = (int)(doy - (153 * mp + 2) / 5 + 1);
        int m = (int)(mp < 10 ? mp + 3 : mp - 9);
        return (yoe + era * 400 + (m <= 2 ? 1 : 0), m, d);
    }

    // DateTimeOffset covers years 1 to 9999; a day of margin keeps the zone conversion inside it.
    private const long DayMs = 86_400_000L;
    private static readonly long MinMs = DateTimeOffset.MinValue.ToUnixTimeMilliseconds() + 2 * DayMs;
    private static readonly long MaxMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() - 2 * DayMs;

    /// <summary>An instant as <c>dd.MM.yyyy HH:mm</c> in <paramref name="zone"/>; the year unpadded, as the Date getters print it.</summary>
    internal static string LocalTime(long ms, TimeZoneInfo zone)
    {
        long y;
        int mo, d, h, mi;
        if (ms >= MinMs && ms <= MaxMs)
        {
            var k = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ms), zone);
            (y, mo, d, h, mi) = (k.Year, k.Month, k.Day, k.Hour, k.Minute);
        }
        else
        {
            // Outside DateTime's years: the zone's base offset, by the calendar arithmetic alone.
            long local = ms + (long)zone.BaseUtcOffset.TotalMilliseconds;
            long days = Math.DivRem(local, DayMs, out long rem);
            if (rem < 0) { days--; rem += DayMs; }
            (y, mo, d) = CivilFromDays(days);
            h = (int)(rem / 3_600_000L);
            mi = (int)(rem / 60_000L % 60);
        }
        return string.Create(CultureInfo.InvariantCulture, $"{d:00}.{mo:00}.{y} {h:00}:{mi:00}");
    }
}
