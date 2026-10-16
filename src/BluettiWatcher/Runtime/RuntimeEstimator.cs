using BluettiWatcher.Decision;
using BluettiWatcher.DtekWatcher;
using BluettiWatcher.Storage;

namespace BluettiWatcher.Runtime;

/// <summary>
/// A battery-time estimate. <see cref="Source"/>: <c>model</c> (the loss model fitted from the station's history),
/// <c>rate</c> (the measured drop), <c>learned</c> (capacity x the learned efficiency), <c>config</c> (capacity x the
/// configured efficiency); null with no estimate (on mains, no load). <see cref="MeasuredEfficiency"/>: this window's
/// efficiency, set only when the window is clean enough to learn from. <see cref="HoursLeftLow"/> and
/// <see cref="HoursLeftHigh"/>: the model's P10-P90 range, set only with <c>model</c> and an interval; high null = unbounded.
/// </summary>
internal sealed record RuntimeEstimate(double? HoursLeft, double? HoursToHibernate, string? Source, double? RatePerHour, double? MeasuredEfficiency,
    double? HoursLeftLow = null, double? HoursLeftHigh = null)
{
    public static readonly RuntimeEstimate None = new(null, null, null, null, null);
}

/// <summary>The measured discharge over a window: SOC points per hour, the mean output, and whether it is clean enough to learn from.</summary>
internal readonly record struct MeasuredRate(double Rate, double MeanLoad, bool Clean);

/// <summary>
/// The battery time from the real discharge: the efficiency the SOC drop over the recent readings off mains shows, applied
/// to the current load; the efficiency formula only while there is too little data. It informs only: no battery level and
/// no hibernate reads it.
/// </summary>
internal static class RuntimeEstimator
{
    public const double LearnedMin = 0.4, LearnedMax = 0.95;
    /// <summary>A learned efficiency never exceeds the configured one by more than this.</summary>
    public const double LearnedOverConfig = 0.05;

