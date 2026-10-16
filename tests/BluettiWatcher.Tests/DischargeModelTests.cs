using BluettiWatcher.Runtime;
using BluettiWatcher.Storage;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

/// <summary>design.md §7 Battery time: the loss model fitted from the station's own discharges, on synthetic history only.</summary>
public sealed class DischargeModelTests
{
    private static readonly long Now = T0.ToUnixTimeSeconds();
    private static readonly StationConfig C = new();

    private static HistoryRow Row(long at, double? soc, double load = 200, bool mains = false, double inDc = 0) =>
        new(at, soc, mains ? 350 : 0, inDc, load, 0, 230, 50, mains ? 1 : 0, null);

    /// <summary>
    /// A station with idle draw <paramref name="p0"/> W and loss <paramref name="k"/>: <paramref name="cuts"/> cuts of
    /// <paramref name="hours"/> h from 100 %, one reading a minute, the load switching between 120 and 380 W every 60
    /// minutes, the SOC reported in whole points (rounded up, as a station shows 100 until it falls under), 30 minutes of
    /// mains between cuts.
    /// </summary>
    private static List<HistoryRow> Station(double p0, double k, int cuts = 4, int hours = 5) =>
        Station(Enumerable.Repeat((p0, k), cuts), hours);

    /// <summary>As <see cref="Station(double, double, int, int)"/>, each cut with its own idle draw and loss.</summary>
    private static List<HistoryRow> Station(IEnumerable<(double P0, double K)> perCut, int hours)
    {
        var rows = new List<HistoryRow>();
        long at = Now;
        foreach (var (p0, k) in perCut)
        {
            double soc = 100;
            for (int m = 0; m < hours * 60; m++, at += 60)
            {
                double load = m / 60 % 2 == 0 ? 120 : 380;
                rows.Add(Row(at, Math.Ceiling(soc - 1e-9), load));
                soc -= (p0 + (1 + k) * load) / C.CapacityWh * 100 / 60;
            }
            for (int m = 0; m < 30; m++, at += 60) rows.Add(Row(at, 100, 200, mains: true));
        }
        return rows;
    }

    private static LossFit FitAll(List<HistoryRow> rows, double priorHours = DischargeModel.PriorHours)
    {
        var stretches = new List<Stretch>();
        foreach (var cut in DischargeModel.Cuts(rows, C)) DischargeModel.Stretches(rows, cut, C, stretches);
        return DischargeModel.Fit(stretches, C, priorHours);
    }

    [Fact]
    public void Fit_recovers_the_idle_draw_and_the_loss_from_whole_percent_steps()
    {
        var bare = FitAll(Station(40, 0.2), priorHours: 0);
        Assert.InRange(bare.IdleW(C), 36, 44);
        Assert.InRange(bare.K(C), 0.18, 0.22);
        Assert.InRange(bare.CleanHours, 15, 20);
        // with the prior (no idle draw, the configured 0.85 = k 0.176) the fit sits between the two, nearer the data
        var fit = FitAll(Station(40, 0.2));
        Assert.InRange(fit.IdleW(C), 20, bare.IdleW(C));
        Assert.InRange(fit.K(C), 0.176, bare.K(C));
    }

    [Fact]
    public void The_prior_weighs_as_many_hours_of_discharge_as_it_is_given()
    {
        // 2 h of data in quarter-hour stretches at 100 and 300 W on the line 3 + 0.06 x P against a 2 h prior:
        // at each load the fit lands halfway between the data and the prior
        Stretch S(double load) => new((3 + 0.06 * load) * 0.25, 0.25, load * 0.25);
        var data = Enumerable.Range(0, 8).Select(i => S(i % 2 == 0 ? 100 : 300)).ToList();
        var prior = DischargeModel.Prior(C);
        var fit = DischargeModel.Fit(data, C, priorHours: 2);
        Assert.Equal((9 + prior.Drain(100)) / 2, fit.Drain(100), 6);
        Assert.Equal((21 + prior.Drain(300)) / 2, fit.Drain(300), 6);
    }

    [Fact]
    public void Fit_without_clean_data_is_the_prior()
    {
        var prior = DischargeModel.Prior(C);
        Assert.Equal(prior, DischargeModel.Fit([], C));
        Assert.Equal((0.0, 0.0), (prior.Idle, prior.CleanHours));
        Assert.Equal(100 / (2304 * 0.85), prior.PerWh, 12);
        // readings with solar input teach nothing
        var solar = Station(40, 0.2, cuts: 1).Select(r => r with { InDc = 50 }).ToList();
        Assert.Equal(prior, FitAll(solar));
    }

