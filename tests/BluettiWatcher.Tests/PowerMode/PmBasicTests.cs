// power-mode.ps1 through pwsh: every exit code, the flag file's bytes, the atomic write, the -Path / POWER_MODE_FILE override.
// A port of the first section of power-mode/power-mode.test.js, test for test (DisplayName = the JS test's name). Each class
// is its own xunit collection, so the classes run in parallel and the tests inside one class one after another.
using System.Globalization;
using static BluettiWatcher.Tests.PowerModeScript.Pm;

namespace BluettiWatcher.Tests.PowerModeScript;

public sealed class PowerModeBasicATests
{
    [Fact(DisplayName = "status: a missing flag file is normal, exit 0")]
    public async Task Status_a_missing_flag_is_normal()
    {
        await using Cleanup t = new();
        World w = NewWorld(t);
        Eq(new Res(0, "power mode: normal", ""), await CliAsync(["status", "-Path", w.Flag]));
        Assert.Empty(Ls(w.Dir));
    }

    [Fact(DisplayName = "on: writes the flag (LF, no BOM, one line), exit 0; status then exits 1 with the local-time line")]
    public async Task On_writes_the_flag()
    {
        await using Cleanup t = new();
        World w = NewWorld(t);
        Res on = await CliAsync(["on", "-Path", w.Flag]);
        Eq(0, on.Status, on.Out);
        Match(EmergencyRe, on.Out);
        byte[] bytes = ReadBytes(w.Flag);
        Assert.NotEqual(0xEF, (int)bytes[0]);
        string text = ReadText(w.Flag);
        System.Text.RegularExpressions.Match m = FlagRe.Match(text);
        Ok(m.Success, text);
        Eq("emergency", m.Groups[1].Value);
        Ok(Math.Abs(ParseMs(m.Groups[2].Value) - Now()) < 120000, m.Groups[2].Value);
        EqL(["power-mode.json"], Ls(w.Dir)); // the temp file is gone
        Res st = await CliAsync(["status", "-Path", w.Flag]);
        Eq(1, st.Status);
        Eq(on.Out, st.Out);
    }

    [Fact(DisplayName = "on while already on keeps since and says so")]
    public async Task On_while_already_on_keeps_since()
    {
        await using Cleanup t = new();
        World w = NewWorld(t);
        WriteText(w.Flag, OldEmergency);
        byte[] before = ReadBytes(w.Flag);
        Eq(new Res(0, "power mode: EMERGENCY since 03.10.2026 01:20 (already on, since kept)", ""), await CliAsync(["on", "-Path", w.Flag]));
        Assert.Equal(before, ReadBytes(w.Flag));
    }
}

public sealed class PowerModeBasicBTests
{
    [Fact(DisplayName = "off: back to normal with a new since, exit 0; status exits 0; off again keeps since and says so")]
    public async Task Off_back_to_normal()
    {
        await using Cleanup t = new();
        World w = NewWorld(t);
        WriteText(w.Flag, OldEmergency);
        Eq(new Res(0, "power mode: normal", ""), await CliAsync(["off", "-Path", w.Flag]));
        string text = ReadText(w.Flag);
        System.Text.RegularExpressions.Match m = FlagRe.Match(text);
        Ok(m.Success, text);
        Eq("normal", m.Groups[1].Value);
        Assert.NotEqual(OldSince, m.Groups[2].Value);
        EqL(["power-mode.json"], Ls(w.Dir));
        Eq(new Res(0, "power mode: normal", ""), await CliAsync(["status", "-Path", w.Flag]));
        byte[] before = ReadBytes(w.Flag);
        Eq(new Res(0, "power mode: normal (already off, since kept)", ""), await CliAsync(["off", "-Path", w.Flag]));
        Assert.Equal(before, ReadBytes(w.Flag));
    }

    [Fact(DisplayName = "off with no flag file is already off and writes nothing")]
    public async Task Off_with_no_flag_file_writes_nothing()
    {
        await using Cleanup t = new();
        World w = NewWorld(t);
        Eq(new Res(0, "power mode: normal (already off, since kept)", ""), await CliAsync(["off", "-Path", w.Flag]));
        Assert.Empty(Ls(w.Dir));
    }

