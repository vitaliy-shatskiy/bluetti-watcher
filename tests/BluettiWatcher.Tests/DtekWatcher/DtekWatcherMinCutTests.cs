using BluettiWatcher.DtekWatcher;
using static BluettiWatcher.Tests.DtekWatcher.Dx;

namespace BluettiWatcher.Tests.DtekWatcher;

/// <summary>A cut shorter than <c>dtekWatcher.minCutMin</c> counts for no statistic (design.md §11 rule 29).</summary>
public sealed class DtekWatcherMinCutTests
{
    private static readonly TimeSpan Fifteen = TimeSpan.FromMinutes(15);

    // design.md §11 DTEK module, rule 29
    [Fact]
    public void A_cut_of_4_minutes_inside_a_window_adds_no_lateness_entry_but_one_of_15_does()
    {
        Assert.Null(DtekOutlook.Lateness(Cut("2026-10-07 17:06", "2026-10-07 17:10"), TwoDays(), null, ["A"], Kyiv, Fifteen));
        var late = DtekOutlook.Lateness(Cut("2026-10-07 17:06", "2026-10-07 17:21"), TwoDays(), null, ["A"], Kyiv, Fifteen);
        Assert.NotNull(late);
        Assert.Equal("A", late!.Queue);
    }

    // design.md §11 DTEK module, rule 29: the boundary is the minimum itself, which counts
    [Fact]
    public void Exactly_the_minimum_counts_and_a_second_less_does_not()
    {
        var start = At("2026-10-07 17:06");
        Assert.NotNull(DtekOutlook.Lateness(new DtekCut(start, start + Fifteen), TwoDays(), null, ["A"], Kyiv, Fifteen));
        Assert.Null(DtekOutlook.Lateness(new DtekCut(start, start + Fifteen - TimeSpan.FromSeconds(1)), TwoDays(), null, ["A"], Kyiv, Fifteen));
    }

    // design.md §11 DTEK module, rule 29: 0 = every cut counts
    [Fact]
    public void A_minimum_of_zero_counts_every_cut()
    {
        Assert.NotNull(DtekOutlook.Lateness(Cut("2026-10-07 17:06", "2026-10-07 17:10"), TwoDays(), null, ["A"], Kyiv, TimeSpan.Zero));
        Assert.NotNull(DtekOutlook.Lateness(Cut("2026-10-07 17:06", "2026-10-07 17:10"), TwoDays(), null, ["A"], Kyiv, TimeSpan.Zero));
    }

    // design.md §11 DTEK module, rule 29: a blip near a 30-minute window is no evidence for the queue
    [Fact]
    public void A_short_cut_near_a_window_does_not_move_the_queue_search_or_the_manual_queue_hint()
    {
        var short4 = Cut("2026-10-07 09:10", "2026-10-07 09:14");   // C 09:00-09:30 only
        var m = DtekMatcher.Apply(new DtekMatch(), short4, TwoDays(), House, Kyiv, Fifteen);
        Assert.Equal((null, null, 0), (m.Fixed, m.Candidate, m.Count));
        Assert.Equal(("C", 1), (DtekMatcher.Apply(new DtekMatch(), short4, TwoDays(), House, Kyiv, TimeSpan.Zero).Candidate, DtekMatcher.Apply(new DtekMatch(), short4, TwoDays(), House, Kyiv, TimeSpan.Zero).Count));   // without a minimum it counted

        var h = DtekMatcher.Hint(null, "A", short4, TwoDays(), House, Kyiv, Fifteen);
        Assert.Equal((null, 0), (h.Candidate, h.Count));
        Assert.Equal(("C", 1), (DtekMatcher.Hint(null, "A", short4, TwoDays(), House, Kyiv, TimeSpan.Zero).Candidate, DtekMatcher.Hint(null, "A", short4, TwoDays(), House, Kyiv, TimeSpan.Zero).Count));
    }

    // design.md §11 DTEK module, rule 29
    [Fact]
    public void The_setting_defaults_to_15_and_is_checked_from_0_to_1440()
    {
        Assert.Equal(15, new DtekConfig().MinCutMin);
        Assert.Equal(Fifteen, new DtekConfig().MinCut);
        foreach (int ok in new[] { 0, 1, 15, 1440 })
            Assert.Null(new DtekConfig { Enabled = true, MinCutMin = ok }.Problem());
        foreach (int bad in new[] { -1, 1441, int.MaxValue, int.MinValue })
            Assert.Contains("minCutMin", new DtekConfig { Enabled = true, MinCutMin = bad }.Problem(), StringComparison.Ordinal);
    }
}