    [Fact]
    public void A_fit_outside_the_physical_bounds_is_rejected_for_the_prior()
    {
        // k = 1.5 (a drain two and a half times the output): never used
        Assert.Equal(DischargeModel.Prior(C), FitAll(Station(0, 1.5, cuts: 2, hours: 2), priorHours: 0));
        // k below 0 (more out than the battery gave) likewise
        Assert.Equal(DischargeModel.Prior(C), FitAll(Station(0, -0.3), priorHours: 0));
    }

    [Fact]
    public void A_negative_idle_draw_refits_with_none()
    {
        // drop = 1.2/23.04 per Wh minus 0.5 points an hour: the unbounded fit gives Idle < 0
        double perWh = 1.2 * 100 / C.CapacityWh;
        Stretch S(double h, double w) => new(perWh * w * h - 0.5 * h, h, w * h);
        var fit = DischargeModel.Fit([S(1, 100), S(1, 400), S(2, 250)], C, priorHours: 0);
        Assert.Equal(0, fit.Idle);
        Assert.True(fit.PerWh > 0 && DischargeModel.Physical(fit, C));
    }

    [Fact]
    public void One_load_only_never_divides_by_zero()
    {
        Stretch[] same = [new(5, 1, 250), new(10, 2, 500)];
        var bare = DischargeModel.Fit(same, C, priorHours: 0);   // singular: Idle and PerWh cannot be told apart
        Assert.True(double.IsFinite(bare.Idle) && double.IsFinite(bare.PerWh));
        var shrunk = DischargeModel.Fit(same, C);   // the prior's two loads make it solvable
        Assert.True(DischargeModel.Physical(bare, C) && DischargeModel.Physical(shrunk, C));
    }

    [Fact]
    public void Cuts_split_at_mains_a_long_silence_a_charge_in_a_silence_and_a_clock_step_back()
    {
        long t = Now;
        List<HistoryRow> rows =
        [
            Row(t, 90), Row(t + 60, null), Row(t + 120, 89),          // a row without SOC is skipped, not a split
            Row(t + 180, 95, mains: true),                             // mains
            Row(t + 240, 94), Row(t + 300, 93),
            Row(t + 300 + DischargeModel.SplitGapSec + 1, 80),         // a silence over 3 h
            Row(t + 300 + DischargeModel.SplitGapSec + 61, 79),
            Row(t + 300 + DischargeModel.SplitGapSec + 61 + C.MaxGapSec + 1, 85),   // risen across a gap: charged unseen
            Row(t + 300 + DischargeModel.SplitGapSec + 30, 84),        // the clock stepped back
        ];
        Assert.Equal([new Cut(0, 2), new Cut(4, 5), new Cut(6, 7), new Cut(8, 8), new Cut(9, 9)], DischargeModel.Cuts(rows, C));
    }

    [Fact]
    public void Stretches_run_from_step_to_step_and_drop_on_suspect_readings()
    {
        // 1 point per 10 minutes from 100, steps at 10, 20 ... 40; RateMinDrop 3 and 20 min: one stretch 10->40
        var rows = Enumerable.Range(0, 46).Select(m => Row(Now + m * 60L, 100 - m / 10)).ToList();
        List<(double, double, double)> Run(List<HistoryRow> rs)
        {
            var into = new List<Stretch>();
            DischargeModel.Stretches(rs, new Cut(0, 45), C, into);
            return [.. into.Select(s => (s.Drop, s.Hours, Math.Round(s.OutWh, 6)))];
        }
        Assert.Equal([(3.0, 0.5, 100.0)], Run(rows));   // 30 minutes at 200 W
        // a rise, a 4-point jump, DC input and a gap over maxGapSec at minute 35 each drop it
        foreach (Func<int, HistoryRow, HistoryRow> spoil in (Func<int, HistoryRow, HistoryRow>[])
            [
                (m, r) => m == 35 ? r with { Soc = r.Soc + 2 } : r,
                (m, r) => m >= 35 ? r with { Soc = r.Soc - 4 } : r,
                (m, r) => m == 35 ? r with { InDc = 30 } : r,
                (m, r) => m >= 35 ? r with { At = r.At + C.MaxGapSec } : r,
            ])
            Assert.Empty(Run([.. rows.Select((r, m) => spoil(m, r))]));
    }
    [Fact]
    public void Replay_scores_only_targets_inside_the_cut_and_marks_the_first_30_minutes()
    {
        // 61 readings, 0..60 min: only the reading at 0 has a 60-min target; none has a 120-min one
        var rows = Enumerable.Range(0, 61).Select(m => Row(Now + m * 60L, 100 - m / 6.0)).ToList();
        var got = new List<Residual>();
        DischargeModel.Replay(rows, [new Cut(0, 60)], (_, _, _) => 10, got.Add);
        Assert.Single(got, r => r.Horizon == 60);
        Assert.DoesNotContain(got, r => r.Horizon == 120);
        Assert.Equal(31, got.Count(r => r.Horizon == 30));   // 0..30
        Assert.Equal(30, got.Count(r => r.Horizon == 30 && r.Early));   // 0..29 early, 30 late
        Assert.All(got, r => Assert.Equal(0, r.Error, 9));   // the forecast 10 %/h is exact
    }

