using System.Net;
using System.Text;
using System.Text.Json;
using BluettiWatcher.Decision;
using BluettiWatcher.Http;
using BluettiWatcher.Io;
using BluettiWatcher.Storage;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

/// <summary>design.md §4 Reading source, Cloud state, Cloud sign-in: the source setting, the refused refresh, the page's sign-in.</summary>
public sealed class ReadingSourceTests : IDisposable
{
    private const string Access = "ACCESS-SRC-4e1b";
    private const string Refresh = "REFRESH-SRC-8d2c";
    private const string NewAccess = "ACCESS-NEW-0a9f";
    private const string NewRefresh = "REFRESH-NEW-77e3";
    private const string Devices = "https://gw.bluettipower.com/api/bluiotdata/ha/v2/devices";
    private const string TokenUrl = "https://sso.bluettipower.com/oauth2/token";
    private const string LastAlive = "https://gw.bluettipower.com/api/bluiotdata/realtime/v1/getDeviceLastAlive";
    private const int Port = 18940;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-src-test-" + Guid.NewGuid().ToString("N"));
    private readonly CannedHandler _http = new();
    private readonly List<string> _log = [];
    private readonly ManualClock _clock = new(T0);
    private readonly StationConfig _cfg;
    private BluettiCloudReader? _cloud;

    public ReadingSourceTests()
    {
        Directory.CreateDirectory(_dir);
        _cfg = new StationConfig { DataDir = _dir, Mac = "AA:BB:CC:DD:EE:FF" };
    }

    public void Dispose()
    {
        try
        {
            lock (_log)
                foreach (var line in _log)
                    foreach (var secret in new[] { Access, Refresh, NewAccess, NewRefresh })
                        Assert.DoesNotContain(secret, line, StringComparison.Ordinal);
            foreach (var c in _http.Calls)
                Assert.True((c.Method == HttpMethod.Get && c.Url == Devices) || (c.Method == HttpMethod.Post && c.Url is TokenUrl or LastAlive), $"{c.Method} {c.Url} is not allowed");
        }
        finally
        {
            _cloud?.Dispose();
            _http.Dispose();
            Directory.Delete(_dir, recursive: true);
        }
    }

    private void Log(string line) { lock (_log) _log.Add(line); }

    private BluettiCloudReader Cloud => _cloud ??= new(_cfg, _http, _clock, Log) { KnownSerial = "SERIAL-1" };

    private string TokenPath => Path.Combine(_dir, "cloud-token.json");

    private void Token(string access = Access, string refresh = Refresh) =>
        File.WriteAllText(TokenPath, $$"""{"access_token":"{{access}}","refresh_token":"{{refresh}}","token_type":"bearer","expires_in":2678400,"created_at":{{T0.ToUnixTimeSeconds()}}}""");

    /// <summary>A getDeviceLastAlive answer stamped with the test clock's local time.</summary>
    private string Answer(string soc = "71", string grid = "0", string acLoad = "169", string ctrlAc = "1", int msgCode = 0) =>
        $$$"""{"msgCode":{{{msgCode}}},"message":"OK","data":{"allFieldIsNull":false,"timestamp":"{{{LocalNow()}}}","batterySoc":"{{{soc}}}","powerGridIn":"{{{grid}}}","acSwitch":"{{{ctrlAc}}}","powerAcOut":"{{{acLoad}}}","powerPvIn":null,"powerDcOut":null}}""";

