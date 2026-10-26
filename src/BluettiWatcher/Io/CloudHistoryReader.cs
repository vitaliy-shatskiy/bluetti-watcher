using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using BluettiWatcher.Storage;

namespace BluettiWatcher.Io;

/// <summary>One 5-minute point of the cloud's power curve: SOC %, powers in W (design.md §4a).</summary>
internal sealed record HistoryPoint(DateTimeOffset At, double Soc, double? InAc, double? InDc, double? OutAc, double? OutDc);

/// <summary>One day's answer. Hourly = the cloud keeps only hourly points for the day: none are given back.</summary>
internal sealed record HistoryDay(int? MsgCode, List<HistoryPoint> Points, bool Hourly, string Why);

/// <summary>
/// The station's own history from the BLUETTI cloud statistics API (design.md §4a), with the token and headers of
/// <see cref="BluettiCloudReader"/> on its own connection pool (<see cref="NewHandler"/>). READ ONLY, FOREVER: a request is
/// built only for the two statistics paths below, with only their query fields; any other path throws before a request
/// exists, and no redirect is followed.
/// </summary>
internal sealed class CloudHistoryReader(HttpMessageHandler handler) : IDisposable
{
    /// <summary>The service's handler for this reader: a redirect is never followed, so no request reaches a path off the allow-list.</summary>
    internal static SocketsHttpHandler NewHandler() => new() { PooledConnectionLifetime = TimeSpan.FromMinutes(10), AllowAutoRedirect = false };

    private const string Base = "https://gw.bluettipower.com";
    // The whole allow-list. Not configurable: no other path of the BLUETTI API may ever be called from here.
    private const string PowerPath = "/api/bluiotdata/dashboard/v2/getDevicePowerStatistics";
    private const string EnergyPath = "/api/bluiotdata/dashboard/v2/getDeviceEnergyStatistics";

    private readonly HttpClient _http = new(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 1 << 20 };

    /// <summary>The cap on each request.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    public void Dispose() => _http.Dispose();

    /// <summary>One day's power curve of the station <paramref name="serial"/>, the day's labels read in <paramref name="zone"/>.</summary>
    public async Task<HistoryDay> PowerDayAsync(string access, string serial, DateOnly day, TimeZoneInfo zone, CancellationToken ct)
    {
        var (status, body, why) = await PostAsync(PowerPath, access, serial, day, 0, ct);
        if (body is null) return new HistoryDay(null, [], false, why);
        if ((int)status is >= 300 and < 400) return new HistoryDay(null, [], false, string.Create(CultureInfo.InvariantCulture, $"answered a redirect (HTTP {(int)status}), not followed"));
        if (status != HttpStatusCode.OK) return new HistoryDay(null, [], false, string.Create(CultureInfo.InvariantCulture, $"answered HTTP {(int)status}"));
        try
        {
            using var doc = JsonDocument.Parse(body);
            return ParsePower(doc.RootElement, zone);
        }
        catch (JsonException) { return new HistoryDay(null, [], false, "the answer is not JSON"); }
    }