    [Fact]
    public void A_stored_model_round_trips_and_a_damaged_or_unphysical_one_is_none()
    {
        var m = new FittedRuntime(new LossFit(1.5, 0.06, 12.5), -0.5, 1.25, Now);
        Assert.Equal(m, FittedRuntime.Parse(m.Format(), C));
        Assert.Null(FittedRuntime.Parse(null, C));
        Assert.Null(FittedRuntime.Parse("2;0.06;3;;;0", C));    // no fit time
        Assert.Null(FittedRuntime.Parse("2;0.06;3;;;-5", C));
        Assert.Null(FittedRuntime.Parse("x;1;2;;;3", C));
        Assert.Null(FittedRuntime.Parse(new FittedRuntime(new LossFit(-1, 0.06, 1), null, null, Now).Format(), C));
        Assert.Null(FittedRuntime.Parse(new FittedRuntime(new LossFit(1, 0.2, 1), null, null, Now).Format(), C));   // k > 1
        Assert.Equal((null, null), FittedRuntime.Parse("2;0.06;1;3;1;5", C) is { } p ? (p.ErrLo, p.ErrHi) : default);   // lo > hi
    }

    [Fact]
    public void Learn_fits_and_reads_an_interval_from_held_out_cuts()
    {
        var m = DischargeModel.Learn(Station(40, 0.2, cuts: 5), C, Now);
        Assert.Equal(FitAll(Station(40, 0.2, cuts: 5)), m.Fit);
        Assert.NotNull(m.ErrLo);
        Assert.True(m.ErrLo <= m.ErrHi);
        Assert.Equal(Now, m.FittedAt);
        // four cuts: thousands of residuals, but too few independent cuts for a range
        Assert.Null(DischargeModel.Learn(Station(40, 0.2, cuts: 4), C, Now).ErrLo);
        // no cut at all: the prior, no interval
        var none = DischargeModel.Learn([Row(Now, 100, mains: true)], C, Now);
        Assert.Equal((DischargeModel.Prior(C), (double?)null), (none.Fit, none.ErrLo));
    }

    [Fact]
    public void The_range_leans_to_a_faster_drain_when_real_drains_sometimes_run_faster()
    {
        // six cuts at the same loads: four drain as modelled, two much faster (an unseen 250 W), so the real drain minus
        // the forecast has a long fast tail: the low end of the time left must reach well under the estimate
        (double, double) normal = (20, 0.2), fast = (270, 0.2);
        var m = DischargeModel.Learn(Station([normal, normal, fast, normal, normal, fast], hours: 4), C, Now);
        Assert.True(m.ErrHi > -m.ErrLo, $"the fast side must be the wide one: [{m.ErrLo}, {m.ErrHi}]");
        var e = RuntimeEstimator.Estimate(Bat(70), [], Now, C, null, m);
        Assert.Equal("model", e.Source);
        Assert.True(e.HoursLeftLow <= 0.7 * e.HoursLeft, $"low {e.HoursLeftLow} against {e.HoursLeft}");
    }

    [Fact]
    public void The_model_gives_the_time_at_the_current_load_with_a_range_around_it()
    {
        var model = new FittedRuntime(new LossFit(2, 0.05, 10), -1, 2, Now);
        var e = RuntimeEstimator.Estimate(Bat(70), [], Now, C, null, model);   // 220 W: 2 + 11 = 13 %/h
        Assert.Equal(("model", 5.4, 5.0), (e.Source, e.HoursLeft, e.HoursToHibernate));   // 70 / 13, 65 / 13 (hibernateAt 5)
        Assert.Equal((4.7, 5.8), (e.HoursLeftLow, e.HoursLeftHigh));   // 70 / 15, 70 / 12
        // a drain that may reach 0: no top
        Assert.Null(RuntimeEstimator.Estimate(Bat(70), [], Now, C, null, model with { ErrLo = -20 }).HoursLeftHigh);
        // no clean data behind it, or off the bounds: the old sources
        Assert.Equal("config", RuntimeEstimator.Estimate(Bat(70), [], Now, C, null, model with { Fit = new LossFit(2, 0.05, 0) }).Source);
        Assert.Equal("config", RuntimeEstimator.Estimate(Bat(70), [], Now, C, null, model with { Fit = new LossFit(-2, 0.05, 10) }).Source);
        Assert.Null(RuntimeEstimator.Estimate(Bat(70), [], Now, C, null, model with { ErrLo = null, ErrHi = null }).HoursLeftLow);
    }
}
