using BluettiWatcher.Decision;
using BluettiWatcher.Io;
using BluettiWatcher.Tray;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

// The tray icon's pixels, its tooltip and the snapshot it is drawn from (README, Tray).
public sealed class TrayViewTests
{
    private static TraySnapshot Snap(double? soc, TrayPower power, bool stale = false) =>
        new(soc, power, 0, 230, "eco · Auto", 2.5, T0, stale, power == TrayPower.Battery);

    private static uint At(uint[] px, int size, int x, int y) => px[y * size + x];

    /// <summary>A row inside the bar band (its top row: the bar is the bottom fifth, one tile row under it).</summary>
    private static int BarRow(int size) => size - 1 - Math.Max(2, size / 5);

    private static int Filled(uint[] px, int size, uint color) =>
        Enumerable.Range(0, size).Count(x => At(px, size, x, BarRow(size)) == color);

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void The_bar_fills_with_the_charge_in_the_mains_colour(int size)
    {
        var px = TrayIconRenderer.Render(Snap(50, TrayPower.Mains), size);
        Assert.Equal(size * size, px.Length);
        Assert.Equal((size - 2 + 1) / 2, Filled(px, size, TrayIconRenderer.MainsFill));
        Assert.Equal(0, Filled(px, size, TrayIconRenderer.BatteryFill));
        Assert.Equal(TrayIconRenderer.Clear, At(px, size, 0, 0));   // rounded corner
    }

    [Fact]
    public void On_battery_the_bar_is_amber_and_full_at_100()
    {
        var px = TrayIconRenderer.Render(Snap(100, TrayPower.Battery), 16);
        Assert.Equal(14, Filled(px, 16, TrayIconRenderer.BatteryFill));
        Assert.Equal(0, Filled(px, 16, TrayIconRenderer.MainsFill));
    }

    [Fact]
    public void A_stale_reading_draws_grey_and_dim_digits()
    {
        var px = TrayIconRenderer.Render(Snap(80, TrayPower.Unknown, stale: true), 16);
        Assert.Equal(11, Filled(px, 16, TrayIconRenderer.UnknownFill));   // round(0.8 * 14)
        Assert.Contains(TrayIconRenderer.StaleDigit, px);
        Assert.DoesNotContain(TrayIconRenderer.Digit, px);
    }

    [Fact]
    public void The_digits_are_drawn_and_differ_by_value()
    {
        var a = TrayIconRenderer.Render(Snap(45, TrayPower.Battery), 24);
        var b = TrayIconRenderer.Render(Snap(46, TrayPower.Battery), 24);
        Assert.Contains(TrayIconRenderer.Digit, a);
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData(null, "?")]
    [InlineData(45.4, "45")]
    [InlineData(45.5, "46")]
    [InlineData(-3.0, "0")]
    [InlineData(104.0, "100")]
    [InlineData(double.NaN, "?")]
    public void The_text_is_the_rounded_charge_within_0_to_100(double? soc, string text) =>
        Assert.Equal(text, TrayIconRenderer.Text(soc));

    [Fact]
    public void The_key_changes_only_with_what_the_picture_shows()
    {
        var a = Snap(45.2, TrayPower.Battery);
        Assert.Equal(TrayIconRenderer.Key(a, 16), TrayIconRenderer.Key(a with { Soc = 44.6, OutWatts = 900, HoursLeft = 1 }, 16));
        Assert.NotEqual(TrayIconRenderer.Key(a, 16), TrayIconRenderer.Key(a with { Power = TrayPower.Mains }, 16));
        Assert.NotEqual(TrayIconRenderer.Key(a, 16), TrayIconRenderer.Key(a, 24));
        Assert.NotEqual(TrayIconRenderer.Key(a, 16), TrayIconRenderer.Key(a with { Soc = 46 }, 16));
    }

    [Fact]
    public void The_tooltip_names_charge_watts_mode_and_time_left()
    {
        string tip = TrayText.Tooltip(Snap(45, TrayPower.Battery), TimeZoneInfo.Utc);
        Assert.Equal("Battery 45 % · on battery\nIn 0 W · out 230 W\nMode: eco · Auto\nTime left 2 h 30 min", tip);
        Assert.True(tip.Length <= TrayText.TooltipMax);
    }

    [Fact]
    public void A_stale_tooltip_says_since_when_and_shows_no_watts()
    {
        string tip = TrayText.Tooltip(Snap(45, TrayPower.Unknown, stale: true) with { HoursLeft = null }, TimeZoneInfo.Utc);
        Assert.StartsWith("Battery 45 % · no reading since ", tip, StringComparison.Ordinal);
        Assert.DoesNotContain(" W", tip, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_tooltip_is_cut_to_the_shell_limit() =>
        Assert.Equal(TrayText.TooltipMax, TrayText.Tooltip(Snap(45, TrayPower.Battery) with { Mode = new string('m', 200) }, TimeZoneInfo.Utc).Length);

    [Theory]
    [InlineData(0.75, "45 min")]
    [InlineData(3.34, "3 h 20 min")]
    [InlineData(0.999, "1 h 0 min")]
    public void Durations_read_as_hours_and_minutes(double hours, string text) => Assert.Equal(text, TrayText.Duration(hours));

    [Fact]
    public void A_fresh_reading_on_battery_carries_the_time_left_and_the_cut()
    {
        var state = new StationState { Cut = true, Manual = ManualMode.Eco };
        var s = TraySnapshot.From(Bat(40), T0, state, PowerModeFlag.Normal with { Mode = "eco" }, 3.5, T0.AddSeconds(60), 600);
        Assert.Equal(TrayPower.Battery, s.Power);
        Assert.False(s.Stale);
        Assert.True(s.Cut);
        Assert.Equal(3.5, s.HoursLeft);
        Assert.Equal("eco · Eco", s.Mode);
        Assert.Equal(220, s.OutWatts);
    }

    [Fact]
    public void A_reading_older_than_the_limit_is_stale_and_has_no_time_left()
    {
        var s = TraySnapshot.From(Bat(40), T0, new StationState(), null, 3.5, T0.AddSeconds(601), 600);
        Assert.True(s.Stale);
        Assert.Equal(TrayPower.Unknown, s.Power);
        Assert.Null(s.HoursLeft);
        Assert.Equal("unknown · Auto", s.Mode);
    }

    [Fact]
    public void On_mains_the_icon_is_green_and_a_gap_kept_cut_still_counts_as_cut()
    {
        var s = TraySnapshot.From(Grid(90), T0, new StationState { CutBeforeGap = true }, null, 3.5, T0, 600);
        Assert.Equal(TrayPower.Mains, s.Power);
        Assert.Null(s.HoursLeft);
        Assert.True(s.Cut);
    }
}
