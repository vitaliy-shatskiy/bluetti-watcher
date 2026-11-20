using System.Globalization;
using System.Net;
using System.Text.Json;
using BluettiWatcher.Decision;

namespace BluettiWatcher.Io;

/// <summary>
/// Bluetooth first; when it does not answer, the cloud (design.md §4 Cloud fallback). Each reading carries its source.
/// <paramref name="cloudOn"/> is asked at each poll (the token file may appear while the service runs); without it the cloud is always on.
/// <paramref name="check"/> is the decision's own acceptance check (<c>""</c> = accepted): a Bluetooth reading it rejects as incomplete
/// (a model with no mode, a field that did not answer) is read again from the cloud, and the cloud's reading is used when it passes;
/// otherwise the Bluetooth reading goes on as it is, so the decision records the miss. <paramref name="log"/> names the answering
/// source once per change. <paramref name="cloudOnly"/>, asked at each poll as well (the reading source may change while the
/// service runs): true = Bluetooth is never opened, the cloud alone reads.
/// </summary>
internal sealed class FallbackReader(IStationReader bluetooth, IStationReader? cloud, Func<bool>? cloudOn = null, Func<Reading, string>? check = null, Action<string>? log = null,
    Func<bool>? cloudOnly = null) : IStationReader
{
    private string? _state = "Bluetooth answers";

    public async Task<Reading?> ReadAsync(CancellationToken ct)
    {
        bool cloudReady = cloud is not null && cloudOn?.Invoke() != false;
        if (cloudOnly?.Invoke() == true)
        {
            var only = cloudReady ? await cloud!.ReadAsync(ct) : null;
            State(only is null ? "no source answers (cloud only)" : "cloud answers (cloud only)");
            return only is null ? null : Cloud(only);
        }
        var r = await bluetooth.ReadAsync(ct);
        if (r is not null)
        {
            string why = check?.Invoke(r) ?? "";
            if (why.Length == 0 || !cloudReady)
            {
                State("Bluetooth answers");
                return r with { Source = "bluetooth" };
            }
            var again = await cloud!.ReadAsync(ct);
            if (again is not null && (check?.Invoke(again) ?? "").Length == 0)
            {
                State($"cloud answers: the Bluetooth reading is {why}");
                return Cloud(again);
            }
            State($"Bluetooth answers, though {why}: the cloud has no usable reading either");
            return r with { Source = "bluetooth" };
        }
        if (!cloudReady) return null;
        var c = await cloud!.ReadAsync(ct);
        State(c is null ? "no source answers" : "cloud answers: Bluetooth gave no reading");
        return c is null ? null : Cloud(c);
    }

    /// <summary>A cloud reading keeps the cloud history's mark (design.md §4 AC2b); any other is "cloud".</summary>
    private static Reading Cloud(Reading r) => r.Source == BluettiCloudReader.HistorySource ? r : r with { Source = "cloud" };

    private void State(string text)
    {
        if (string.Equals(text, _state, StringComparison.Ordinal)) return;
        _state = text;
        log?.Invoke($"reading source: {text}");
    }
}
/// <summary>The cloud token file: <c>{access_token, refresh_token, token_type, expires_in, created_at}</c>, created_at in unix seconds.</summary>
internal sealed record CloudToken(string Access, string Refresh, string TokenType, long ExpiresIn, long CreatedAt)
{
    // Int128: the file's two longs can be anything; their sum saturates at the clamp instead of overflowing.
    public DateTimeOffset ExpiresAt => DateTimeOffset.FromUnixTimeSeconds((long)Int128.Clamp((Int128)CreatedAt + ExpiresIn, 0, 253_402_300_799));

    /// <summary>Never the tokens: a record would print them.</summary>
    public override string ToString() => $"CloudToken {{ expires {ExpiresAt:yyyy-MM-dd'T'HH:mm:ss'Z'} }}";
}