    [Fact(DisplayName = "status prints the time in the PC's zone (pinned by POWER_MODE_TZ) whatever offset the file carries")]
    public async Task Status_prints_the_time_in_the_pinned_zone()
    {
        await using Cleanup t = new();
        World w = NewWorld(t);
        foreach ((string since, string want) in new[]
        {
            ("2026-10-02T22:20:00Z", "03.10.2026 01:20"),
            ("2026-10-02T18:20:00-04:00", "03.10.2026 01:20"),
            ("2026-12-01T10:00:00+00:00", "01.12.2026 12:00"),
            ("2026-12-31T22:30Z", "01.01.2027 00:30"),
        })
        {
            WriteText(w.Flag, EmergencyJson(since));
            Eq(new Res(1, $"power mode: EMERGENCY since {want}", ""), await CliAsync(["status", "-Path", w.Flag]));
        }
    }
}

public sealed class PowerModeCorruptFlagTests
{
    [Theory(DisplayName = "status on a corrupt flag exits 2 and never says normal")]
    [InlineData("not JSON", "{not json")]
    [InlineData("an empty file", "")]
    [InlineData("an array", "[1]")]
    [InlineData("an unknown mode", """{"mode":"on","since":"2026-10-03T01:20:00+03:00"}""")]
    [InlineData("a mode in another case", """{"mode":"Emergency","since":"2026-10-03T01:20:00+03:00"}""")]
    [InlineData("no since", """{"mode":"normal"}""")]
    [InlineData("a since without an offset", """{"mode":"emergency","since":"2026-10-03T01:20:00"}""")]
    [InlineData("a since on a day that does not exist", """{"mode":"emergency","since":"2026-02-30T01:20:00+02:00"}""")]
    public async Task Status_on_a_corrupt_flag_exits_2(string label, string text)
    {
        Assert.NotNull(label);
        await using Cleanup t = new();
        World w = NewWorld(t);
        WriteText(w.Flag, text);
        Res r = await CliAsync(["status", "-Path", w.Flag]);
        Eq(2, r.Status, r.Out);
        Match("^power mode: COULD NOT READ ", r.Out);
        Ok(Has(r.Out, w.Flag), r.Out);
        NoMatch("normal", r.Out);
        Eq(text, ReadText(w.Flag));
    }
}

public sealed class PowerModeBasicCTests
{
    [Fact(DisplayName = "on and off replace a corrupt flag file and say so")]
    public async Task On_and_off_replace_a_corrupt_flag()
    {
        await using Cleanup t = new();
        World w = NewWorld(t);
        WriteText(w.Flag, "{not json");
        Res on = await CliAsync(["on", "-Path", w.Flag]);
        Eq(0, on.Status, on.Out);
        Match(@"^power mode: EMERGENCY since .* \(replaced an unreadable flag file\)\z", on.Out);
        Eq(1, (await CliAsync(["status", "-Path", w.Flag])).Status);
        WriteText(w.Flag, "{not json");
        Eq(new Res(0, "power mode: normal (replaced an unreadable flag file)", ""), await CliAsync(["off", "-Path", w.Flag]));
        Eq(0, (await CliAsync(["status", "-Path", w.Flag])).Status);
    }

    [Fact(DisplayName = "an unknown action, a wrong-case action and no action exit 2 with usage and write nothing")]
    public async Task An_unknown_action_exits_2_with_usage()
    {
        await using Cleanup t = new();
        World w = NewWorld(t);
        string[][] cases = [["toggle", "-Path", w.Flag], ["ON", "-Path", w.Flag], ["-Path", w.Flag]];
        foreach (string[] args in cases)
        {
            Res r = await CliAsync(args);
            Eq(2, r.Status, r.Out);
            Match(@"^usage: power-mode\.ps1 on\|off\|status", r.Out);
        }
        Assert.Empty(Ls(w.Dir));
    }

    [Fact(DisplayName = "on into a directory that does not exist exits 2 and creates nothing")]
    public async Task On_into_a_missing_directory_exits_2()
    {
        await using Cleanup t = new();
        World w = NewWorld(t);
        Res r = await CliAsync(["on", "-Path", Path.Combine(w.Dir, "missing", "power-mode.json")]);
        Eq(2, r.Status, r.Out);
        Match("^power mode: COULD NOT RUN: ", r.Out);
        Assert.Empty(Ls(w.Dir));
    }

