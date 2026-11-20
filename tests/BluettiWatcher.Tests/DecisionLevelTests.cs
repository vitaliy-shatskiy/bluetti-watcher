using System.Text.Json;
using BluettiWatcher.Decision;
using BluettiWatcher.Io;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

/// <summary>design.md §6 Battery levels and hibernate: what the level says, and the levels raised by the measured drain.</summary>
public class DecisionLevelTests
{
    /// <summary>The owner's thresholds (20 / 17 / 16 / 15 %), one reading a minute.</summary>
    private static StationConfig Owner() => new()
    {
        PollSec = 60, FastPollSec = 60, MainsLostSec = 300, MainsBackSec = 300,
        EmergencyAt = 20, StopAt = 17, WarnAt = 16, HibernateAt = 15,
    };

    /// <summary>Six readings at 30 % (the cut begins at the sixth), then 1.5 points a minute down, floored at 1 % (within the plausible rate: no rebase).</summary>
    private static readonly double[] Fall = [.. Enumerable.Repeat(30.0, 6), .. Enumerable.Range(0, 24).Select(k => Math.Max(1, 30 - 1.5 * k))];

    private static object[] Minutely(IEnumerable<double> socs) => socs.Select(x => (object)new S(Bat(x), Dt: 60)).ToArray();

    /// <summary>The level as /api/now names it.</summary>
    private static bool Hibernating(Level l) => string.Equals(l.ToString(), "hibernating", StringComparison.OrdinalIgnoreCase);

    private static int IndexOf(List<StationDecisionResult> d, string action) => d.FindIndex(x => Flat([x]).Contains(action));

    // at 16 % with hibernateAt 15 the warning is given, the PC does not hibernate: the level must not say it does.
    [Fact]
    public void Level_at_the_warning_above_hibernateAt_never_reads_hibernating()
    {
        var d = Step(Seq(Pre(16), Bat(16), Bat(16), Bat(16), Bat(16), Bat(16), Bat(16)), Station, cfg: Owner());
        Assert.Contains("hibernate-warning", Flat(d));
        Assert.DoesNotContain("hibernate", Flat(d));
        Assert.All(d.Where(x => x.Battery is not null), x => Assert.False(Hibernating(x.Battery!.Level)));
        Assert.All(d, x => Assert.False(Hibernating(x.State.Level)));
    }

    // The flag keeps its word: the hook says "the PC hibernates at N %: save now" from the warning on.
    [Fact]
    public void BatteryArgs_warning_level_writes_hibernating_to_the_flag()
    {
        var args = PowerModeAdapter.BatteryArgs(new BatteryReport(16, Level.Warning, false), 15);
        Assert.Equal("hibernating", args[args.ToList().IndexOf("-Level") + 1]);
    }

    // A state saved by an earlier version during the warning ("Hibernating") still loads, with its cut and warning intact.
    [Fact]
    public void State_saved_with_the_old_level_name_loads_as_warning()
    {
        var s = JsonSerializer.Deserialize("""{"cut":true,"level":"Hibernating"}""", StationJson.Default.StationState);
        Assert.True(s!.Cut);
        Assert.Equal(Level.Warning, s.Level);
        Assert.Contains("\"Hibernating\"", JsonSerializer.Serialize(s, StationJson.Default.StationState), StringComparison.Ordinal);
    }

    // at 90 %/h the soc crosses warn and hibernate within a few polls; the raised levels give the warning early
    // enough that the hibernate comes at the first poll its own agreement allows (2 readings a minute apart at or under 15 %).
    [Fact]
    public void High_drain_warning_comes_early_and_hibernate_fires_at_its_first_agreed_poll()
    {
        int firstAt15 = Array.FindIndex(Fall, x => x <= 15);
        var raised = Step(Minutely(Fall), Station, cfg: Owner(), drain: _ => 90);
        int warn = IndexOf(raised, "hibernate-warning"), hib = IndexOf(raised, "hibernate");
        Assert.Equal(firstAt15 + 2, hib);
        Assert.True(warn < hib - 1, $"warning at {warn}, hibernate at {hib}");

        var plain = Step(Minutely(Fall), Station, cfg: Owner());
        Assert.True(IndexOf(plain, "hibernate") > hib, "without the drain the hibernate comes later (the defect)");
    }