/// <summary>
/// The BLUETTI cloud as the fallback reading (design.md §4 Cloud fallback), with the token of the official Home Assistant integration.
/// READ ONLY, FOREVER, as the Bluetooth path: the only calls are the device list (GET, for the serial), the live state
/// (getDeviceLastAlive, the official app's read), the token refresh (POST) and, through <see cref="CloudHistoryReader"/>'s
/// own allow-list, the history's power curve; no control, fulfillment or bind call exists here. No token ever reaches a log line: a failure names its kind only.
/// </summary>
internal sealed class BluettiCloudReader(StationConfig config, HttpMessageHandler handler, TimeProvider time, Action<string> log) : IStationReader, IDisposable
{
    // Not secrets: the public client values shipped in BLUETTI's official Home Assistant integration
    // (github.com/bluetti-official/bluetti-home-assistant, custom_components/bluetti/profile/application.yaml).
    // ClientSecret is base64("HomeAssistant"). Access comes only from the user's own token (cloud-token.json, never in the repo).
    private const string DevicesUrl = "https://gw.bluettipower.com/api/bluiotdata/ha/v2/devices";
    // The official app's live read: the device list answers every value 0 for some stations while they run (design.md §4 AC2b).
    private const string LastAliveUrl = "https://gw.bluettipower.com/api/bluiotdata/realtime/v1/getDeviceLastAlive";
    internal const string TokenUrl = "https://sso.bluettipower.com/oauth2/token";
    internal const string AppKey = "19974C15BCCBC91B21A8BA71BF5";
    internal const string AppVer = "1.0.5";
    internal const string ClientId = "HomeAssistant";
    internal const string ClientSecret = "SG9tZUFzc2lzdGFudA==";
    private const int Expired = 805;
    private static readonly TimeSpan RefreshAhead = TimeSpan.FromDays(3);
    private static readonly TimeSpan EarlyRefreshHold = TimeSpan.FromHours(1);

    private readonly HttpClient _http = new(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 1 << 20 };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset? _earlyRefreshFailedAt;
    /// <summary>A token pair the server issued that could not be written yet: used, and saved again each poll until it is.</summary>
    private CloudToken? _unsaved;
    /// <summary>The station's serial from the last device list: the history API's <c>deviceSn</c>, never logged.</summary>
    private string? _serial;
    private double? _staleSoc;
    private DateTimeOffset _staleSocSince;
    /// <summary>
    /// The refresh token the server refused (HTTP 400, 401 or 403: a dead grant, not a hiccup) and when; until a new sign-in
    /// or <see cref="RefusedHold"/> passes it is not sent again, nor a read with the access token that answered 805.
    /// Only compared, never logged.
    /// </summary>
    private volatile Refusal? _refused;
    /// <summary>The last read's outcome for <see cref="Status"/>; null = no read since the start or the last sign-in.</summary>
    private volatile Outcome? _outcome;
    internal static readonly TimeSpan RefusedHold = TimeSpan.FromHours(6);

    private sealed record Refusal(string Refresh, string? DeadAccess, int Http, DateTimeOffset At);
    private sealed record Outcome(bool Ok, string Why, DateTimeOffset At);

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The serial a device list gave before this start (station.db, <see cref="CloudBackfill.SerialKey"/>): the history
    /// session then needs no device-list read, which the cloud may leave unanswered. A device list read later replaces it.
    /// </summary>
    public string? KnownSerial
    {
        get => _serial;
        init => _serial = value is { Length: > 0 } ? value : null;
    }

    /// <summary>The service's reader: it starts with the serial saved for its own station type (<see cref="CloudBackfill.SavedSerial"/>).</summary>
    internal static BluettiCloudReader ForService(StationConfig config, HttpMessageHandler handler, TimeProvider time, Action<string> log, Storage.StationStore store,
        CloudHistoryReader? history = null) =>
        new(config, handler, time, log) { KnownSerial = CloudBackfill.SavedSerial(store, config.Type), History = history, Store = store };

    /// <summary>The history API the all-zero device list falls back to (design.md §4 AC2b); null = that answer is a miss.</summary>
    internal CloudHistoryReader? History { get; init; }
    /// <summary>Holds the daily history call count shared with <see cref="CloudBackfill"/>; null = no history call.</summary>
    internal Storage.StationStore? Store { get; init; }