    /// <summary>
    /// The only way out of this class. <paramref name="path"/> off the allow-list throws before anything is built; the
    /// body holds the read query fields of that path and nothing else (<paramref name="energyFlag"/>: 0 day, 1 month, 2 year).
    /// </summary>
    internal async Task<(HttpStatusCode Status, string? Body, string Why)> PostAsync(string path, string access, string serial, DateOnly day, int energyFlag, CancellationToken ct)
    {
        if (path is not (PowerPath or EnergyPath)) throw new InvalidOperationException("cloud history: a path off the read-only allow-list is never called");
        if (energyFlag is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(energyFlag));
        using var json = new MemoryStream();
        using (var w = new Utf8JsonWriter(json))
        {
            w.WriteStartObject();
            w.WriteString("deviceSn", serial);
            w.WriteString("startDate", day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            w.WriteBoolean("remoteFlag", false);
            w.WriteBoolean("installerFlag", false);
            if (path == PowerPath)
            {
                w.WriteString("typeFlag", "pv");
                w.WriteNumber("queryType", 1);
            }
            else w.WriteNumber("flag", energyFlag);
            w.WriteEndObject();
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(Base + path))
        {
            Content = new StringContent(Encoding.UTF8.GetString(json.ToArray()), Encoding.UTF8, "application/json"),
        };
        req.Headers.TryAddWithoutValidation("Authorization", access);   // raw, no "Bearer", as the device list
        req.Headers.Add("x-os", "open");
        req.Headers.Add("x-app-key", BluettiCloudReader.AppKey);
        req.Headers.Add("x-app-ver", BluettiCloudReader.AppVer);
        using var cap = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cap.CancelAfter(Timeout);
        try
        {
            using var resp = await _http.SendAsync(req, cap.Token);
            return (resp.StatusCode, await resp.Content.ReadAsStringAsync(cap.Token), "");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (0, null, string.Create(CultureInfo.InvariantCulture, $"timed out after {Timeout.TotalSeconds:0.#} s"));
        }
        catch (HttpRequestException e)
        {
            return (0, null, $"HTTP failed ({e.HttpRequestError})");   // the kind only: the message may echo the request
        }
    }

    /// <summary>
    /// <c>data.chart_x</c> local labels "yyyy-MM-dd HH:mm" and <c>data.chart_y</c> series <c>{title, data[]}</c>: soc,
    /// grid (AC in), ac (AC out), pv (DC in), dc (DC out). A point needs a SOC above 0 (a later point of today, or an
    /// hour the station sent nothing, has none). A day whose first two adjacent readable labels are not 5 minutes apart is
    /// hourly: no points.
    /// On the fall-back day a repeated label is the summer hour the first time and the winter hour the second; a label
    /// in the spring-forward hour does not exist and is dropped.
    /// </summary>
    internal static HistoryDay ParsePower(JsonElement root, TimeZoneInfo zone)
    {
        if (root.ValueKind != JsonValueKind.Object) return new HistoryDay(null, [], false, "the answer is not an object");
        int? code = root.TryGetProperty("msgCode", out var mc) && mc.ValueKind == JsonValueKind.Number && mc.TryGetInt32(out int c) ? c : null;
        if (code is null) return new HistoryDay(null, [], false, "the answer has no msgCode");
        if (code != 0) return new HistoryDay(code, [], false, string.Create(CultureInfo.InvariantCulture, $"msgCode {code}"));
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("chart_x", out var xs) || xs.ValueKind != JsonValueKind.Array
            || !data.TryGetProperty("chart_y", out var ys) || ys.ValueKind != JsonValueKind.Array)
            return new HistoryDay(code, [], false, "the answer has no chart");
        var labels = new List<DateTime?>();
        foreach (var x in xs.EnumerateArray())
            labels.Add(x.ValueKind == JsonValueKind.String && DateTime.TryParseExact(x.GetString(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null);
        for (int i = 0; i + 1 < labels.Count; i++)
        {
            if (labels[i] is not { } a || labels[i + 1] is not { } b) continue;
            if (b - a != TimeSpan.FromMinutes(5)) return new HistoryDay(code, [], true, "");
            break;
        }
        var series = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var y in ys.EnumerateArray())
            if (y.ValueKind == JsonValueKind.Object && y.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
                && y.TryGetProperty("data", out var values) && values.ValueKind == JsonValueKind.Array)
                series.TryAdd(title.GetString()!, values);
        double? Value(string name, int i)
        {
            if (!series.TryGetValue(name, out var values) || i >= values.GetArrayLength()) return null;
            var v = values[i];
            double n = 0;
            bool ok = v.ValueKind == JsonValueKind.Number ? v.TryGetDouble(out n)
                : v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out n);
            return ok && double.IsFinite(n) ? n : null;
        }
        var points = new List<HistoryPoint>(labels.Count);
        var seen = new Dictionary<DateTime, int>();
        for (int i = 0; i < labels.Count; i++)
        {
            if (labels[i] is not { } local) continue;
            int occurrence = seen[local] = seen.GetValueOrDefault(local, -1) + 1;
            TimeSpan offset;
            if (zone.IsInvalidTime(local)) continue;
            if (zone.IsAmbiguousTime(local))
            {
                var both = zone.GetAmbiguousTimeOffsets(local).OrderByDescending(o => o).ToArray();
                if (occurrence >= both.Length) continue;
                offset = both[occurrence];
            }
            else if (occurrence > 0) continue;
            else offset = zone.GetUtcOffset(local);
            if (Value("soc", i) is not { } soc || soc <= 0) continue;
            points.Add(new HistoryPoint(new DateTimeOffset(local, offset), soc, Value("grid", i), Value("pv", i), Value("ac", i), Value("dc", i)));
        }
        return new HistoryDay(code, points, false, "");
    }
}