    [Fact(DisplayName = "on over a path that is a directory exits 2 and leaves no temp file")]
    public async Task On_over_a_directory_exits_2()
    {
        await using Cleanup t = new();
        World w = NewWorld(t);
        Directory.CreateDirectory(w.Flag);
        Res r = await CliAsync(["on", "-Path", w.Flag]);
        Eq(2, r.Status, r.Out);
        EqL(["power-mode.json"], Ls(w.Dir));
        Assert.Empty(Ls(w.Flag));
    }
}

public sealed class PowerModeBasicDTests
{
    [Fact(DisplayName = "POWER_MODE_FILE names the configured flag (its folder created with its own ACL); -Path wins over it")]
    public async Task POWER_MODE_FILE_names_the_configured_flag()
    {
        await using Cleanup t = new();
        World w = NewWorld(t);
        t.After(() => Icacls(w.Dir, "/reset", "/T", "/Q"));
        string configured = Path.Combine(w.Dir, "pd", "power-mode.json");
        string other = Path.Combine(w.Dir, "other.json");
        Eq(0, (await CliAsync(["on"], configured)).Status);
        EqL(["pd"], Ls(w.Dir));
        EqL(["power-mode.json"], Ls(Path.GetDirectoryName(configured)!));
        Eq(1, (await CliAsync(["status"], configured)).Status);
        Eq(0, (await CliAsync(["status", "-Path", other], configured)).Status);
        Eq(0, (await CliAsync(["on", "-Path", other], Path.Combine(w.Dir, "never", "never.json"))).Status);
        EqL(["other.json", "pd"], Ls(w.Dir));
    }

    [Fact(DisplayName = "with no POWER_MODE_TZ the time is the PC's own zone; POWER_MODE_TZ names another")]
    public async Task With_no_POWER_MODE_TZ_the_time_is_the_PCs_own_zone()
    {
        await using Cleanup t = new();
        World w = NewWorld(t);
        WriteText(w.Flag, EmergencyJson(OldSince));
        DateTimeOffset d = TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(OldSince, CultureInfo.InvariantCulture), TimeZoneInfo.Local);
        string local = d.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        Eq(new Res(1, $"power mode: EMERGENCY since {local}", ""), await CliAsync(["status", "-Path", w.Flag], null, Env(("POWER_MODE_TZ", ""))));
        Eq(new Res(1, "power mode: EMERGENCY since 02.10.2026 18:20", ""), await CliAsync(["status", "-Path", w.Flag], null, Env(("POWER_MODE_TZ", "America/New_York"))));
    }

    [Fact(DisplayName = "a written flag whose local time cannot be formatted: the raw since and a warn line, exit 0; status exits by mode")]
    public async Task A_written_flag_whose_local_time_cannot_be_formatted()
    {
        await using Cleanup t = new();
        World w = NewWorld(t);
        Dictionary<string, string> tz = Env(("POWER_MODE_TZ", "No/Such_Zone"));
        Res on = await CliAsync(["on", "-Path", w.Flag], null, tz);
        Eq(0, on.Status, on.Out);
        string since = FlagRe.Match(ReadText(w.Flag)).Groups[2].Value;
        string[] lines = Lines(on.Out);
        Eq($"power mode: EMERGENCY since {since}", lines[0]);
        Match(@"^warn: local time unavailable \(", lines[1]);
        Eq(2, lines.Length);
        Res again = await CliAsync(["on", "-Path", w.Flag], null, tz);
        Eq(0, again.Status, again.Out);
        Eq($"power mode: EMERGENCY since {since} (already on, since kept)", Lines(again.Out)[0]);
        Res st = await CliAsync(["status", "-Path", w.Flag], null, tz);
        Eq(1, st.Status, st.Out);
        Eq($"power mode: EMERGENCY since {since}", Lines(st.Out)[0]);
        Eq(new Res(0, "power mode: normal", ""), await CliAsync(["off", "-Path", w.Flag], null, tz));
    }
}