    /// <summary>The history API refused the serial: the next history session reads the device list again.</summary>
    internal void ForgetSerial() => _serial = null;

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }

    public async Task<Reading?> ReadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var (r, why) = await ReadOnceAsync(ct);
            // A miss for no live state is said once per outage (ReadOnceAsync), not each poll.
            if (r is null && !why.StartsWith(NoLiveState, StringComparison.Ordinal)) log($"cloud: no reading - {why}");
            _outcome = new Outcome(r is not null, why, time.GetUtcNow());
            return r;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// The cloud's state for the page and /api/health (design.md §4 Cloud state): off (the source never calls it) ·
    /// signed-out (no token file) · sign-in-needed (the server refused the refresh token) · error (the last read failed) ·
    /// ok. The detail names the reason, never a token.
    /// </summary>
    public CloudStatus Status()
    {
        if (config.Source == ReadingSource.Bluetooth) return new CloudStatus("off", "the reading source is Bluetooth only", null);
        if (config.Source == ReadingSource.Auto && config.CloudEnabled == false) return new CloudStatus("off", "cloudEnabled is false", null);
        if (!File.Exists(config.CloudTokenFile)) return new CloudStatus("signed-out", "no token: sign in to the BLUETTI cloud", null);
        if (_refused is { } refused)
            return new CloudStatus("sign-in-needed", Invariant($"the token refresh was refused (HTTP {refused.Http}): sign in again"), refused.At);
        if (_outcome is { } o) return o.Ok ? new CloudStatus("ok", "the last read answered", o.At) : new CloudStatus("error", o.Why, o.At);
        var (t, why) = LoadToken();
        return t is null
            ? new CloudStatus("error", why, null)
            : new CloudStatus("ok", Invariant($"signed in, not read yet; the access token {(t.ExpiresAt > time.GetUtcNow() ? "is valid until" : "expired at")} {t.ExpiresAt:yyyy-MM-dd'T'HH:mm:ss'Z'}"), null);
    }

    /// <summary>A token pair older than this is refreshed by <see cref="KeepAliveAsync"/> even when no read needs the cloud.</summary>
    internal static readonly TimeSpan KeepAliveEvery = TimeSpan.FromDays(1);
    private DateTimeOffset? _keepAliveFailedAt;

    /// <summary>
    /// design.md §4 AC11: under the auto source a healthy Bluetooth never calls the cloud, so without this the pair is never
    /// refreshed and lapses unnoticed. When the source allows the cloud and a token exists, a pair issued
    /// <see cref="KeepAliveEvery"/> ago (or expiring within <see cref="RefreshAhead"/>) is refreshed; a refused refresh
    /// turns the state to sign-in-needed and is held as any refusal; another failure waits an hour.
    /// </summary>
    internal async Task KeepAliveAsync(CancellationToken ct)
    {
        if (!config.CloudOn) return;
        await _gate.WaitAsync(ct);
        try
        {
            var (token, _) = CurrentToken();
            if (token is null || HeldRefusal(token) is not null) return;
            var now = time.GetUtcNow();
            bool due = token.ExpiresAt - now < RefreshAhead || now - DateTimeOffset.FromUnixTimeSeconds(token.CreatedAt) >= KeepAliveEvery;
            if (!due || (_keepAliveFailedAt is { } failedAt && now - failedAt < EarlyRefreshHold)) return;
            var (fresh, why) = await RefreshAsync(token, ct);
            if (fresh is not null)
            {
                _keepAliveFailedAt = null;
                log("cloud: the token was refreshed (daily keep-alive)");
                return;
            }
            _keepAliveFailedAt = now;
            _outcome = new Outcome(false, $"the daily token refresh failed - {why}", now);
            log($"cloud: the daily token refresh failed - {why}");
        }
        finally { _gate.Release(); }
    }

    /// <summary>Runs <see cref="KeepAliveAsync"/> once an hour until <paramref name="ct"/> ends; one failure never stops it.</summary>
    internal async Task RunKeepAliveAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await KeepAliveAsync(ct); }
            catch (Exception e) when (e is not OperationCanceledException) { log($"cloud: keep-alive failed: {e.GetType().Name}"); }
            try { await Task.Delay(TimeSpan.FromHours(1), time, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// A new sign-in (<paramref name="token"/>) or a sign-out (null): the token file is written or deleted under the reader's
    /// lock, so no read in flight writes an older refreshed pair over it; what the reader held for the old token is dropped.
    /// Throws IOException / UnauthorizedAccessException when the file cannot be written or deleted.
    /// </summary>
    internal async Task ReplaceTokenAsync(CloudToken? token, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            string path = config.CloudTokenFile;
            if (token is null) File.Delete(path);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                Save(path, token);
            }
            _unsaved = null;
            _refused = null;
            _outcome = null;
            _earlyRefreshFailedAt = null;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// design.md §4 AC2: the live state from getDeviceLastAlive (the device list is read only while no serial is known);
    /// no live state in it falls to the history's newest point (AC2b), said once per outage.
    /// </summary>
    private async Task<(Reading? Reading, string Why)> ReadOnceAsync(CancellationToken ct)
    {
        if (_serial is null)
        {
            var (_, listWhy) = await WithTokenAsync(DevicesAsync, ct);
            if (_serial is null) return (null, listWhy.Length > 0 ? listWhy : $"no {config.Type} serial in the cloud answer");
        }
        string serial = _serial;
        var (answer, why) = await WithTokenAsync((access, c) => LastAliveAsync(access, serial, c), ct);
        if (why.StartsWith(NoLiveState, StringComparison.Ordinal))
        {
            if (!_noLiveState) log($"cloud: {why}");
            _noLiveState = true;
            (answer, why) = await NewestHistoryAsync(why, ct);
        }
        else if (answer is not null)
        {
            _noLiveState = false;
            _historyNote = null;
        }
        return answer is null ? (null, why) : Stale(answer, time.GetUtcNow()) is { } stale ? (null, stale) : (answer, "");
    }

    /// <summary>A live state that is no reading (every field null, no SOC, too old): a plain miss unless the history replaces it (design.md §4 AC2b).</summary>
    internal const string NoLiveState = "no live state from getDeviceLastAlive";
    /// <summary>The source of a reading taken from the cloud history's newest point.</summary>
    internal const string HistorySource = "cloud-history";
    /// <summary>At most one history call this often for the live reading; between calls the last newest point is used.</summary>
    internal static readonly TimeSpan HistoryEvery = TimeSpan.FromMinutes(5);
    private bool _noLiveState;
    private string? _historyNote;
    private HistoryPoint? _historyPoint;
    private DateTimeOffset? _historyCalledAt;

    /// <summary>
    /// design.md §4 AC2b: with getDeviceLastAlive answering no live state (<paramref name="liveWhy"/>), the history's newest
    /// point of today is the reading while it is no older than cloudHistoryMaxAgeMin; older, missing, or no call allowed (the
    /// 5-minute spacing keeps the last answer, the daily cap is shared with the back-fill) is a miss. Its outcome is logged when it changes.
    /// </summary>
    private async Task<(Reading? Reading, string Why)> NewestHistoryAsync(string liveWhy, CancellationToken ct)
    {
        int maxAge = config.CloudHistoryMaxAgeMin;
        if (History is null || Store is null || maxAge <= 0 || config.CloudHistory == false || _serial is not { } serial) return (null, liveWhy);
        (Reading? Reading, string Why) HistoryMiss(string why)
        {
            HistoryNote(why);
            return (null, $"{liveWhy}; {why}");
        }
        var now = time.GetUtcNow();
        var zone = time.LocalTimeZone;
        if (_historyCalledAt is not { } called || now - called >= HistoryEvery)
        {
            var (token, why) = CurrentToken();
            if (token is null) return HistoryMiss(why);
            if (!CloudBackfill.TryCountCall(Store, now, zone, CloudBackfill.DefaultCallsPerDay))
                return HistoryMiss(Invariant($"the daily cap of {CloudBackfill.DefaultCallsPerDay} history calls is reached"));
            _historyCalledAt = now;
            var day = await History.PowerDayAsync(token.Access, serial, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime), zone, ct);
            _historyPoint = day.Points.MaxBy(p => p.At);
            if (day.Why.Length > 0) return HistoryMiss($"the history answered: {day.Why}");
        }
        if (_historyPoint is not { } point) return HistoryMiss("the history has no point today");
        if (now - point.At > TimeSpan.FromMinutes(maxAge)) return HistoryMiss(Invariant($"the history's newest point is older than {maxAge} min"));
        HistoryNote("its newest point is the reading");
        return (new Reading(point.Soc, point.InAc, point.OutAc, DerivedMode(point.InAc, point.OutAc, point.OutAc > 0), point.InDc, point.OutDc, Source: HistorySource), "");
    }

    private void HistoryNote(string text)
    {
        if (string.Equals(text, _historyNote, StringComparison.Ordinal)) return;
        _historyNote = text;
        log($"cloud history (live): {text}");
    }

    /// <summary>
    /// design.md §4 AC9: the cloud may serve a frozen cache with the device online. Off mains under a real load the SOC must move:
    /// unchanged for staleAfterMin it is a miss. The clock of an SOC value starts when the cloud first gave it.
    /// </summary>
    private string? Stale(Reading r, DateTimeOffset now)
    {
        if (r.Soc != _staleSoc) { _staleSoc = r.Soc; _staleSocSince = now; }
        if (config.StaleAfterMin <= 0 || r.InAc != 0 || !(r.OutAc >= config.StaleLoadW)) return null;
        var held = now - _staleSocSince;
        return held >= TimeSpan.FromMinutes(config.StaleAfterMin)
            ? Invariant($"cloud reading stale: SOC {r.Soc} % unchanged for {(int)held.TotalMinutes} min at {r.OutAc} W out, no AC input")
            : null;
    }

    private (CloudToken? Token, string Why) CurrentToken()
    {
        if (_unsaved is not { } kept) return LoadToken();
        try
        {
            Save(config.CloudTokenFile, kept);
            _unsaved = null;
            log("cloud: the refreshed token is written now");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"cloud: the refreshed token still could not be written ({e.GetType().Name}); kept in memory");
        }
        return (kept, "");
    }

    /// <summary>
    /// The token and the station's serial for <see cref="CloudHistoryReader"/>: the same token, refreshed early as a read
    /// refreshes it; the serial is the one the last device list gave (one device-list read when none has yet).
    /// <paramref name="refused"/>: the access token the history API answered msgCode 805 to; refreshed now unless a read
    /// already replaced it. Holds the reader's lock for these calls: a cloud read waits for them (design.md §4a).
    /// </summary>
    internal async Task<(string? Access, string? Serial, string Why)> HistorySessionAsync(string? refused, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_serial is null)
            {
                var (_, why) = await WithTokenAsync(DevicesAsync, ct);
                if (_serial is null) return (null, null, why.Length > 0 ? why : $"no {config.Type} serial in the cloud answer");
            }
            if (refused is not null)
            {
                var (current, currentWhy) = CurrentToken();
                if (current is null) return (null, null, currentWhy);
                if (current.Access != refused) return (current.Access, _serial, "");
                var (renewed, refreshWhy) = await RefreshAsync(current, ct);
                return renewed is null ? (null, null, $"msgCode {Expired} (token expired) and {refreshWhy}") : (renewed.Access, _serial, "");
            }
            var (token, _, tokenWhy) = await FreshTokenAsync(ct);
            return token is null ? (null, null, tokenWhy) : (token.Access, _serial, "");
        }
        finally { _gate.Release(); }
    }

    /// <summary>The current token, refreshed first when it expires within <see cref="RefreshAhead"/>.</summary>
    private async Task<(CloudToken? Token, bool Refreshed, string Why)> FreshTokenAsync(CancellationToken ct)
    {
        var (token, why) = CurrentToken();
        if (token is null) return (null, false, why);
        var now = time.GetUtcNow();
        if (token.ExpiresAt - now >= RefreshAhead || (_earlyRefreshFailedAt is { } failedAt && now - failedAt < EarlyRefreshHold)) return (token, false, "");
        var (fresh, failed) = await RefreshAsync(token, ct);
        if (fresh is not null) return (fresh, true, "");
        // The token may still be good: read with it, and try the refresh again in an hour, not every poll.
        _earlyRefreshFailedAt = now;
        log($"cloud: the early token refresh failed - {failed}; reading with the current token");
        return (token, true, "");
    }

    /// <summary>One cloud call with the current token: refreshed first when due, and once more after a refresh when it answers 805.</summary>
    private async Task<(Reading? Reading, string Why)> WithTokenAsync(Func<string, CancellationToken, Task<(int? MsgCode, Reading? Reading, string Why)>> call, CancellationToken ct)
    {
        var (token, refreshed, why) = await FreshTokenAsync(ct);
        if (token is null) return (null, why);
        // The access token already answered 805 and its refresh was refused: no call can succeed until a new sign-in.
        if (HeldRefusal(token) is { } held && held.DeadAccess == token.Access) return (null, SignInNeeded(held));
        var answer = await call(token.Access, ct);
        if (answer.MsgCode != Expired) return (answer.Reading, answer.Why);
        if (refreshed) return (null, $"msgCode {Expired} (token expired) after a token refresh");
        var (renewed, refreshWhy) = await RefreshAsync(token, ct);
        if (renewed is null && _refused is { } refused && refused.Refresh == token.Refresh) _refused = refused with { DeadAccess = token.Access };
        if (renewed is null) return (null, $"msgCode {Expired} (token expired) and {refreshWhy}");
        answer = await call(renewed.Access, ct);
        return answer.MsgCode == Expired ? (null, $"msgCode {Expired} (token expired) after a token refresh") : (answer.Reading, answer.Why);
    }

    /// <summary>
    /// Both calls only read: with no answer at all (a timeout, a failed connection) one is sent once more; an answer never is.
    /// <paramref name="name"/> names the call in the reason; <paramref name="send"/> builds a fresh request each time.
    /// </summary>
    private async Task<(HttpStatusCode Status, string? Body, string Why)> RetriedAsync(string name, Func<Task<(HttpStatusCode Status, string? Body, string Why)>> send)
    {
        var (status, body, why) = await send();
        if (body is not null) return (status, body, "");
        string first = why;
        (status, body, why) = await send();
        if (body is null) return (0, null, why == first ? $"{name}: {first}, again on retry" : $"{name}: {first}, then {why} on retry");
        log($"cloud: {name} answered on the second try (first: {first})");
        return (status, body, "");
    }

    /// <summary>The device list, read only for the station's serial (design.md §4 AC2): its values are no reading.</summary>
    private async Task<(int? MsgCode, Reading? Reading, string Why)> DevicesAsync(string access, CancellationToken ct)
    {
        var (status, body, why) = await RetriedAsync("devices", () => DevicesOnceAsync(access, ct));
        if (body is null) return (null, null, why);
        if (status != HttpStatusCode.OK) return (null, null, Invariant($"devices answered HTTP {(int)status}"));
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null, "the devices answer is not an object");
            if (MsgCode(root) is not { } code) return (null, null, "the devices answer has no msgCode");
            if (code != 0) return (code, null, Invariant($"msgCode {code}"));
            if (Serial(root, config.Type) is not { } sn) return (code, null, $"no {config.Type} in the cloud account");
            _serial = sn;
            return (code, null, "");
        }
        catch (JsonException) { return (null, null, "the devices answer is not JSON"); }
    }

    /// <summary>The <c>sn</c> of the first device whose model starts with <paramref name="type"/>, online or not; null when there is none.</summary>
    internal static string? Serial(JsonElement root, string type)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        foreach (var d in data.EnumerateArray())
            if (d.ValueKind == JsonValueKind.Object && Str(d, "model") is { } model && model.StartsWith(type, StringComparison.OrdinalIgnoreCase))
                return Str(d, "sn") is { Length: > 0 } sn ? sn : null;
        return null;
    }

    private async Task<(HttpStatusCode Status, string? Body, string Why)> DevicesOnceAsync(string access, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(DevicesUrl));
        Headers(req, access);
        return await SendAsync(req, ct);
    }

    private static void Headers(HttpRequestMessage req, string access)
    {
        req.Headers.TryAddWithoutValidation("Authorization", access);   // raw, no "Bearer"
        req.Headers.Add("x-os", "open");
        req.Headers.Add("x-app-key", AppKey);
        req.Headers.Add("x-app-ver", AppVer);
    }

    /// <summary>The live state of the station <paramref name="serial"/> (the app's own read, design.md §4 AC2).</summary>
    private async Task<(int? MsgCode, Reading? Reading, string Why)> LastAliveAsync(string access, string serial, CancellationToken ct)
    {
        var (status, body, why) = await RetriedAsync("live state", () => LastAliveOnceAsync(access, serial, ct));
        if (body is null) return (null, null, why);
        if (status != HttpStatusCode.OK) return (null, null, Invariant($"the live state answered HTTP {(int)status}"));
        try
        {
            using var doc = JsonDocument.Parse(body);
            return ParseLastAlive(doc.RootElement, time.GetUtcNow(), time.LocalTimeZone, config.CloudLiveMaxAgeSec);
        }
        catch (JsonException) { return (null, null, "the live state answer is not JSON"); }
    }

    private async Task<(HttpStatusCode Status, string? Body, string Why)> LastAliveOnceAsync(string access, string serial, CancellationToken ct)
    {
        var json = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(json))
        {
            w.WriteStartObject();
            w.WriteString("deviceSn", serial);
            w.WriteEndObject();
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(LastAliveUrl))
        {
            Content = new StringContent(System.Text.Encoding.UTF8.GetString(json.WrittenSpan), System.Text.Encoding.UTF8, "application/json"),
        };
        Headers(req, access);
        return await SendAsync(req, ct);
    }

    /// <summary>
    /// design.md §4 AC2: getDeviceLastAlive's flat <c>data</c>, numbers as strings (or numbers, or null). A reading only
    /// when <c>allFieldIsNull</c> is false, <c>batterySoc</c> is a number and <c>timestamp</c> (the station's local
    /// "yyyy-MM-dd HH:mm:ss", read in <paramref name="zone"/>) is at most <paramref name="maxAgeSec"/> off
    /// <paramref name="now"/> either way; else <see cref="NoLiveState"/> with the reason. Powers: grid in = AC in, AC out,
    /// PV in = DC in, DC out; the mode is derived (AC2a) with <c>acSwitch</c> as the AC output's state.
    /// </summary>
    internal static (int? MsgCode, Reading? Reading, string Why) ParseLastAlive(JsonElement root, DateTimeOffset now, TimeZoneInfo zone, int maxAgeSec)
    {
        if (root.ValueKind != JsonValueKind.Object) return (null, null, "the live state answer is not an object");
        if (MsgCode(root) is not { } code) return (null, null, "the live state answer has no msgCode");
        if (code != 0) return (code, null, Invariant($"msgCode {code}"));
        if (!root.TryGetProperty("data", out var d) || d.ValueKind != JsonValueKind.Object) return (code, null, $"{NoLiveState} (no data)");
        if (!d.TryGetProperty("allFieldIsNull", out var allNull) || allNull.ValueKind != JsonValueKind.False)
            return (code, null, $"{NoLiveState} ({(allNull.ValueKind == JsonValueKind.True ? "every field null" : "no allFieldIsNull false")})");
        double? Num(string k) => Str(d, k) is { } v && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;
        if (Num("batterySoc") is not { } soc) return (code, null, $"{NoLiveState} (no numeric batterySoc)");
        if (LocalInstant(Str(d, "timestamp"), now, zone) is not { } at) return (code, null, $"{NoLiveState} (no readable timestamp)");
        double age = (now - at).TotalSeconds;
        if (maxAgeSec <= 0 || Math.Abs(age) > maxAgeSec)
            return (code, null, Invariant($"{NoLiveState} (timestamp {age:0} s off the clock, cloudLiveMaxAgeSec {maxAgeSec})"));
        double? inAc = Num("powerGridIn"), outAc = Num("powerAcOut");
        // A running station is never at 0 % with nothing flowing: the cloud holds no live state for it.
        if (soc == 0 && (inAc ?? 0) == 0 && (outAc ?? 0) == 0) return (code, null, $"{NoLiveState} (all values zero)");
        return (code, new Reading(soc, inAc, outAc, DerivedMode(inAc, outAc, Num("acSwitch") == 1), Num("powerPvIn"), Num("powerDcOut"), Source: "cloud"), "");
    }

    /// <summary>
    /// A local "yyyy-MM-dd HH:mm:ss" in <paramref name="zone"/>; in the hour a clock change repeats, the reading of the two
    /// nearer <paramref name="now"/>; a time the zone skips, or unreadable text, is null.
    /// </summary>
    private static DateTimeOffset? LocalInstant(string? text, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (!DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local) || zone.IsInvalidTime(local)) return null;
        if (!zone.IsAmbiguousTime(local)) return new DateTimeOffset(local, zone.GetUtcOffset(local));
        return zone.GetAmbiguousTimeOffsets(local).Select(o => new DateTimeOffset(local, o)).MinBy(t => (t - now).Duration());
    }

    private static int? MsgCode(JsonElement root) =>
        root.TryGetProperty("msgCode", out var mc) && Long(mc) is { } l && l is >= int.MinValue and <= int.MaxValue ? (int)l : null;

    /// <summary>The cloud has no output mode: mains in = mains on; no AC in while the AC output is on and feeds a load = the inverter; else unknown.</summary>
    private static string? DerivedMode(double? inAc, double? outAc, bool acOn) =>
        inAc > 0 ? "CLOUD_MAINS" : inAc == 0 && acOn && outAc > 0 ? "INVERTER_OUTPUT" : null;

    /// <summary>A refused refresh token still held back: no call goes out with it until a new sign-in or the hold passes.</summary>
    private Refusal? HeldRefusal(CloudToken token)
    {
        if (_refused is not { } r) return null;
        if (r.Refresh == token.Refresh && time.GetUtcNow() - r.At < RefusedHold) return r;
        if (r.Refresh != token.Refresh) _refused = null;   // a sign-in outside the page (--cloud-login) replaced the token
        return null;
    }

    private static string SignInNeeded(Refusal r) =>
        Invariant($"sign-in needed: the token refresh was refused (HTTP {r.Http}); sign in again on the page or with --cloud-login (no retry for {RefusedHold.TotalHours:0} h)");

    private async Task<(CloudToken? Token, string Why)> RefreshAsync(CloudToken token, CancellationToken ct)
    {
        if (token.Refresh.Length == 0) return (null, "the token file has no refresh token");
        if (HeldRefusal(token) is { } held) return (null, SignInNeeded(held));
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(TokenUrl))
        {
            Content = new FormUrlEncodedContent([
                new("grant_type", "refresh_token"), new("refresh_token", token.Refresh),
                new("client_id", ClientId), new("client_secret", ClientSecret)]),
        };
        var (status, body, why) = await SendAsync(req, ct);
        if (body is null) return (null, $"the token refresh {why}");
        if (status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // A dead grant answers the same every time: held back instead of sent at every poll.
            var refused = new Refusal(token.Refresh, null, (int)status, time.GetUtcNow());
            _refused = refused;
            return (null, SignInNeeded(refused));
        }
        if (status != HttpStatusCode.OK) return (null, Invariant($"the token refresh answered HTTP {(int)status}"));
        CloudToken? fresh;
        try
        {
            using var doc = JsonDocument.Parse(body);
            fresh = Renewed(doc.RootElement, token, time.GetUtcNow().ToUnixTimeSeconds());
        }
        catch (JsonException) { return (null, "the token refresh answer is not JSON"); }
        if (fresh is null) return (null, "the token refresh answer has no access token");
        // The server may have retired the old pair: the new one is used whether or not it reached the disk.
        _unsaved = null;
        try { Save(config.CloudTokenFile, fresh); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _unsaved = fresh;
            log($"cloud: the refreshed token could not be written ({e.GetType().Name}); kept in memory, written again next poll");
        }
        return (fresh, "");
    }

    /// <summary>A standard OAuth2 answer (or one wrapped in <c>data</c>); a missing refresh token, type or lifetime keeps the old one.</summary>
    internal static CloudToken? Renewed(JsonElement root, CloudToken old, long now)
    {
        var t = root.ValueKind == JsonValueKind.Object && !root.TryGetProperty("access_token", out _) && root.TryGetProperty("data", out var data) ? data : root;
        if (t.ValueKind != JsonValueKind.Object || Str(t, "access_token") is not { Length: > 0 } access) return null;
        return new CloudToken(access, Str(t, "refresh_token") is { Length: > 0 } r ? r : old.Refresh, Str(t, "token_type") ?? old.TokenType,
            t.TryGetProperty("expires_in", out var e) && Long(e) is long exp && exp > 0 ? exp : old.ExpiresIn, now);
    }

    private (CloudToken? Token, string Why) LoadToken()
    {
        string text;
        try { text = File.ReadAllText(config.CloudTokenFile); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return (null, "no token file"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (null, $"the token file is unreadable ({e.GetType().Name})"); }
        try
        {
            using var doc = JsonDocument.Parse(text);
            var t = doc.RootElement;
            if (t.ValueKind != JsonValueKind.Object || Str(t, "access_token") is not { Length: > 0 } access) return (null, "the token file has no access token");
            long Field(string k) => t.TryGetProperty(k, out var v) ? Long(v) ?? 0 : 0;
            return (new CloudToken(access, Str(t, "refresh_token") ?? "", Str(t, "token_type") ?? "bearer", Field("expires_in"), Field("created_at")), "");
        }
        catch (JsonException) { return (null, $"the token file is not JSON (torn or zeroed?): {config.CloudTokenFile} needs a fresh token from a new login"); }
    }

    /// <summary>Temp file beside it, flushed to the disk, then a replace: a reader (or a hard power loss) never leaves half a token file.</summary>
    internal static void Save(string path, CloudToken t)
    {
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using (var w = new Utf8JsonWriter(fs))
            {
                w.WriteStartObject();
                w.WriteString("access_token", t.Access);
                w.WriteString("refresh_token", t.Refresh);
                w.WriteString("token_type", t.TokenType);
                w.WriteNumber("expires_in", t.ExpiresIn);
                w.WriteNumber("created_at", t.CreatedAt);
                w.WriteEndObject();
            }
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Body null = no answer; the reason names the failure's kind only, never its message (it may echo the request).</summary>
    private async Task<(HttpStatusCode Status, string? Body, string Why)> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var cap = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cap.CancelAfter(Timeout);
        try
        {
            using var resp = await _http.SendAsync(req, cap.Token);
            return (resp.StatusCode, await resp.Content.ReadAsStringAsync(cap.Token), "");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (0, null, Invariant($"timed out after {Timeout.TotalSeconds:0.#} s"));
        }
        catch (HttpRequestException e)
        {
            return (0, null, $"HTTP failed ({e.HttpRequestError})");
        }
    }

    private static string? Str(JsonElement o, string k) => o.TryGetProperty(k, out var v) ? v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetRawText(),
        _ => null,
    } : null;

    private static long? Long(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number when v.TryGetInt64(out var n) => n,
        JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
        _ => null,
    };

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
