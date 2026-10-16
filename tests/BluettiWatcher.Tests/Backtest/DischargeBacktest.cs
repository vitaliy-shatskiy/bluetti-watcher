using System.Globalization;
using System.Text;
using BluettiWatcher.Runtime;
using BluettiWatcher.Storage;
using Microsoft.Data.Sqlite;

namespace BluettiWatcher.Tests.Backtest;

/// <summary>
/// The battery-time backtest over a real station.db: replays every cut and scores each candidate forecast of the SOC 30,
/// 60 and 120 minutes ahead, every fitted value chosen leave-one-cut-out. Runs only on request, on a database named by
/// BW_BACKTEST_DB, and writes its report to BW_BACKTEST_OUT; the database and the report never enter the repository.
/// </summary>
public sealed class DischargeBacktest
{
    [Fact(Explicit = true)]
    public void Replays_the_cuts_of_a_real_database()
    {
        string? db = Environment.GetEnvironmentVariable("BW_BACKTEST_DB");
        string? output = Environment.GetEnvironmentVariable("BW_BACKTEST_OUT");
        Assert.SkipWhen(string.IsNullOrEmpty(db) || string.IsNullOrEmpty(output), "BW_BACKTEST_DB and BW_BACKTEST_OUT name the database and the report");
        Assert.True(File.Exists(db), $"no database at {db}");
        var (rows, eco) = Load(db!);
        var report = new Backtester(rows, eco, new StationConfig()).Run();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output!))!);
        File.WriteAllText(output!, report);
    }

    /// <summary>Accepted readings (raw and 5-minute aggregates) oldest first, and the eco-on times, read-only.</summary>
    internal static (List<HistoryRow> Rows, List<long> Eco) Load(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        var rows = new List<HistoryRow>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT at, soc, in_ac, in_dc, out_ac, out_dc, (in_ac > 0) * 1.0 FROM reading WHERE status=0
                UNION ALL SELECT at, soc, in_ac, in_dc, out_ac, out_dc, COALESCE(mains, 0) FROM agg5 WHERE n > 0
                ORDER BY 1
                """;
            using var r = cmd.ExecuteReader();
            static double? D(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
            while (r.Read()) rows.Add(new(r.GetInt64(0), D(r, 1), D(r, 2), D(r, 3), D(r, 4), D(r, 5), null, null, D(r, 6) ?? 0, null));
        }
        var eco = new List<long>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT at FROM event WHERE kind='eco-on' ORDER BY at";
            using var r = cmd.ExecuteReader();
            while (r.Read()) eco.Add(r.GetInt64(0));
        }
        return (rows, eco);
    }
}

/// <summary>The replay and the candidates; see <see cref="DischargeBacktest"/>.</summary>
internal sealed class Backtester
{
    private readonly HistoryRow[] _rows;
    private readonly List<long> _eco;
    private readonly StationConfig _c;
    private readonly List<Cut> _cuts;
    private readonly int[] _cutOf;
    private readonly List<Stretch>[] _stretches;
    private readonly double[][] _ewma;      // [half-life][row]: the load weighted back to the cut's start
    private readonly double[] _baseline;    // the current estimator's drain at each row, the learned efficiency replayed online
    private readonly double[] _baselineEwma;
    private readonly StringBuilder _notes = new();

    /// <summary>The half-life grid for the load forecast, minutes; 0 = the latest reading.</summary>
    private static readonly int[] HalfLives = [0, 5, 10, 15, 20, 30, 45, 60, 90, 120];

    private static readonly double[] TimeToLevels = [60, 50, 40, 30, 20];

    public Backtester(List<HistoryRow> rows, List<long> eco, StationConfig c)
    {
        _rows = [.. rows];
        _eco = eco;
        _c = c;
        _cuts = DischargeModel.Cuts(_rows, c);
        _cutOf = new int[_rows.Length];
        Array.Fill(_cutOf, -1);
        for (int k = 0; k < _cuts.Count; k++)
            for (int i = _cuts[k].Start; i <= _cuts[k].End; i++) _cutOf[i] = k;
        _stretches = [.. _cuts.Select(cut => { var l = new List<Stretch>(); DischargeModel.Stretches(_rows, cut, c, l); return l; })];
        _ewma = [.. HalfLives.Select(Ewma)];
        (_baseline, _baselineEwma) = Baseline();
    }

    private static double Load(HistoryRow r) => Math.Max(0, (r.OutAc ?? 0) + (r.OutDc ?? 0));

    /// <summary>The weighted load by the recursion (each reading's weight halves every half-life), reset at each cut.</summary>
    private double[] Ewma(int hlMin)
    {
        var e = new double[_rows.Length];
        foreach (var cut in _cuts)
        {
            double sw = 0, sp = 0;
            long prev = _rows[cut.Start].At;
            for (int i = cut.Start; i <= cut.End; i++)
            {
                var r = _rows[i];
                if (r.Mains >= 0.5 || r.Soc is null) { e[i] = i > cut.Start ? e[i - 1] : Load(r); continue; }
                if (hlMin == 0) { e[i] = Load(r); continue; }
                double decay = Math.Pow(2, -(r.At - prev) / (hlMin * 60.0));
                sw = sw * decay + 1;
                sp = sp * decay + Load(r);
                prev = r.At;
                e[i] = sp / sw;
            }
        }
        return e;
    }

    /// <summary>
    /// The current estimator at each row: the measured rate over the window scaled to the current load (and, for B, to the
    /// weighted load), else capacity x efficiency, the learned efficiency moved online the way the service moves it.
    /// </summary>
    private (double[] Now, double[] Ewma) Baseline()
    {
        var now = new double[_rows.Length];
        var ewma = new double[_rows.Length];
        double? learned = null;
        long learnedAt = 0;
        int lo = 0;
        foreach (var cut in _cuts)
            for (int i = cut.Start; i <= cut.End; i++)
            {
                long at = _rows[i].At;
                while (_rows[lo].At < at - _c.RateWindowMin * 60L) lo++;
                var window = new ArraySegment<HistoryRow>(_rows, lo, i - lo + 1);
                double load = Load(_rows[i]);
                var m = RuntimeEstimator.Rate(window, at, _c);
                double eff = RuntimeEstimator.Sound(learned, _c) ?? _c.Efficiency;
                double perW = 100.0 / (_c.CapacityWh * eff);
                if (m is { } r)
                {
                    double perLoad = r.MeanLoad > 0 ? r.Rate / r.MeanLoad : perW;
                    now[i] = r.MeanLoad > 0 ? perLoad * load : r.Rate;
                    double e = r.MeanLoad / (r.Rate / 100 * _c.CapacityWh);
                    if (r.Clean && double.IsFinite(e) && e > 0 && (learned is null || at - learnedAt >= _c.RateWindowMin * 60L))
                    {
                        learned = Math.Round(RuntimeEstimator.Bound(RuntimeEstimator.Smooth(learned, RuntimeEstimator.Bound(e, _c), Http.StationApi.LearnWeight), _c), 3);
                        learnedAt = at;
                    }
                    ewma[i] = perLoad;   // the window's points per W; times the weighted load later
                }
                else
                {
                    now[i] = perW * load;
                    ewma[i] = perW;
                }
            }
        _notes.Append(CultureInfo.InvariantCulture, $"- The baseline's learned efficiency, replayed online from the configured {_c.Efficiency}: {learned?.ToString("0.###", CultureInfo.InvariantCulture) ?? "never learned"} at the end.\n");
        return (now, ewma);
    }

    /// <summary>Mean absolute error per (horizon, early) and overall per horizon over the residuals.</summary>
    private sealed class Score
    {
        public readonly Dictionary<(int H, bool? Early), (double Sum, int N)> Cells = [];

        public void Add(Residual r)
        {
            foreach (bool? key in (bool?[])[r.Early, null])
            {
                var cell = Cells.GetValueOrDefault((r.Horizon, key));
                Cells[(r.Horizon, key)] = (cell.Sum + Math.Abs(r.Error), cell.N + 1);
            }
        }

        public double Mae(int h, bool? early = null) => Cells.TryGetValue((h, early), out var c) && c.N > 0 ? c.Sum / c.N : double.NaN;
        public int N(int h, bool? early = null) => Cells.TryGetValue((h, early), out var c) ? c.N : 0;
        public double Objective => Mae(60) + Mae(120);
    }

    private List<Residual> Replay(Func<int, Cut, int, double?> drain, Func<int, bool> cutFilter)
    {
        var list = new List<Residual>();
        DischargeModel.Replay(_rows, _cuts, (i, cut, h) => cutFilter(_cutOf[i]) ? drain(i, cut, h) : null, list.Add);
        return list;
    }

    private static Score ScoreOf(IEnumerable<Residual> rs)
    {
        var s = new Score();
        foreach (var r in rs) s.Add(r);
        return s;
    }

    private LossFit FitExcept(int held, double priorHours = DischargeModel.PriorHours) =>
        DischargeModel.Fit([.. _stretches.Where((_, k) => k != held).SelectMany(s => s)], _c, priorHours);

    /// <summary>The half-life (index into the grid) that scores best on the training cuts for a drain built from it.</summary>
    private int BestHalfLife(int held, Func<int, Func<int, Cut, int, double?>> drainFor)
    {
        int best = 0;
        double bestScore = double.MaxValue;
        for (int h = 0; h < HalfLives.Length; h++)
        {
            double s = ScoreOf(Replay(drainFor(h), k => k != held)).Objective;
            if (s < bestScore) (best, bestScore) = (h, s);
        }
        return best;
    }

    /// <summary>A candidate: given the held-out cut, the drain forecast fitted without it (fitted once per fold).</summary>
    private sealed record Candidate(string Name, string What, Func<int, Func<int, Cut, int, double?>> Make)
    {
        private readonly Dictionary<int, Func<int, Cut, int, double?>> _folds = [];

        public Func<int, Cut, int, double?> Fold(int held)
        {
            if (!_folds.TryGetValue(held, out var f)) _folds[held] = f = Make(held);
            return f;
        }
    }

    public string Run()
    {
        var c = _c;
        var fits = new LossFit[_cuts.Count];
        for (int k = 0; k < _cuts.Count; k++) fits[k] = FitExcept(k);
        var chosen = new Dictionary<string, List<int>>();
        void Chose(string name, int h) { if (!chosen.TryGetValue(name, out var l)) chosen[name] = l = []; l.Add(HalfLives[h]); }

        var seasonal = new Seasonal(_rows, _cuts);
        var ecoRatio = new EcoRatio(_rows, _cuts, _cutOf, _eco);

        var candidates = new List<Candidate>
        {
            new("A baseline", "the current estimator: window rate x current / window load, else capacity x efficiency (learned online)",
                _ => (i, _, _) => _baseline[i]),
            new("B", "A with the load = EWMA, half-life fitted",
                held =>
                {
                    int h = BestHalfLife(held, h => (i, _, _) => _baselineEwma[i] * _ewma[h][i]);
                    Chose("B", h);
                    return (i, _, _) => _baselineEwma[i] * _ewma[h][i];
                }),
            new("C", "loss model P0 + (1+k) x P, fitted, at the current load",
                held => (i, _, _) => fits[held].Drain(Load(_rows[i]))),
            new("B+C", "loss model at the EWMA load, half-life fitted",
                held =>
                {
                    var f = fits[held];
                    int h = BestHalfLife(held, h => (i, _, _) => f.Drain(_ewma[h][i]));
                    Chose("B+C", h);
                    return (i, _, _) => f.Drain(_ewma[h][i]);
                }),
            new("D eco", "B+C, the load scaled by the learned eco-mode ratio until eco mode is on",
                held =>
                {
                    var f = fits[held];
                    double ratio = ecoRatio.Ratio(held);
                    double Pl(int i, int h) => _ewma[h][i] * (ecoRatio.Pending(i, held) ? ratio : 1);
                    int h = BestHalfLife(held, h => (i, _, _) => f.Drain(Pl(i, h)));
                    Chose("D eco", h);
                    return (i, _, _) => f.Drain(Pl(i, h));
                }),
            new("D hour", "B+C, the load shaped by the hour-of-day profile (UTC hours) over the horizon",
                held =>
                {
                    var f = fits[held];
                    var prof = seasonal.Profile(held);
                    double Pl(int i, int hl, int hor) => _ewma[hl][i] / prof.At(_rows[i].At) * prof.Mean(_rows[i].At, hor);
                    int h = BestHalfLife(held, h => (i, _, hor) => f.Drain(Pl(i, h, hor)));
                    Chose("D hour", h);
                    return (i, _, hor) => f.Drain(Pl(i, h, hor));
                }),
        };

        var residuals = new Dictionary<string, List<Residual>>();
        foreach (var cand in candidates)
        {
            var all = new List<Residual>();
            for (int k = 0; k < _cuts.Count; k++)
            {
                var drain = cand.Fold(k);
                all.AddRange(Replay(drain, x => x == k));
            }
            residuals[cand.Name] = all;
        }

        // E: the prior's weight, judged chronologically: each cut forecast from the cuts before it only (candidate C).
        var chrono = new List<(string Name, Score S)>();
        foreach (double n0 in (double[])[0, 0.5, 2, 8, 1e9])
        {
            var all = new List<Residual>();
            for (int k = 0; k < _cuts.Count; k++)
            {
                var f = DischargeModel.Fit([.. _stretches.Take(k).SelectMany(s => s)], c, n0);
                all.AddRange(Replay((i, _, _) => f.Drain(Load(_rows[i])), x => x == k));
            }
            chrono.Add((n0 >= 1e9 ? "prior only" : $"prior {n0} h", ScoreOf(all)));
        }
        var chronoBase = ScoreOf(Replay((i, _, _) => _baseline[i], _ => true));

        return Report(candidates, residuals, fits, chosen, chrono, chronoBase, ecoRatio, seasonal);
    }

    private string Report(List<Candidate> candidates, Dictionary<string, List<Residual>> residuals, LossFit[] fits,
        Dictionary<string, List<int>> chosen, List<(string Name, Score S)> chrono, Score chronoBase, EcoRatio eco, Seasonal seasonal)
    {
        var c = _c;
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        string F(double v, string fmt = "0.00") => double.IsFinite(v) ? v.ToString(fmt, inv) : "-";
        var allStretches = _stretches.SelectMany(s => s).ToList();
        var full = DischargeModel.Fit(allStretches, c);
        double onBattery = _cuts.Sum(k => _rows[k.End].At - _rows[k.Start].At) / 3600.0;

        sb.Append("# Battery-time backtest\n\n");
        sb.Append(inv, $"Data: {_rows.Length} accepted readings, {_cuts.Count} cuts, {F(onBattery, "0.0")} h on battery, {allStretches.Count} clean stretches ({F(allStretches.Sum(s => s.Hours), "0.0")} h). ");
        sb.Append(inv, $"Configured: {c.CapacityWh} Wh, efficiency {c.Efficiency}. Every fitted value is chosen leave-one-cut-out (fit on the other cuts, score the held-out one).\n\n");
        sb.Append("Cuts (relative, no clock times): ");
        sb.Append(string.Join(", ", _cuts.Select(k => $"{(_rows[k.End].At - _rows[k.Start].At) / 60} min {F(_rows[k.Start].Soc ?? 0, "0")}->{F(_rows[k.End].Soc ?? 0, "0")} %")));
        sb.Append("\n\n## SOC forecast MAE, SOC points (held-out cuts)\n\n");
        sb.Append("| Candidate | 30 min early | 30 min late | 60 min early | 60 min late | 120 min early | 120 min late | 30 all | 60 all | 120 all |\n|---|---|---|---|---|---|---|---|---|---|\n");
        foreach (var cand in candidates)
        {
            var s = ScoreOf(residuals[cand.Name]);
            sb.Append(inv, $"| {cand.Name} | {F(s.Mae(30, true))} | {F(s.Mae(30, false))} | {F(s.Mae(60, true))} | {F(s.Mae(60, false))} | {F(s.Mae(120, true))} | {F(s.Mae(120, false))} | {F(s.Mae(30))} | {F(s.Mae(60))} | {F(s.Mae(120))} |\n");
        }
        var b = ScoreOf(residuals["A baseline"]);
        sb.Append(inv, $"\nScored forecasts per horizon (all / early): 30 min {b.N(30)}/{b.N(30, true)}, 60 min {b.N(60)}/{b.N(60, true)}, 120 min {b.N(120)}/{b.N(120, true)}. Same points for every candidate.\n\n");
        sb.Append("Candidates:\n\n");
        foreach (var cand in candidates) sb.Append(inv, $"- **{cand.Name}**: {cand.What}.\n");

        sb.Append("\n## Per-cut MAE at 60 / 120 min (held-out)\n\n| Cut | A baseline | B | C | B+C |\n|---|---|---|---|---|\n");
        for (int k = 0; k < _cuts.Count; k++)
        {
            sb.Append(inv, $"| {k + 1} ");
            foreach (string n in (string[])["A baseline", "B", "C", "B+C"])
            {
                var s = ScoreOf(residuals[n].Where(r => r.CutIndex == k));
                sb.Append(inv, $"| {F(s.Mae(60))} / {F(s.Mae(120))} ");
            }
            sb.Append("|\n");
        }

        sb.Append("\n## Time to X % error, minutes (cuts that reached X; forecasts made above X)\n\n| Candidate |");
        foreach (double x in TimeToLevels) sb.Append(inv, $" {x} % |");
        sb.Append("\n|---|" + string.Concat(TimeToLevels.Select(_ => "---|")) + "\n");
        foreach (var cand in candidates.Where(x => x.Name is "A baseline" or "C" or "B+C"))
        {
            sb.Append(inv, $"| {cand.Name} |");
            foreach (double x in TimeToLevels) sb.Append(inv, $" {TimeTo(cand, x)} |");
            sb.Append('\n');
        }

        sb.Append("\n## Fitted values\n\n");
        sb.Append(inv, $"- Loss model on all cuts: P0 = {F(full.IdleW(c), "0")} W, k = {F(full.K(c), "0.000")}, Wh delivered per 100 % (capacity / (1 + k)) = {F(100 / full.PerWh, "0")} Wh, equivalent efficiency at the mean load = ");
        double meanLoad = allStretches.Sum(s => s.OutWh) / Math.Max(1e-9, allStretches.Sum(s => s.Hours));
        sb.Append(inv, $"{F(meanLoad / (full.Drain(meanLoad) / 100 * c.CapacityWh), "0.000")} at {F(meanLoad, "0")} W.\n");
        var unshrunk = DischargeModel.Fit(allStretches, c, 0);
        sb.Append(inv, $"- Same without the prior: P0 = {F(unshrunk.IdleW(c), "0")} W, k = {F(unshrunk.K(c), "0.000")}.\n");
        sb.Append(inv, $"- Per fold: P0 {F(fits.Min(f => f.IdleW(c)), "0")}-{F(fits.Max(f => f.IdleW(c)), "0")} W, k {F(fits.Min(f => f.K(c)), "0.000")}-{F(fits.Max(f => f.K(c)), "0.000")}.\n");
        sb.Append("- Capacity: the SOC drop fixes only capacity / (1 + k), so capacity and k cannot be told apart from this data; the model keeps the ratio and reads k at the configured capacity. The guards 0 <= k < 1 hold the ratio within 50-100 % of the configured capacity.\n");
        foreach (var (name, hls) in chosen)
            sb.Append(inv, $"- Half-life chosen for {name} per fold (min): {string.Join(", ", hls)}.\n");
        sb.Append(inv, $"- Eco mode: eco-on in {eco.CutsWithEco} of {_cuts.Count} cuts; load after / before eco on (all cuts) = {F(eco.Ratio(-1), "0.000")}.\n");
        sb.Append(inv, $"- Hour-of-day profile (all cuts, battery readings, UTC hour: relative load): {seasonal.Profile(-1)}.\n");

        sb.Append("\n## E: the prior's weight, chronological (each cut forecast from earlier cuts only, C)\n\n| Prior | 60 all | 120 all | 60 early | 120 late |\n|---|---|---|---|---|\n");
        sb.Append(inv, $"| A baseline (online) | {F(chronoBase.Mae(60))} | {F(chronoBase.Mae(120))} | {F(chronoBase.Mae(60, true))} | {F(chronoBase.Mae(120, false))} |\n");
        foreach (var (name, s) in chrono)
            sb.Append(inv, $"| {name} | {F(s.Mae(60))} | {F(s.Mae(120))} | {F(s.Mae(60, true))} | {F(s.Mae(120, false))} |\n");

        sb.Append("\n## F: P10-P90 interval, split-conformal over held-out residuals\n\n");
        foreach (string name in (string[])["A baseline", "C", "B+C"])
        {
            sb.Append(inv, $"- {name}: coverage on held-out cuts (target 80 %): ");
            sb.Append(string.Join(", ", DischargeModel.Horizons.SelectMany(h => (bool[])[true, false], (h, e) => (h, e)).Select(p =>
            {
                var (cov, lo, hi) = Coverage(residuals[name], p.h, p.e);
                return $"{p.h} min {(p.e ? "early" : "late")} {F(cov * 100, "0")} % [{F(lo, "0.0")}, {F(hi, "0.0")}]";
            })));
            sb.Append('\n');
        }

        sb.Append("\n## Decision rule: beats the baseline at both 60 and 120 min (all stages, held-out)\n\n");
        foreach (var cand in candidates.Skip(1))
        {
            var s = ScoreOf(residuals[cand.Name]);
            bool wins = s.Mae(60) < b.Mae(60) && s.Mae(120) < b.Mae(120);
            sb.Append(inv, $"- {cand.Name}: {(wins ? "yes" : "no")} (60 min {F(s.Mae(60))} vs {F(b.Mae(60))}, 120 min {F(s.Mae(120))} vs {F(b.Mae(120))}).\n");
        }

        // The service's own path: DischargeModel.Learn on the history without one cut, its range against that cut.
        sb.Append("\n## The shipped model (DischargeModel.Learn, as the service runs it)\n\n");
        var shipped = DischargeModel.Learn(_rows, c, _rows[^1].At);
        sb.Append(inv, $"- On all history: P0 = {F(shipped.Fit.IdleW(c), "0")} W, k = {F(shipped.Fit.K(c), "0.000")}, clean {F(shipped.Fit.CleanHours, "0.0")} h, drain error P10-P90 = [{F(shipped.ErrLo ?? double.NaN)}, {F(shipped.ErrHi ?? double.NaN)}] points/h.\n");
        int hits = 0, scored = 0;
        for (int k = 0; k < _cuts.Count; k++)
        {
            var cut = _cuts[k];
            var rest = _rows.Where((_, i) => i < cut.Start || i > cut.End).ToList();
            var m = DischargeModel.Learn(rest, c, _rows[^1].At);
            if (m.ErrLo is not { } lo || m.ErrHi is not { } hi) continue;
            DischargeModel.Replay(_rows, [cut], (i, _, _) => m.Fit.Drain(Load(_rows[i])), r =>
            {
                if (r.Horizon != 120) return;
                double err = r.Error * 60 / r.Horizon;   // real drain minus forecast, as Learn keeps it
                scored++;
                if (err >= lo && err <= hi) hits++;
            });
        }
        sb.Append(inv, $"- Range coverage on held-out cuts, drain error over the next 120 min (target 80 %): {F(scored > 0 ? 100.0 * hits / scored : double.NaN, "0")} % of {scored}.\n");

        sb.Append("\n## Notes\n\n");
        sb.Append(_notes);
        return sb.ToString();
    }

    /// <summary>
    /// The share of a cut's residuals inside the P10-P90 of the other cuts' residuals at the same horizon and stage, pooled
    /// over the cuts; and the bounds over all cuts (predicted minus real, SOC points).
    /// </summary>
    private (double Coverage, double Lo, double Hi) Coverage(List<Residual> rs, int h, bool early)
    {
        int inside = 0, n = 0;
        var cell = rs.Where(r => r.Horizon == h && r.Early == early).ToList();
        for (int k = 0; k < _cuts.Count; k++)
        {
            var others = cell.Where(r => r.CutIndex != k).Select(r => r.Error).Order().ToList();
            if (others.Count < 10) continue;
            double lo = Quantile(others, 0.1), hi = Quantile(others, 0.9);
            foreach (var r in cell.Where(r => r.CutIndex == k)) { n++; if (r.Error >= lo && r.Error <= hi) inside++; }
        }
        var sorted = cell.Select(r => r.Error).Order().ToList();
        return (n > 0 ? (double)inside / n : double.NaN, sorted.Count > 0 ? Quantile(sorted, 0.1) : double.NaN, sorted.Count > 0 ? Quantile(sorted, 0.9) : double.NaN);
    }

    private static double Quantile(List<double> sorted, double q) => DischargeModel.Quantile(sorted, q);

    /// <summary>Time-to-X error in minutes: each forecast made above X in a cut that reached X, held out, at the 120-min drain.</summary>
    private string TimeTo(Candidate cand, double x)
    {
        double sum = 0;
        int n = 0;
        for (int k = 0; k < _cuts.Count; k++)
        {
            var cut = _cuts[k];
            int hit = -1;
            for (int i = cut.Start; i <= cut.End; i++) if (_rows[i].Soc <= x) { hit = i; break; }
            if (hit < 0) continue;
            var drain = cand.Fold(k);
            for (int i = cut.Start; i < hit; i++)
            {
                if (_rows[i].Soc is not { } soc || _rows[i].Mains >= 0.5) continue;
                if (drain(i, cut, 120) is not { } d || !(d > 0)) continue;
                double predicted = (soc - x) / d * 60, actual = (_rows[hit].At - _rows[i].At) / 60.0;
                sum += Math.Abs(predicted - actual);
                n++;
            }
        }
        return n > 0 ? $"{(sum / n).ToString("0", CultureInfo.InvariantCulture)} (n={n})" : "-";
    }

    /// <summary>The learned eco-mode load ratio: the load in 15 minutes after eco-on over the load before it in the same cut.</summary>
    private sealed class EcoRatio
    {
        private readonly HistoryRow[] _rows;
        private readonly List<Cut> _cuts;
        private readonly int[] _cutOf;
        private readonly long?[] _ecoAt;   // per cut: the eco-on time inside it
        private readonly double?[] _ratio;

        public EcoRatio(HistoryRow[] rows, List<Cut> cuts, int[] cutOf, List<long> eco)
        {
            (_rows, _cuts, _cutOf) = (rows, cuts, cutOf);
            _ecoAt = [.. cuts.Select(k => eco.Where(e => e >= rows[k.Start].At - 60 && e <= rows[k.End].At).Select(e => (long?)e).FirstOrDefault())];
            _ratio = [.. cuts.Select((k, ci) =>
            {
                if (_ecoAt[ci] is not { } e) return null;
                var before = Enumerable.Range(k.Start, k.End - k.Start + 1).Where(i => rows[i].At < e).Select(i => Load(rows[i])).ToList();
                var after = Enumerable.Range(k.Start, k.End - k.Start + 1).Where(i => rows[i].At > e && rows[i].At <= e + 900).Select(i => Load(rows[i])).ToList();
                return before.Count > 0 && after.Count > 0 && before.Average() > 0 ? after.Average() / before.Average() : (double?)null;
            })];
        }

        public int CutsWithEco => _ecoAt.Count(e => e is not null);

        /// <summary>The median ratio over the cuts but <paramref name="held"/>; 1 without any.</summary>
        public double Ratio(int held)
        {
            var l = _ratio.Where((r, k) => k != held && r is not null).Select(r => r!.Value).Order().ToList();
            return l.Count > 0 ? Quantile(l, 0.5) : 1;
        }

        /// <summary>Eco mode not yet on at row <paramref name="i"/> of its cut, in a station where most other cuts saw it come on.</summary>
        public bool Pending(int i, int held)
        {
            int ci = _cutOf[i];
            if (ci < 0) return false;
            int others = _ecoAt.Where((e, k) => k != held && e is not null).Count();
            if (others * 2 < _cuts.Count - 1) return false;
            return _ecoAt[ci] is not { } e || _rows[i].At < e;
        }
    }

    /// <summary>The hour-of-day load profile from battery readings, UTC hours, relative to the mean, shrunk toward 1 where thin.</summary>
    private sealed class Seasonal(HistoryRow[] rows, List<Cut> cuts)
    {
        public Prof Profile(int held)
        {
            var sum = new double[24];
            var n = new int[24];
            double total = 0;
            int count = 0;
            for (int k = 0; k < cuts.Count; k++)
            {
                if (k == held) continue;
                for (int i = cuts[k].Start; i <= cuts[k].End; i++)
                {
                    int h = (int)(rows[i].At % 86400 / 3600);
                    double l = Load(rows[i]);
                    sum[h] += l;
                    n[h]++;
                    total += l;
                    count++;
                }
            }
            double mean = count > 0 ? total / count : 1;
            var rel = new double[24];
            for (int h = 0; h < 24; h++) rel[h] = mean > 0 && n[h] > 0 ? (sum[h] + 30 * mean) / (n[h] + 30) / mean : 1;
            return new Prof(rel);
        }

        public sealed class Prof(double[] rel)
        {
            public double At(long at) => rel[(int)(at % 86400 / 3600)];

            /// <summary>The mean relative load over the next <paramref name="minutes"/>.</summary>
            public double Mean(long at, int minutes)
            {
                double s = 0;
                for (int m = 0; m < minutes; m += 5) s += At(at + m * 60L);
                return s / Math.Max(1, (minutes + 4) / 5);
            }

            public override string ToString() => string.Join(" ", rel.Select((r, h) => $"{h}:{r.ToString("0.00", CultureInfo.InvariantCulture)}"));
        }
    }
}