    /// <summary>
    /// The measured rate, SOC points per hour, and the mean output over the span it was measured on; null when the data is
    /// too thin or suspect. Only the trailing run of readings off mains within the window counts: it ends at a reading on
    /// mains, a reading without SOC or a gap over <c>maxGapSec</c>. The SOC moves in whole points, so the span runs from the
    /// first to the last SOC step inside the run, never from a plateau's middle. A SOC rise inside the run = no rate.
    /// Clean (fit to learn from): no DC input and no single SOC step over <c>socJumpFloor</c> points within the span.
    /// </summary>
    internal static MeasuredRate? Rate(IReadOnlyList<HistoryRow> rows, long nowSec, StationConfig c)
    {
        if (c.RateWindowMin <= 0) return null;
        long from = nowSec - c.RateWindowMin * 60L;
        var run = new List<HistoryRow>();
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            var r = rows[i];
            if (r.At > nowSec) continue;   // ahead of the clock (a step back): not evidence
            if (r.At < from) break;
            if (r.Mains >= 0.5 || r.Soc is not { } soc || !double.IsFinite(soc)) break;
            if (run.Count > 0 && run[^1].At - r.At > c.MaxGapSec) break;
            run.Add(r);
        }
        run.Reverse();
        int first = -1, last = -1;
        for (int k = 1; k < run.Count; k++)
        {
            if (run[k].Soc > run[k - 1].Soc) return null;   // a jump up during a cut
            if (run[k].Soc < run[k - 1].Soc) { if (first < 0) first = k; last = k; }
        }
        if (first < 0 || last <= first) return null;
        double drop = run[first].Soc!.Value - run[last].Soc!.Value;
        long span = run[last].At - run[first].At;
        if (drop < c.RateMinDrop || span < c.RateMinSpanMin * 60L) return null;
        double rate = drop / (span / 3600.0);
        if (!double.IsFinite(rate) || rate <= 0) return null;
        double load = 0;
        bool clean = true;
        for (int k = first; k <= last; k++)
        {
            load += (run[k].OutAc ?? 0) + (run[k].OutDc ?? 0);
            if (run[k].InDc > 0 || (k > first && run[k - 1].Soc - run[k].Soc > c.SocJumpFloor)) clean = false;
        }
        return new MeasuredRate(rate, load / (last - first + 1), clean);
    }

    /// <summary>
    /// Hours of battery at the current reading's load, to empty and to <c>hibernateAt</c>; null on mains or with no load.
    /// Measured: SOC / rate x mean output over the window / current output, so a load raised now shortens the time now.
    /// <paramref name="learned"/>: the learned efficiency, used instead of the configured one when it is sound.
    /// <paramref name="model"/>: the fitted model; when it rests on at least <see cref="DischargeModel.PriorHours"/> of clean
    /// data (as much as the prior weighs) and is within the physical bounds it gives the time instead (its drain at the
    /// current load); the measured rate and efficiency are still reported beside it. With DC input the model stands aside:
    /// it was fitted without any and cannot tell what the panels give, while the measured drop includes it.
    /// <paramref name="cut"/>: where the current cut stands; with the model's eco profile, the load is taken down by the eco
    /// factor from when eco is due until it comes on (null = not known, the current load throughout).
    /// </summary>
    public static RuntimeEstimate Estimate(Reading? current, IReadOnlyList<HistoryRow> rows, long nowSec, StationConfig c, double? learned,
        FittedRuntime? model = null, CutState? cut = null)
    {
        if (current is not { Soc: { } soc } || !double.IsFinite(soc) || current.InAc > 0) return RuntimeEstimate.None;
        double load = (current.OutAc ?? 0) + (current.OutDc ?? 0);
        if (!(load > 0)) return RuntimeEstimate.None;
        var rate = Rate(rows, nowSec, c);
        double? measured = null;
        if (rate is { } m)
        {
            double eff = m.MeanLoad / (m.Rate / 100 * c.CapacityWh);
            measured = m.Clean && double.IsFinite(eff) && eff > 0 ? Math.Round(Bound(eff, c), 3) : null;
        }
        if (model is { Fit: var fit } && fit.CleanHours >= DischargeModel.PriorHours && !(current.InDc > 0)
            && DischargeModel.Physical(fit, c) && fit.Drain(load) > 0
            && DischargeModel.HoursFor(fit, model.Eco ?? EcoProfile.None, load, nowSec, cut, soc) is var hours && double.IsFinite(hours))
        {
            var eco = model.Eco ?? EcoProfile.None;
            double drain = hours > 0 && double.IsFinite(hours) ? soc / hours : fit.Drain(load);   // the mean drain to empty
            double? low = null, high = null;
            if (model.ErrLo is { } lo && model.ErrHi is { } hi)
            {
                // the range always holds the estimate; a drain that may reach 0 leaves the top open
                double fast = Math.Max(drain, drain + hi), slow = Math.Min(drain, drain + lo);
                low = Cap(soc / fast);
                high = slow > 0 ? Cap(soc / slow) : null;
            }
            return new(Cap(hours), Cap(DischargeModel.HoursFor(fit, eco, load, nowSec, cut, Math.Max(0, soc - c.HibernateAt))), "model",
                rate is { } r ? Math.Round(r.Rate, 1) : null, measured, low, high);
        }
        if (rate is { } w)
        {
            double scale = w.MeanLoad > 0 ? w.MeanLoad / load : 1;
            return new(Cap(soc / w.Rate * scale), Cap(Math.Max(0, soc - c.HibernateAt) / w.Rate * scale), "rate", Math.Round(w.Rate, 1), measured);
        }
        double? sound = Sound(learned, c);
        double e = sound ?? c.Efficiency;
        return new(Cap(c.CapacityWh * soc / 100 * e / load), Cap(c.CapacityWh * Math.Max(0, soc - c.HibernateAt) / 100 * e / load),
            sound is null ? "config" : "learned", null, null);
    }

    /// <summary>An efficiency held within 0.4-0.95 and at most the configured one plus <see cref="LearnedOverConfig"/>.</summary>
    public static double Bound(double eff, StationConfig c) => Math.Clamp(eff, LearnedMin, Math.Max(LearnedMin, Math.Min(LearnedMax, c.Efficiency + LearnedOverConfig)));

    /// <summary>A learned efficiency within its bounds, else null.</summary>
    public static double? Sound(double? learned, StationConfig c) =>
        learned is { } l && double.IsFinite(l) && l >= LearnedMin && l <= LearnedMax ? Bound(l, c) : null;

    /// <summary>The learned value moved toward a new clean measurement: the first one is taken whole, later ones by <paramref name="weight"/>.</summary>
    public static double Smooth(double? learned, double measured, double weight) => learned is { } l ? l + weight * (measured - l) : measured;

    private static double? Cap(double hours) => double.IsFinite(hours) && hours >= 0 ? Math.Round(Math.Min(hours, DtekOutlook.MaxHours), 1) : null;
}
