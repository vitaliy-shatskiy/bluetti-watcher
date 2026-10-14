using System.Text.Json;
using BluettiWatcher.Hook;

namespace BluettiWatcher.Tests;

/// <summary>The hook's seams beyond the JavaScript cases: the command word, the built-in rules, JavaScript's number and string forms, the environment.</summary>
public sealed class HookCompatTests
{
    [Fact]
    public void The_word_hook_runs_the_hook_whatever_follows_and_help_still_wins()
    {
        Assert.Equal(CliAction.Hook, Cli.Parse(["hook"]).Action);
        Assert.Equal(CliAction.Hook, Cli.Parse(["hook", "--bogus"]).Action);
        Assert.Equal(CliAction.Help, Cli.Parse(["hook", "--help"]).Action);
        Assert.Equal(CliAction.Error, Cli.Parse(["--port", "1", "hook"]).Action);
        Assert.Contains("BluettiWatcher.exe hook", Cli.Usage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("power-mode.rules.md")]
    [InlineData("power-mode.eco.rules.md")]
    [InlineData("power-mode.stop.rules.md")]
    [InlineData("power-mode.hibernate.rules.md")]
    public void The_built_in_rules_are_the_repository_files(string name)
    {
        string? text = HookWorld.EmbeddedRules(name);
        Assert.NotNull(text);
        Assert.Equal(HookPowerModeTests.Source(name), JsCompat.Trim(text.Replace("\r\n", "\n", StringComparison.Ordinal)));
        Assert.Null(HookWorld.EmbeddedRules("station.md"));
    }

    [Theory]
    [InlineData(12345, "12345")]
    [InlineData(18.5, "18.5")]
    [InlineData(0.1, "0.1")]
    [InlineData(-0.0, "0")]
    [InlineData(1e21, "1e+21")]
    [InlineData(1e20, "100000000000000000000")]
    [InlineData(1e15, "1000000000000000")]
    [InlineData(1.5e-5, "0.000015")]
    [InlineData(1e-7, "1e-7")]
    [InlineData(1.25e-7, "1.25e-7")]
    [InlineData(-2.5, "-2.5")]
    [InlineData(123456789012345680000.0, "123456789012345680000")]
    public void Numbers_print_as_JavaScript_prints_them(double v, string want) => Assert.Equal(want, JsCompat.NumberToString(v));

    [Theory]
    [InlineData(18.499, "18.5")]
    [InlineData(4.005, "4.01")]
    [InlineData(0.125, "0.13")]
    [InlineData(100, "100")]
    public void Hundredths_round_half_up_as_Math_round(double v, string want) => Assert.Equal(want, JsCompat.Hundredths(v));

    [Fact]
    public void Strings_quote_as_JSON_stringify_does()
    {
        Assert.Equal("\"a\\\"b\\\\c\\n\\r\\t\\b\\f\\u0001\u00e9\u2028/<>\"", JsCompat.Quote("a\"b\\c\n\r\t\b\f\u0001\u00e9\u2028/<>"));
        Assert.Equal("\"\\ud800x\\udc00\"", JsCompat.Quote("\ud800x\udc00"));
        Assert.Equal("\"\U0001F600\"", JsCompat.Quote("\U0001F600"));
    }

    [Fact]
    public void Trim_is_ECMAScript_trim()
    {
        Assert.Equal("x", JsCompat.Trim("\uFEFF\u00A0\u3000 x\u2029\n"));
        Assert.Equal("\u0085x\u0085", JsCompat.Trim("\u0085x\u0085"));
    }

    [Fact]
    public void Of_two_equal_keys_the_last_counts_and_String_of_an_array_joins()
    {
        using var doc = JsCompat.Parse("{\"mode\":\"normal\",\"mode\":\"emergency\",\"since\":[\"2026-10-03T01:20:00+03:00\"],\"n\":[1,null,\"x\"]}");
        Assert.Equal("emergency", JsCompat.Str(JsCompat.Get(doc.RootElement, "mode")));
        Assert.Equal("2026-10-03T01:20:00+03:00", JsCompat.ToJsString(JsCompat.Get(doc.RootElement, "since")));
        Assert.Equal("1,,x", JsCompat.ToJsString(JsCompat.Get(doc.RootElement, "n")));
        Assert.Equal("undefined", JsCompat.ToJsString(JsCompat.Get(doc.RootElement, "until")));
        Assert.Throws<JsonException>(() => JsCompat.Parse("\uFEFF{}"));
    }

    [Theory]
    [InlineData("2026-10-03T01:20:00+03:00", 1_790_979_600_000L)]
    [InlineData("2024-02-29T00:00Z", 1_709_164_800_000L)]
    [InlineData("0000-01-01T00:00:00Z", -62_167_219_200_000L)]
    [InlineData("2026-10-03T01:20:00+14:59", 1_790_936_460_000L)]
    public void Since_parses_to_the_instant(string since, long ms) => Assert.Equal(ms, JsCompat.ParseSince(since));

    [Theory]
    [InlineData("2026-10-03T24:00:00Z")]
    [InlineData("2026-13-01T00:00:00Z")]
    [InlineData("2025-02-29T00:00:00Z")]
    [InlineData("2026-10-03T01:20:60Z")]
    [InlineData("2026-10-03T01:20:00+15:00")]
    [InlineData("2026-10-03T01:20:00+03:60")]
    [InlineData("2026-10-03T01:20:00Z\n")]
    [InlineData("2026-10-03T01:20:00z")]
    [InlineData("\u0662026-10-03T01:20:00Z")]
    public void A_since_out_of_range_is_none(string since) => Assert.Null(JsCompat.ParseSince(since));

    [Fact]
    public void A_year_outside_DateTime_still_prints()
    {
        var utc = TimeZoneInfo.Utc;
        Assert.Equal("01.01.0 00:00", JsCompat.LocalTime(-62_167_219_200_000L, utc));
        Assert.Equal("03.10.2026 01:20", JsCompat.LocalTime(1_790_979_600_000L, TimeZoneInfo.FindSystemTimeZoneById("FLE Standard Time")));
    }

    [Fact]
    public void Numeric_and_long_session_ids_map_as_the_JavaScript_did()
    {
        using var doc = JsCompat.Parse("[12345, 1.5, 1e21, \"a.b\", \"\", true]");
        var ids = doc.RootElement.EnumerateArray().Select(e => PowerModeHook.SessionKey(e)).ToArray();
        Assert.Equal(new string?[] { "12345", "1_5", "1e_21", "a_b", null, null }, ids);
        Assert.Null(PowerModeHook.SessionKey(null));
        Assert.Equal("h" + PowerModeHook.ShortHash(new string('x', 65)), PowerModeHook.SessionKey(JsCompat.Parse($"\"{new string('x', 65)}\"").RootElement));
    }

    [Fact]
    public void The_environment_reads_as_node_read_it()
    {
        Assert.Equal(@"C:\ProgramData\BluettiWatcher\power-mode.json", HookWorld.FlagFileOf(null, null, null));
        Assert.Equal(@"D:\pd\BluettiWatcher\power-mode.json", HookWorld.FlagFileOf(null, "D:/pd", @"C:\x"));
        Assert.Equal(@"C:\x\BluettiWatcher\power-mode.json", HookWorld.FlagFileOf(null, null, @"C:\x"));
        Assert.Equal(@"D:\power\power-mode.json", HookWorld.FlagFileOf(@"D:\power\x\..\power-mode.json\", null, null));
        Assert.Equal(@"C:\t", HookWorld.TempDirOf(@"C:\t\", @"C:\u", null, null));
        Assert.Equal(@"C:\u", HookWorld.TempDirOf(null, @"C:\u", null, null));
        Assert.Equal(@"C:\", HookWorld.TempDirOf(@"C:\", null, null, null));
        Assert.Equal(@"C:\Windows\temp", HookWorld.TempDirOf(null, null, @"C:\Windows", null));
        Assert.Equal("Tokyo Standard Time", HookWorld.ZoneOf("No/Such_Zone,Tokyo Standard Time", "FLE Standard Time").Id);
        Assert.Equal("FLE Standard Time", HookWorld.ZoneOf(null, "FLE Standard Time").Id);
        Assert.Equal(TimeZoneInfo.Local.Id, HookWorld.ZoneOf("No/Such_Zone", null).Id);
    }
}
