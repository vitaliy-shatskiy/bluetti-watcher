using System.Globalization;
using BluettiWatcher.Storage;

namespace BluettiWatcher.Runtime;

/// <summary>A run of readings off mains with a SOC: indexes into the rows, both ends included.</summary>
internal readonly record struct Cut(int Start, int End);

/// <summary>A clean discharge between two SOC steps: the points dropped, the hours between the steps, the Wh the load drew.</summary>
internal readonly record struct Stretch(double Drop, double Hours, double OutWh);

/// <summary>
/// The station's loss model in SOC points: drain(P) = <see cref="Idle"/> + <see cref="PerWh"/> x P points an hour, P the
/// output in W. In watts: battery power = P0 + (1 + k) x P, P0 = Idle x capacity / 100, 1 + k = PerWh x capacity / 100.
/// Capacity and k are not told apart by SOC drops (only their ratio is), so the model keeps the ratio and reads P0 and k
/// at the configured capacity. <see cref="CleanHours"/>: the clean discharge it was fitted on, 0 = the prior alone.
/// </summary>
internal readonly record struct LossFit(double Idle, double PerWh, double CleanHours)
{
    /// <summary>SOC points an hour at <paramref name="load"/> W.</summary>
    public double Drain(double load) => Idle + PerWh * Math.Max(0, load);

    /// <summary>P0, the idle draw in W at the configured capacity.</summary>
    public double IdleW(StationConfig c) => Idle * c.CapacityWh / 100;

    /// <summary>k, the proportional loss at the configured capacity.</summary>
    public double K(StationConfig c) => PerWh * c.CapacityWh / 100 - 1;
}