    // a long poll at the highest drain counted (300 s, 120 %/h) would ask for warn 31 / stop 51 / emergency 71 %: each
    // raise stops at its configured level + 10 points, the order kept, hibernateAt never moved.
    [Fact]
    public void Effective_long_poll_at_the_highest_drain_raises_each_level_at_most_ten_points()
    {
        var c = new StationConfig { PollSec = 300 };
        var th = StationDecision.Effective(c, 120);
        Assert.True(th.WarnAt <= c.WarnAt + 10, $"warn {th.WarnAt}");
        Assert.True(th.StopAt <= c.StopAt + 10, $"stop {th.StopAt}");
        Assert.True(th.EmergencyAt <= c.EmergencyAt + 10, $"emergency {th.EmergencyAt}");
        Assert.True(th.WarnAt > c.WarnAt, "the drain still raises the warning");
        Assert.True(th.HibernateAt < th.WarnAt && th.WarnAt <= th.StopAt && th.StopAt <= th.EmergencyAt, $"{th}");
        Assert.Equal(c.HibernateAt, th.HibernateAt);
    }

    // a low drain leaves every level where the config puts it.
    [Fact]
    public void Low_drain_keeps_the_configured_levels()
    {
        var c = Owner();
        var th = StationDecision.Effective(c, 3);
        Assert.Equal((20.0, 17.0, 16.0, 15.0), (th.EmergencyAt, th.StopAt, th.WarnAt, th.HibernateAt));
        Assert.Equal(AllActs(Step(Minutely(Fall), Station, cfg: c)), AllActs(Step(Minutely(Fall), Station, cfg: c, drain: _ => 3)));
    }

    // Fail-open: no estimate, a stale one (the service passes null), zero, negative, NaN, infinite = the configured levels.
    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-40.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Missing_or_unsound_drain_keeps_the_configured_levels(double? drain)
    {
        var th = StationDecision.Effective(Owner(), drain);
        Assert.Equal((20.0, 17.0, 16.0, 15.0, (double?)null), (th.EmergencyAt, th.StopAt, th.WarnAt, th.HibernateAt, th.DrainPerHour));
    }

    // one wild sample is capped; it never moves hibernateAt and never warns or hibernates at 80 %.
    [Fact]
    public void Huge_drain_is_capped_and_never_moves_hibernateAt()
    {
        var c = Owner();
        var th = StationDecision.Effective(c, 1e9);
        Assert.Equal(StationDecision.MaxDrainPerHour, th.DrainPerHour);
        Assert.Equal(15, th.HibernateAt);
        Assert.True(th.EmergencyAt < 40, $"emergency at {th.EmergencyAt}");
        var d = Step(Minutely(Enumerable.Repeat(80.0, 15)), Station, cfg: c, drain: _ => 1e9);
        Assert.DoesNotContain(Flat(d), a => a is "stop" or "hibernate-warning" or "hibernate");
    }

    // the levels only rise and keep their order (emergency >= stop >= warn >= hibernate).
    [Theory]
    [InlineData(3.0)]
    [InlineData(30.0)]
    [InlineData(90.0)]
    [InlineData(500.0)]
    public void Raised_levels_never_go_under_the_configured_ones_and_keep_their_order(double drain)
    {
        var c = Owner();
        var th = StationDecision.Effective(c, drain);
        Assert.True(th.EmergencyAt >= c.EmergencyAt && th.StopAt >= c.StopAt && th.WarnAt >= c.WarnAt && th.HibernateAt == c.HibernateAt);
        Assert.True(th.EmergencyAt >= th.StopAt && th.StopAt >= th.WarnAt && th.WarnAt >= th.HibernateAt);
        Assert.True(th.EmergencyAt <= 100);
    }

    // Default-deny: whatever the drain does from poll to poll (flapping, spikes, gaps), the hibernate still comes,
    // never later than with the configured levels, and never above hibernateAt.
    [Theory]
    [InlineData("none")]
    [InlineData("flap")]
    [InlineData("spike")]
    [InlineData("nan")]
    public void Changing_drain_never_delays_or_suppresses_the_configured_hibernate(string kind)
    {
        Func<int, double?> drain = kind switch
        {
            "flap" => i => i % 2 == 0 ? 90 : null,
            "spike" => i => i % 5 == 0 ? 1e6 : 2,
            "nan" => i => i % 3 == 0 ? double.NaN : 60,
            _ => _ => null,
        };
        var plain = Step(Minutely(Fall), Station, cfg: Owner());
        var d = Step(Minutely(Fall), Station, cfg: Owner(), drain: drain);
        int hib = IndexOf(d, "hibernate");
        Assert.InRange(hib, 0, IndexOf(plain, "hibernate"));
        Assert.All(d.Where(x => x.Actions.Contains(StationAction.Hibernate)), x => Assert.True(x.State.LastSoc <= 15));
    }
}