    private string LocalNow() => TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), _clock.Zone).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    // --- the setting -------------------------------------------------------------------------------------------------

    [Fact]
    public void Source_NotSet_IsAutoAndCloudFollowsTheTokenFile()
    {
        Assert.Equal(ReadingSource.Auto, _cfg.Source);
        Assert.False(_cfg.CloudOn);
        Token();
        Assert.True(_cfg.CloudOn);
        Assert.True(_cfg.CloudHistoryOn);
        Assert.False(_cfg.CloudOnly);
    }

    [Fact]
    public void Source_Bluetooth_NeverAllowsTheCloudOrItsHistory()
    {
        Token();
        _cfg.ReadingSource = ReadingSource.Bluetooth;
        _cfg.CloudEnabled = true;
        Assert.False(_cfg.CloudOn);
        Assert.False(_cfg.CloudHistoryOn);
        Assert.False(_cfg.CloudOnly);
    }

    [Fact]
    public void Source_Cloud_NeedsNoStationAddressAndNoBluetoothModel()
    {
        var cfg = new StationConfig { DataDir = _dir, Type = "NOT-A-BLE-MODEL", ReadingSource = ReadingSource.Cloud };
        Assert.True(cfg.CloudOn);
        Assert.True(cfg.CloudOnly);
        Assert.Null(cfg.PollProblem);
        Assert.Null(cfg.ConfigProblem);
    }

    [Fact]
    public void Source_CloudEnabledFalseAndNoKey_IsBluetooth()
    {
        _cfg.CloudEnabled = false;
        Assert.Equal(ReadingSource.Bluetooth, _cfg.Source);
        _cfg.ReadingSource = ReadingSource.Cloud;
        Assert.Equal(ReadingSource.Cloud, _cfg.Source);   // the new key wins over the old one
    }

    [Fact]
    public void Source_PageChoice_WinsOverTheConfigKey()
    {
        _cfg.ReadingSource = ReadingSource.Cloud;
        _cfg.PageSource = ReadingSource.Bluetooth;
        Assert.Equal(ReadingSource.Bluetooth, _cfg.Source);
        _cfg.PageSource = null;
        Assert.Equal(ReadingSource.Cloud, _cfg.Source);
    }

    [Theory]
    [InlineData("auto", "Auto")]
    [InlineData("bluetooth", "Bluetooth")]
    [InlineData("cloud", "Cloud")]
    public void Source_ConfigJsonKey_IsRead(string name, string expected)
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), $$"""{ "readingSource": "{{name}}" }""");
        Assert.Equal(expected, StationConfig.Load(["--data-dir", _dir]).Source.ToString());
    }

    [Fact]
    public void SourceFile_RoundTripsAndAnUnreadableOneIsNamedAndIgnored()
    {
        Assert.Null(ReadingSourceFile.Read(_dir, Log));
        ReadingSourceFile.Write(_dir, ReadingSource.Cloud);
        Assert.Equal(ReadingSource.Cloud, ReadingSourceFile.Read(_dir, Log));
        Assert.False(File.Exists(ReadingSourceFile.PathOf(_dir) + ".tmp"));
        File.WriteAllText(ReadingSourceFile.PathOf(_dir), """{"readingSource":"satellite"}""");
        Assert.Null(ReadingSourceFile.Read(_dir, Log));
        File.WriteAllText(ReadingSourceFile.PathOf(_dir), "{torn");
        Assert.Null(ReadingSourceFile.Read(_dir, Log));
        Assert.Equal(2, _log.Count(l => l.StartsWith("CONFIG - reading-source.json", StringComparison.Ordinal)));
    }

    // --- the reader the service polls --------------------------------------------------------------------------------

    [Fact]
    public async Task Fallback_CloudOnly_NeverOpensBluetooth()
    {
        var bt = new QueueReader { Default = new Reading(50, 0, 100, "INVERTER_OUTPUT") };
        var cloud = new QueueReader { Default = new Reading(70, 0, 100, "INVERTER_OUTPUT") };
        var r = await new FallbackReader(bt, cloud, () => true, cloudOnly: () => true).ReadAsync(CancellationToken.None);
        Assert.Equal(("cloud", 70.0), (r?.Source, r?.Soc));
        Assert.Equal((0, 1), (bt.Reads, cloud.Reads));
    }

    [Fact]
    public async Task Fallback_BluetoothSource_NeverCallsTheCloudOnAMiss()
    {
        Token();
        _cfg.PageSource = ReadingSource.Bluetooth;
        var bt = new QueueReader();
        var cloud = new QueueReader { Default = new Reading(70, 0, 100, "INVERTER_OUTPUT") };
        var reader = new FallbackReader(bt, cloud, () => _cfg.CloudOn, cloudOnly: () => _cfg.CloudOnly);
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
        Assert.Equal(0, cloud.Reads);
        _cfg.PageSource = ReadingSource.Cloud;   // the page's change applies at the next poll
        Assert.Equal("cloud", (await reader.ReadAsync(CancellationToken.None))?.Source);
        Assert.Equal((1, 1), (bt.Reads, cloud.Reads));
    }

    // --- the cloud's state, the refused refresh ------------------------------------------------------------------------

    [Fact]
    public async Task Status_FollowsTheSourceTheTokenAndTheLastRead()
    {
        Assert.Equal("signed-out", Cloud.Status().State);   // auto, no token: CloudOn is false yet the source allows it
        _cfg.CloudEnabled = true;
        Assert.Equal("signed-out", Cloud.Status().State);
        _cfg.PageSource = ReadingSource.Bluetooth;
        Assert.Equal("off", Cloud.Status().State);
        _cfg.PageSource = ReadingSource.Cloud;
        Token();
        var fresh = Cloud.Status();
        Assert.Equal("ok", fresh.State);
        Assert.Contains("not read yet", fresh.Detail, StringComparison.Ordinal);
        _http.Json("{}", HttpStatusCode.InternalServerError);
        Assert.Null(await Cloud.ReadAsync(CancellationToken.None));
        Assert.Equal(("error", "the live state answered HTTP 500"), (Cloud.Status().State, Cloud.Status().Detail));
        _http.Json(Answer());
        Assert.NotNull(await Cloud.ReadAsync(CancellationToken.None));
        Assert.Equal("ok", Cloud.Status().State);
    }

    [Fact]
    public void Status_AutoWithoutTokenAndCloudDisabled_IsOffOrSignedOut()
    {
        using var on = new BluettiCloudReader(new StationConfig { DataDir = _dir, ReadingSource = ReadingSource.Auto, CloudEnabled = true }, _http, _clock, Log);
        using var off = new BluettiCloudReader(new StationConfig { DataDir = _dir, ReadingSource = ReadingSource.Auto, CloudEnabled = false }, _http, _clock, Log);
        Assert.Equal(("signed-out", "off"), (on.Status().State, off.Status().State));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task RefusedRefresh_After805_IsNotSentAgainAndNeedsASignIn(HttpStatusCode refused)
    {
        _cfg.PageSource = ReadingSource.Cloud;
        Token();
        _http.Json(Answer(msgCode: 805));
        _http.Json("""{"error":"invalid_grant"}""", refused);
        Assert.Null(await Cloud.ReadAsync(CancellationToken.None));
        Assert.Equal(2, _http.Calls.Count);
        var status = Cloud.Status();
        Assert.Equal("sign-in-needed", status.State);
        Assert.Contains($"HTTP {(int)refused}", status.Detail, StringComparison.Ordinal);

        // Every poll after it: no live-state call with the dead access token, no refresh with the refused one.
        _clock.Now = T0.AddMinutes(1);
        Assert.Null(await Cloud.ReadAsync(CancellationToken.None));
        _clock.Now = T0.AddHours(5);
        Assert.Null(await Cloud.ReadAsync(CancellationToken.None));
        Assert.Equal(2, _http.Calls.Count);
        Assert.Contains("sign-in needed", _log[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusedRefresh_IsTriedAgainAfterTheHold()
    {
        _cfg.PageSource = ReadingSource.Cloud;
        Token();
        _http.Json(Answer(msgCode: 805));
        _http.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        Assert.Null(await Cloud.ReadAsync(CancellationToken.None));
        _clock.Now = T0 + BluettiCloudReader.RefusedHold + TimeSpan.FromMinutes(1);
        _http.Json(Answer(msgCode: 805));
        _http.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        Assert.Null(await Cloud.ReadAsync(CancellationToken.None));
        Assert.Equal(4, _http.Calls.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task FailedRefresh_NotARefusal_IsTriedAgainNextPoll(HttpStatusCode status)
    {
        _cfg.PageSource = ReadingSource.Cloud;
        Token();
        _http.Json(Answer(msgCode: 805));
        _http.Json("{}", status);
        Assert.Null(await Cloud.ReadAsync(CancellationToken.None));
        Assert.Equal("error", Cloud.Status().State);
        _http.Json(Answer(msgCode: 805));
        _http.Json("{}", status);
        Assert.Null(await Cloud.ReadAsync(CancellationToken.None));
        Assert.Equal(4, _http.Calls.Count);
    }

    [Fact]
    public async Task RefusedRefresh_ANewTokenOnDisk_EndsTheHold()
    {
        _cfg.PageSource = ReadingSource.Cloud;
        Token();
        _http.Json(Answer(msgCode: 805));
        _http.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        Assert.Null(await Cloud.ReadAsync(CancellationToken.None));
        Token(NewAccess, NewRefresh);   // --cloud-login while the service runs
        _http.Json(Answer());
        Assert.NotNull(await Cloud.ReadAsync(CancellationToken.None));
        Assert.Equal(NewAccess, _http.Calls[^1].Auth);
        Assert.Equal("ok", Cloud.Status().State);
    }

    // --- the keep-alive: a token never lapses while Bluetooth answers ---------------------------------------------------

    private void Refreshed() =>
        _http.Json($$"""{"access_token":"{{NewAccess}}","refresh_token":"{{NewRefresh}}","token_type":"bearer","expires_in":2678400}""");

    [Fact]
    public async Task KeepAlive_BluetoothHealthyForDays_RefreshesTheTokenOnceADay()
    {
        Token();   // 31 days, created at T0; the auto source with Bluetooth answering never reads the cloud
        await Cloud.KeepAliveAsync(CancellationToken.None);
        Assert.Empty(_http.Calls);   // fresh: nothing to do

        _clock.Now = T0.AddDays(1).AddMinutes(1);
        Refreshed();
        await Cloud.KeepAliveAsync(CancellationToken.None);
        Assert.Equal(HttpMethod.Post, Assert.Single(_http.Calls).Method);
        using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(TokenPath, TestContext.Current.CancellationToken)))
            Assert.Equal(NewRefresh, doc.RootElement.GetProperty("refresh_token").GetString());

        _clock.Now = T0.AddDays(1).AddHours(5);
        await Cloud.KeepAliveAsync(CancellationToken.None);
        Assert.Single(_http.Calls);   // the new pair is fresh again: no second refresh that day
    }

    [Fact]
    public async Task KeepAlive_RefusedRefresh_TurnsSignInNeededAndIsNotRepeated()
    {
        Token();
        _clock.Now = T0.AddDays(2);
        _http.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
        await Cloud.KeepAliveAsync(CancellationToken.None);
        Assert.Equal("sign-in-needed", Cloud.Status().State);
        _clock.Now = T0.AddDays(2).AddHours(1);
        await Cloud.KeepAliveAsync(CancellationToken.None);
        Assert.Single(_http.Calls);
    }

    [Fact]
    public async Task KeepAlive_SourceBluetoothOrNoToken_CallsNothing()
    {
        _clock.Now = T0.AddDays(5);
        await Cloud.KeepAliveAsync(CancellationToken.None);   // no token file
        Token();
        _cfg.PageSource = ReadingSource.Bluetooth;
        await Cloud.KeepAliveAsync(CancellationToken.None);
        Assert.Empty(_http.Calls);
    }

    // --- task: the cloud answer rejected as "incomplete (no valid output mode)" -----------------------------------------

    /// <summary>
    /// Every reading rejected that way since 30.10.2026 carried SOC 0 %, 0 W in and 0 W out: a device state with nothing
    /// live in it. The decision must not take SOC 0 % as the battery (a stop and a hibernation would follow): such an answer
    /// is no live state, a plain miss with no reading at all (design.md §4 AC2b). A real state on the inverter or on mains derives its mode.
    /// </summary>
    [Theory]
    [InlineData("0", "0", "0", "1", false)]     // the answer the cloud gave 306 times: no reading, never SOC 0 %
    [InlineData("0", "0", "0", "0", false)]
    [InlineData("64", "0", "169", "1", true)]   // mains off, AC on, a load: the inverter
    [InlineData("64", "250", "169", "1", true)] // on mains
    [InlineData("64", "250", "0", "0", true)]   // on mains, AC output off
    public async Task CloudAnswer_ModeDerivation_DecidesAcceptance(string soc, string grid, string acLoad, string ctrlAc, bool accepted)
    {
        _cfg.PageSource = ReadingSource.Cloud;
        Token();
        _http.Json(Answer(soc, grid, acLoad, ctrlAc));
        var r = await Cloud.ReadAsync(CancellationToken.None);
        if (!accepted)
        {
            Assert.Null(r);
            return;
        }
        Assert.NotNull(r);
        Assert.Equal("", StationDecision.Check(r, new StationState(), T0, _cfg));
    }

    /// <summary>design.md §4 AC2b: the all-zero answer is stored as a plain miss: a row with no SOC, never one with SOC 0.</summary>
    [Fact]
    public async Task Poll_AllZeroCloudAnswer_StoresAPlainMissWithNoSocZeroRow()
    {
        _cfg.PageSource = ReadingSource.Cloud;
        Token();
        _http.Json(Answer("0", "0", "0", "1"));
        string db = Path.Combine(_dir, "station.db");
        using (var store = new StationStore(db))
        using (var service = new StationService(_cfg, new FallbackReader(new QueueReader(), Cloud, cloudOnly: () => true), new FakeActions(), new FakeHibernator(true),
            new FixedFlag(FlagView.Normal), store, _clock, Log))
            await service.PollAsync(CancellationToken.None);
        await using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False");
        await c.OpenAsync(TestContext.Current.CancellationToken);
        await using var q = c.CreateCommand();
        q.CommandText = "SELECT status, soc FROM reading";
        await using var rows = await q.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await rows.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal((long)ReadingStatus.Missed, rows.GetInt64(0));
        Assert.True(await rows.IsDBNullAsync(1, TestContext.Current.CancellationToken));
        Assert.False(await rows.ReadAsync(TestContext.Current.CancellationToken));
    }

    // --- the page's controls -------------------------------------------------------------------------------------------

    private static HttpRequest Req(string method, string path, string body = "", bool guarded = true)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = $"127.0.0.1:{Port}" };
        if (guarded) headers["X-Station"] = "1";
        return new HttpRequest(method, path, new Dictionary<string, string>(), headers, Encoding.UTF8.GetBytes(body));
    }

    private sealed class World : IDisposable
    {
        public World(ReadingSourceTests t, TimeSpan? wait = null)
        {
            Store = new StationStore(Path.Combine(t._dir, "station.db"));
            Service = new StationService(t._cfg, new QueueReader(), new FakeActions(), new FakeHibernator(true), new FixedFlag(FlagView.Normal), Store, t._clock, t.Log);
            Account = new CloudAccount(t._cfg, t.Cloud, t._http, t._clock, t.Log, CancellationToken.None) { Wait = wait ?? TimeSpan.FromSeconds(20) };
            Api = new StationApi(t._cfg, Service, Store, t._clock, T0, cloud: Account);
        }

        public StationStore Store { get; }
        public StationService Service { get; }
        public CloudAccount Account { get; }
        public StationApi Api { get; }

        public void Dispose()
        {
            Account.Dispose();
            Service.Dispose();
            Store.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    private static async Task<(int Status, string Body)> Send(StationApi api, HttpRequest r)
    {
        var resp = await api.HandleAsync(r, CancellationToken.None);
        return (resp.Status, Encoding.UTF8.GetString(resp.Body));
    }

    [Fact]
    public async Task Api_SourceChoice_IsGuardedSavedAndAppliedAtOnce()
    {
        using var w = new World(this);
        Assert.Equal(403, (await Send(w.Api, Req("POST", "/api/reading-source", """{"readingSource":"cloud"}""", guarded: false))).Status);
        Assert.Equal(ReadingSource.Auto, _cfg.Source);
        Assert.Equal(400, (await Send(w.Api, Req("POST", "/api/reading-source", """{"readingSource":"radio"}"""))).Status);
        Assert.Equal(405, (await Send(w.Api, Req("GET", "/api/reading-source"))).Status);

        var (status, body) = await Send(w.Api, Req("POST", "/api/reading-source", """{"readingSource":"bluetooth"}"""));
        Assert.Equal(200, status);
        var root = JsonElement.Parse(body);
        Assert.Equal(("bluetooth", "page", "off"), (root.GetProperty("readingSource").GetString(), root.GetProperty("readingSourceFrom").GetString(), root.GetProperty("cloud").GetProperty("state").GetString()));
        Assert.Equal(ReadingSource.Bluetooth, _cfg.Source);
        Assert.Equal(ReadingSource.Bluetooth, ReadingSourceFile.Read(_dir, Log));   // survives a restart

        var health = JsonElement.Parse((await Send(w.Api, Req("GET", "/api/health"))).Body);
        Assert.Equal("bluetooth", health.GetProperty("readingSource").GetString());
        Assert.Equal("off", health.GetProperty("cloud").GetProperty("state").GetString());
    }

    // Bluetooth on a model it cannot read would leave nothing polled (no readings, no battery protection): refused.
    [Fact]
    public async Task Api_SourceThatLeavesNothingToPoll_IsRefusedAndTheSourceStays()
    {
        _cfg.Type = "NOT-A-BLE-MODEL";
        Token();
        using var w = new World(this);
        Assert.Null(_cfg.PollProblem);   // auto with a token: the cloud alone reads it

        var (status, body) = await Send(w.Api, Req("POST", "/api/reading-source", """{"readingSource":"bluetooth"}"""));
        Assert.Equal(409, status);
        Assert.Contains("NOT-A-BLE-MODEL", body, StringComparison.Ordinal);
        Assert.Equal(ReadingSource.Auto, _cfg.Source);
        Assert.Null(_cfg.PageSource);
        Assert.False(File.Exists(ReadingSourceFile.PathOf(_dir)));
        Assert.Null(_cfg.PollProblem);
    }

    [Fact]
    public async Task Api_UseConfig_DeletesThePageChoiceAndTheConfigKeyApplies()
    {
        _cfg.ReadingSource = ReadingSource.Cloud;
        using var w = new World(this);
        Assert.Equal(200, (await Send(w.Api, Req("POST", "/api/reading-source", """{"readingSource":"bluetooth"}"""))).Status);
        Assert.Equal(403, (await Send(w.Api, Req("POST", "/api/reading-source", """{"readingSource":"config"}""", guarded: false))).Status);
        Assert.True(File.Exists(ReadingSourceFile.PathOf(_dir)));

        var (status, body) = await Send(w.Api, Req("POST", "/api/reading-source", """{"readingSource":"config"}"""));
        Assert.Equal(200, status);
        var root = JsonElement.Parse(body);
        Assert.Equal(("cloud", "config"), (root.GetProperty("readingSource").GetString(), root.GetProperty("readingSourceFrom").GetString()));
        Assert.False(File.Exists(ReadingSourceFile.PathOf(_dir)));
        Assert.Equal(ReadingSource.Cloud, _cfg.Source);
    }

    [Fact]
    public async Task Api_SignOut_DeletesTheTokenAndIsGuarded()
    {
        Token();
        using var w = new World(this);
        Assert.Equal(403, (await Send(w.Api, Req("POST", "/api/cloud/sign-out", guarded: false))).Status);
        Assert.True(File.Exists(TokenPath));
        var (status, body) = await Send(w.Api, Req("POST", "/api/cloud/sign-out"));
        Assert.Equal(200, status);
        Assert.False(File.Exists(TokenPath));
        Assert.Equal("signed-out", JsonElement.Parse(body).GetProperty("cloud").GetProperty("state").GetString());
        Assert.DoesNotContain(Access, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_SignIn_ReturnsTheAuthorizeAddressAndSavesTheTokenThroughTheReader()
    {
        using var w = new World(this);
        Assert.Equal(403, (await Send(w.Api, Req("POST", "/api/cloud/sign-in", guarded: false))).Status);
        _http.Json($$"""{ "access_token": "{{NewAccess}}", "refresh_token": "{{NewRefresh}}", "token_type": "bearer", "expires_in": 2592000 }""");

        var (status, body) = await Send(w.Api, Req("POST", "/api/cloud/sign-in"));
        Assert.Equal(202, status);
        var root = JsonElement.Parse(body);
        var url = new Uri(root.GetProperty("authorizeUrl").GetString()!);
        Assert.StartsWith("https://sso.bluettipower.com/oauth2/grant?", url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("waiting", root.GetProperty("signIn").GetProperty("state").GetString());
        var q = url.Query.TrimStart('?').Split('&').Select(kv => kv.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        var redirect = new Uri(q["redirect_uri"]);
        Assert.Equal("127.0.0.1", redirect.Host);
        Assert.NotEqual(Port, redirect.Port);

        // A second press while it waits: the same address, no second catcher.
        Assert.Equal(url.AbsoluteUri, JsonElement.Parse((await Send(w.Api, Req("POST", "/api/cloud/sign-in"))).Body).GetProperty("authorizeUrl").GetString());

        using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
        {
            using var forged = await client.GetAsync(new Uri($"{redirect}?code=X&state=forged"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
            using var back = await client.GetAsync(new Uri($"{redirect}?code=CODE-7&state={q["state"]}"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        }
        var signIn = await Poll(w.Api, s => s != "waiting");
        Assert.Equal("signed-in", signIn.GetProperty("signIn").GetProperty("state").GetString());
        Assert.Equal("ok", signIn.GetProperty("cloud").GetProperty("state").GetString());
        Assert.DoesNotContain(NewAccess, signIn.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(NewRefresh, signIn.GetRawText(), StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(TokenPath, TestContext.Current.CancellationToken));
        Assert.Equal(NewRefresh, doc.RootElement.GetProperty("refresh_token").GetString());
        Assert.Contains("CODE-7", _http.Calls.Single().Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_SignInRefusedInTheBrowser_IsFailedAndWritesNothing()
    {
        using var w = new World(this);
        var url = new Uri(JsonElement.Parse((await Send(w.Api, Req("POST", "/api/cloud/sign-in"))).Body).GetProperty("authorizeUrl").GetString()!);
        var q = url.Query.TrimStart('?').Split('&').Select(kv => kv.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
        using (await client.GetAsync(new Uri($"{q["redirect_uri"]}?error=access_denied&state={q["state"]}"), TestContext.Current.CancellationToken)) { }
        var state = await Poll(w.Api, s => s != "waiting");
        Assert.Equal("failed", state.GetProperty("signIn").GetProperty("state").GetString());
        Assert.Contains("access_denied", state.GetProperty("signIn").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.False(File.Exists(TokenPath));
        Assert.Empty(_http.Calls);
    }

    /// <summary>GET /api/cloud until the sign-in state passes <paramref name="done"/>; fails after 10 s (an event the test awaits, bounded).</summary>
    private static async Task<JsonElement> Poll(StationApi api, Func<string, bool> done)
    {
        for (int i = 0; i < 200; i++)
        {
            var root = JsonElement.Parse((await Send(api, Req("GET", "/api/cloud"))).Body);
            if (done(root.GetProperty("signIn").GetProperty("state").GetString()!)) return root;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        Assert.Fail("the sign-in did not end within 10 s");
        return default;
    }
}