/// <summary>
/// Fills the gaps of station.db from the cloud's own 5-minute history (design.md §4a): gaps of at least
/// <see cref="GapMinutes"/> without an accepted reading in the last <see cref="WindowDays"/> days, each local day asked
/// once, at most <see cref="CallsPerRun"/> calls a run and <see cref="CallsPerDay"/> a day. Runs after start, once a day
/// and after a gap in the live readings ends. Never in the poll path: a failed call is a log line and nothing else.
/// </summary>
internal sealed class CloudBackfill(StationConfig config, CloudHistoryReader reader, Func<string?, CancellationToken, Task<(string? Access, string? Serial, string Why)>> session,
    StationStore store, TimeProvider time, Action<string> log, Action? forgetSerial = null) : IDisposable
{
    public const int GapMinutes = 10;
    public const int WindowDays = 30;
    /// <summary>The newest minutes are left to the live poll and the cloud's upload delay.</summary>
    public const int LagMinutes = 10;
    internal const string CallsKey = "cloudHistoryCalls";
    internal const string DoneKey = "cloudHistoryDone";
    /// <summary>"type;serial" of the last session: the next start's history needs no device-list read (<see cref="SavedSerial"/>).</summary>
    internal const string SerialKey = "cloudHistorySerial";

    /// <summary>The saved serial when it was saved for <paramref name="type"/>; another type's (or an untyped) one is never used.</summary>
    internal static string? SavedSerial(StationStore store, string type)
    {
        string[] parts = (store.GetMeta(SerialKey) ?? "").Split(';');
        return parts.Length == 2 && string.Equals(parts[0], type, StringComparison.OrdinalIgnoreCase) && parts[1].Length > 0 ? parts[1] : null;
    }
    /// <summary>The cloud's msgCode for an expired token.</summary>
    private const int ExpiredToken = 805;
    private static readonly TimeSpan DoneAfterDayEnd = TimeSpan.FromHours(1);
    private static readonly TimeSpan FirstRunAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RunEvery = TimeSpan.FromDays(1);

    private readonly SemaphoreSlim _gapEnded = new(0, 1);
    private DateTimeOffset? _lastAccepted;

    public int CallsPerRun { get; init; } = 4;
    public int CallsPerDay { get; init; } = 40;

    public void Dispose() => _gapEnded.Dispose();

    /// <summary>The service loop's view after each poll: an accepted reading after a gap wakes the back-fill.</summary>
    public void AfterPoll(DateTimeOffset? lastAccepted)
    {
        if (lastAccepted is not { } now) return;
        bool gapEnded = _lastAccepted is { } before && now - before >= TimeSpan.FromMinutes(GapMinutes);
        _lastAccepted = now;
        if (gapEnded && _gapEnded.CurrentCount == 0) _gapEnded.Release();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(FirstRunAfter, time, ct);
            while (!ct.IsCancellationRequested)
            {
                try { await RunOnceAsync(ct); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log($"cloud history: run failed: {e.GetType().Name}");
                }
                await _gapEnded.WaitAsync(RunEvery, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // stopping
        }
    }

    /// <summary>One run. Returns the calls made.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (!config.CloudHistoryOn) return 0;
        var now = time.GetUtcNow();
        var zone = time.LocalTimeZone;
        long to = now.AddMinutes(-LagMinutes).ToUnixTimeSeconds();
        long from = Math.Max(now.AddDays(-WindowDays).ToUnixTimeSeconds(), store.FirstAt() ?? to);
        var gaps = store.Gaps(from, to, GapMinutes);
        if (gaps.Count == 0) return 0;
        var windowStart = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(from), zone).DateTime);
        var done = (store.GetMeta(DoneKey) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateOnly.MinValue)
            .Where(d => d >= windowStart).ToHashSet();
        var days = new SortedSet<DateOnly>(Comparer<DateOnly>.Create((a, b) => b.CompareTo(a)));   // newest first
        foreach (var (s, e) in gaps)
            for (var d = LocalDay(s, zone); d <= LocalDay(e - 1, zone); d = d.AddDays(1))
                if (!done.Contains(d)) days.Add(d);
        if (days.Count == 0) return 0;

        string today = TimeZoneInfo.ConvertTime(now, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string[] calls = (store.GetMeta(CallsKey) ?? "").Split(' ');
        int callsToday = calls.Length == 2 && calls[0] == today && int.TryParse(calls[1], NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;
        string? access = null, serial = null;
        int made = 0;
        bool refreshed = false;
        bool CapReached()
        {
            if (callsToday >= CallsPerDay) { log(Invariant($"cloud history: the daily cap of {CallsPerDay} calls is reached; the other days wait for tomorrow")); return true; }
            return made >= CallsPerRun;
        }
        Task<HistoryDay> CallAsync(DateOnly day)
        {
            callsToday++;
            made++;
            store.SetMeta(CallsKey, Invariant($"{today} {callsToday}"));   // counted before the call: a crash mid-call still counts
            return reader.PowerDayAsync(access!, serial!, day, zone, ct);
        }
        foreach (var day in days)
        {
            if (CapReached()) break;
            if (access is null)
            {
                var (a, sn, why) = await session(null, ct);
                if (a is null || sn is null) { log($"cloud history: not run - {why}"); return made; }
                (access, serial) = (a, sn);
                string saved = $"{config.Type};{sn}";
                if (store.GetMeta(SerialKey) != saved) store.SetMeta(SerialKey, saved);
            }
            var answer = await CallAsync(day);
            string label = day.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
            if (answer.MsgCode == ExpiredToken && !refreshed)
            {
                // The token was refused: refreshed once a run, then the same day asked once more.
                log(Invariant($"cloud history: {label} - {answer.Why} (calls today {callsToday}/{CallsPerDay}); the token is refreshed and the day asked again"));
                refreshed = true;
                var (a, sn, why) = await session(access, ct);
                if (a is null || sn is null) { log($"cloud history: not run - {why}"); return made; }
                (access, serial) = (a, sn);
                if (CapReached()) break;
                answer = await CallAsync(day);
            }
            if (answer.Why.Length > 0)
            {
                log(Invariant($"cloud history: {label} - {answer.Why} (calls today {callsToday}/{CallsPerDay})"));
                if (answer.MsgCode is null) continue;
                if (answer.MsgCode is not (0 or ExpiredToken))
                {
                    // Not the token: the serial may be another station's. The next run reads the device list again.
                    store.SetMeta(SerialKey, "");
                    forgetSerial?.Invoke();
                    log("cloud history: the API refused the station's serial; the device list is read again next run");
                }
                break;   // the API refused (still an expired token, ...): the next run asks again
            }
            int added = 0;
            if (answer.Hourly) log(Invariant($"cloud history: {label} - the cloud keeps only hourly points for this day: skipped (calls today {callsToday}/{CallsPerDay})"));
            else
            {
                var inGaps = answer.Points.Where(p => p.At.ToUnixTimeSeconds() is var t && t >= from && t < to && gaps.Any(g => t >= g.From && t < g.To));
                added = store.AddCloudHistory(inGaps);
                log(Invariant($"cloud history: {label} - {answer.Points.Count} points, {added} rows added (calls today {callsToday}/{CallsPerDay})"));
            }
            if (now >= DayEnd(day, zone) + DoneAfterDayEnd)
            {
                done.Add(day);
                store.SetMeta(DoneKey, string.Join(',', done.Order().Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
            }
        }
        return made;
    }

    private static DateOnly LocalDay(long unix, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(unix), zone).DateTime);

    /// <summary>The local midnight after <paramref name="day"/>; a midnight the clock skips counts from the hour after it.</summary>
    private static DateTimeOffset DayEnd(DateOnly day, TimeZoneInfo zone)
    {
        var midnight = day.AddDays(1).ToDateTime(TimeOnly.MinValue);
        if (zone.IsInvalidTime(midnight)) midnight = midnight.AddHours(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnight, zone), TimeSpan.Zero);
    }

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