/// <summary>
/// The battery model as fitted from the station's history: the loss model, and the P10-P90 of its drain error (SOC points
/// an hour, real minus forecast) over held-out cuts at the longest horizon with enough of them; null bounds = too few.
/// Kept in the meta table as "idle;perWh;cleanHours;errLo;errHi;fittedAt" (invariant culture, empty = none).
/// </summary>
internal sealed record FittedRuntime(LossFit Fit, double? ErrLo, double? ErrHi, long FittedAt)
{
    public string Format()
    {
        static string N(double? v) => v is { } x ? x.ToString("R", CultureInfo.InvariantCulture) : "";
        return string.Join(';', N(Fit.Idle), N(Fit.PerWh), N(Fit.CleanHours), N(ErrLo), N(ErrHi), FittedAt.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>A stored model, or null when it is missing, damaged (no fit time included), or outside the physical bounds for this config.</summary>
    public static FittedRuntime? Parse(string? raw, StationConfig c)
    {
        var p = (raw ?? "").Split(';');
        if (p.Length != 6) return null;
        static double? D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : null;
        if (D(p[0]) is not { } idle || D(p[1]) is not { } perWh || D(p[2]) is not { } hours
            || !long.TryParse(p[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out long at) || at <= 0) return null;
        var fit = new LossFit(idle, perWh, hours);
        double? lo = D(p[3]), hi = D(p[4]);
        if (lo is null != hi is null || lo > hi) (lo, hi) = (null, null);
        return DischargeModel.Physical(fit, c) && hours >= 0 ? new(fit, lo, hi, at) : null;
    }
}

/// <summary>A forecast error: predicted minus real SOC <see cref="Horizon"/> minutes ahead, made <see cref="Early"/> = in a cut's first 30 minutes.</summary>
internal readonly record struct Residual(int CutIndex, int Horizon, bool Early, double Error);

/// <summary>
/// Battery-time pieces fitted from the station's own history (design.md §7 Battery time): the cuts, the clean discharge
/// stretches, the loss model over them shrunk toward the configured efficiency, and a
/// replay that scores a forecast against what the SOC really did. Pure functions over history rows, no I/O.
/// </summary>
internal static class DischargeModel
{
    /// <summary>The prior's weight, in hours of clean discharge: the fit moves off the configured efficiency as data outweighs it.</summary>
    public const double PriorHours = 2;

    /// <summary>A silence longer than this splits a cut: the readings after it cannot be told from a new cut.</summary>
    public const long SplitGapSec = 3 * 3600;

    /// <summary>The scored horizons, minutes.</summary>
    public static readonly int[] Horizons = [30, 60, 120];

    /// <summary>The first minutes of a cut, scored apart: the load settles (eco mode) and little is measured yet.</summary>
    public const int EarlyMin = 30;

    /// <summary>The first reading from this many seconds before the horizon to <see cref="TargetLateSec"/> after it stands for it.</summary>
    public const long TargetEarlySec = 30;

    /// <summary>See <see cref="TargetEarlySec"/>: a missed poll moves the target up to this much later.</summary>
    public const long TargetLateSec = 90;

    private static bool Off(HistoryRow r) => r.Mains < 0.5 && r.Soc is { } s && double.IsFinite(s);

    private static double Load(HistoryRow r) => Math.Max(0, (r.OutAc ?? 0) + (r.OutDc ?? 0));

    /// <summary>
    /// The cuts in time-ordered rows: runs of readings off mains with a SOC. A reading on mains ends a run; a row without a
    /// SOC is skipped; a silence over <see cref="SplitGapSec"/>, a SOC rise across a silence over <c>maxGapSec</c> or the
    /// clock stepping back starts a new run.
    /// </summary>
    public static List<Cut> Cuts(IReadOnlyList<HistoryRow> rows, StationConfig c)
    {
        var cuts = new List<Cut>();
        int start = -1, last = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Mains >= 0.5)
            {
                if (start >= 0) cuts.Add(new(start, last));
                start = -1;
                continue;
            }
            if (!Off(r)) continue;
            if (start >= 0)
            {
                var p = rows[last];
                long gap = r.At - p.At;
                if (gap <= 0 || gap > SplitGapSec || (gap > c.MaxGapSec && r.Soc > p.Soc))
                {
                    cuts.Add(new(start, last));
                    start = -1;
                }
            }
            if (start < 0) start = i;
            last = i;
        }
        if (start >= 0) cuts.Add(new(start, last));
        return cuts;
    }

    /// <summary>
    /// The clean stretches of a cut: from one SOC step down to a later one, at least <c>RateMinDrop</c> points over
    /// <c>RateMinSpanMin</c> minutes, closed at the first step that satisfies both. A gap over <c>maxGapSec</c>, DC input,
    /// a SOC rise or a single step over <c>socJumpFloor</c> points drops the stretch in progress; the next step starts one.
    /// The output energy counts each reading's load until the next reading.
    /// </summary>
    public static void Stretches(IReadOnlyList<HistoryRow> rows, Cut cut, StationConfig c, List<Stretch> into)
    {
        int from = -1;   // the row of the step the stretch starts at
        double wh = 0;
        for (int i = cut.Start + 1; i <= cut.End; i++)
        {
            var p = rows[i - 1];
            var r = rows[i];
            if (!Off(r)) continue;
            if (!Off(p)) { from = -1; continue; }
            double step = p.Soc!.Value - r.Soc!.Value;
            long dt = r.At - p.At;
            if (dt <= 0 || dt > c.MaxGapSec || r.InDc > 0 || p.InDc > 0 || step < 0 || step > c.SocJumpFloor)
            {
                from = -1;
                continue;
            }
            if (from >= 0) wh += Load(p) * dt / 3600.0;
            if (step <= 0) continue;
            if (from < 0) { from = i; wh = 0; continue; }
            double drop = rows[from].Soc!.Value - r.Soc!.Value;
            long span = r.At - rows[from].At;
            if (drop >= c.RateMinDrop && span >= c.RateMinSpanMin * 60L)
            {
                into.Add(new(drop, span / 3600.0, wh));
                from = i;
                wh = 0;
            }
        }
    }

    /// <summary>The prior: no idle draw, the configured efficiency for every load.</summary>
    public static LossFit Prior(StationConfig c) =>
        new(0, 100.0 / (c.CapacityWh * Math.Clamp(c.Efficiency, RuntimeEstimator.LearnedMin, 1)), 0);

    /// <summary>
    /// The loss model by least squares over the stretches (drop = Idle x hours + PerWh x Wh), shrunk toward the prior: the
    /// prior enters as <paramref name="priorHours"/> of discharge at half and at one and a half times the mean load, cut
    /// into pieces as long as the mean clean stretch (a stretch of h hours weighs h squared in the sums, so only pieces of
    /// the data's own length make the prior weigh as many hours of discharge as it is given), so with
    /// little data the fit stays near the configured efficiency and with much data the data decide. Idle below 0 refits with
    /// Idle = 0 (the bounded least squares). A fit outside the physical bounds (k below 0 or from 1) is rejected: the prior.
    /// </summary>
    public static LossFit Fit(IReadOnlyList<Stretch> stretches, StationConfig c, double priorHours = PriorHours)
    {
        var prior = Prior(c);
        double hours = 0, wh = 0;
        foreach (var s in stretches)
            if (s.Hours > 0 && double.IsFinite(s.Drop) && double.IsFinite(s.OutWh))
            {
                hours += s.Hours;
                wh += s.OutWh;
            }
        if (!(hours > 0) || !(wh > 0)) return prior;
        double mean = wh / hours;
        double shh = 0, she = 0, see = 0, shy = 0, sey = 0;
        int count = 0;
        void Add(double h, double e, double y, double pieces = 1)
        {
            shh += pieces * h * h;
            she += pieces * h * e;
            see += pieces * e * e;
            shy += pieces * h * y;
            sey += pieces * e * y;
        }
        foreach (var s in stretches)
            if (s.Hours > 0 && double.IsFinite(s.Drop) && double.IsFinite(s.OutWh))
            {
                Add(s.Hours, s.OutWh, s.Drop);
                count++;
            }
        if (priorHours > 0)
        {
            double h = hours / count;   // the mean clean stretch
            foreach (double f in (double[])[0.5, 1.5]) Add(h, f * mean * h, prior.Drain(f * mean) * h, priorHours / 2 / h);
        }
        double det = shh * see - she * she;
        double idle, perWh;
        if (det > 1e-9 * shh * see)
        {
            idle = (see * shy - she * sey) / det;
            perWh = (shh * sey - she * shy) / det;
        }
        else (idle, perWh) = (-1, 0);
        if (!(idle >= 0)) (idle, perWh) = (0, see > 0 ? sey / see : double.NaN);
        var fit = new LossFit(idle, perWh, hours);
        return Physical(fit, c) ? fit : prior;
    }

    /// <summary>The physical bounds: no negative idle draw, 0 &lt;= k &lt; 1 at the configured capacity.</summary>
    public static bool Physical(LossFit f, StationConfig c) =>
        double.IsFinite(f.Idle) && f.Idle >= 0 && double.IsFinite(f.K(c)) && f.K(c) >= 0 && f.K(c) < 1;

    /// <summary>The fewest held-out residuals an interval is read from.</summary>
    public const int MinResiduals = 30;

    /// <summary>The fewest cuts those residuals come from: a minute's residual is close to the next one, a cut is the independent unit.</summary>
    public const int MinCuts = 5;

    /// <summary>
    /// The model from the history: the loss fit over every clean stretch, and the interval from leave-one-cut-out residuals
    /// (each cut forecast at its current load by the fit without it; real drain minus forecast, points an hour) at 120
    /// minutes, else 60, else 30, with at least <see cref="MinResiduals"/> of them from <see cref="MinCuts"/> cuts. One pass
    /// over the rows plus one replay; run on a refit, never on each poll.
    /// </summary>
    public static FittedRuntime Learn(IReadOnlyList<HistoryRow> rows, StationConfig c, long nowSec)
    {
        var cuts = Cuts(rows, c);
        var per = new List<Stretch>[cuts.Count];
        var all = new List<Stretch>();
        for (int k = 0; k < cuts.Count; k++)
        {
            per[k] = [];
            Stretches(rows, cuts[k], c, per[k]);
            all.AddRange(per[k]);
        }
        var fit = Fit(all, c);
        var errors = Horizons.ToDictionary(h => h, _ => new List<double>());
        var cutsSeen = Horizons.ToDictionary(h => h, _ => 0);
        for (int k = 0; k < cuts.Count; k++)
        {
            var others = new List<Stretch>(all.Count);
            for (int j = 0; j < cuts.Count; j++) if (j != k) others.AddRange(per[j]);
            var held = Fit(others, c);
            var before = Horizons.ToDictionary(h => h, h => errors[h].Count);
            // the error is predicted minus real SOC: a positive one = the real drop was larger = the real drain faster
            Replay(rows, [cuts[k]], (i, _, _) => held.Drain(Load(rows[i])), r => errors[r.Horizon].Add(r.Error * 60 / r.Horizon));
            foreach (int h in Horizons) if (errors[h].Count > before[h]) cutsSeen[h]++;
        }
        for (int h = Horizons.Length - 1; h >= 0; h--)
        {
            var e = errors[Horizons[h]];
            if (e.Count < MinResiduals || cutsSeen[Horizons[h]] < MinCuts) continue;
            e.Sort();
            return new(fit, Quantile(e, 0.1), Quantile(e, 0.9), nowSec);
        }
        return new(fit, null, null, nowSec);
    }

    /// <summary>The <paramref name="q"/> quantile of sorted values, linear between neighbours.</summary>
    public static double Quantile(List<double> sorted, double q)
    {
        double pos = q * (sorted.Count - 1);
        int lo = (int)Math.Floor(pos);
        int hi = Math.Min(sorted.Count - 1, lo + 1);
        return sorted[lo] + (pos - lo) * (sorted[hi] - sorted[lo]);
    }

    /// <summary>
    /// Replays the cuts: at each reading off mains, the forecast drain (SOC points an hour over the next h minutes, from
    /// <paramref name="drain"/>, which must read only rows up to the index it gets) against the real SOC h minutes later
    /// in the same cut, for each of <see cref="Horizons"/>. A forecast is never below 0 %. No target near the horizon = not
    /// scored.
    /// </summary>
    public static void Replay(IReadOnlyList<HistoryRow> rows, IReadOnlyList<Cut> cuts, Func<int, Cut, int, double?> drain, Action<Residual> sink)
    {
        for (int ci = 0; ci < cuts.Count; ci++)
        {
            var cut = cuts[ci];
            long t0 = rows[cut.Start].At;
            foreach (int h in Horizons)
            {
                int j = cut.Start;
                for (int i = cut.Start; i <= cut.End; i++)
                {
                    if (!Off(rows[i])) continue;
                    long want = rows[i].At + h * 60L;
                    if (j < i) j = i;
                    while (j < cut.End && rows[j].At < want - TargetEarlySec) j++;
                    if (rows[j].At < want - TargetEarlySec) break;   // the cut ends before the horizon
                    if (rows[j].At > want + TargetLateSec || !Off(rows[j])) continue;
                    if (drain(i, cut, h) is not { } d || !double.IsFinite(d)) continue;
                    double predicted = Math.Max(0, rows[i].Soc!.Value - d * h / 60.0);
                    sink(new(ci, h, rows[i].At - t0 < EarlyMin * 60L, predicted - rows[j].Soc!.Value));
                }
            }
        }
    }
}
