using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using BluettiWatcher.Decision;
using BluettiWatcher.Io;
using BluettiWatcher.Storage;
using Microsoft.Data.Sqlite;

namespace BluettiWatcher.Tests;

/// <summary>Counts every request and answers none: a test that must send nothing asserts <see cref="Calls"/> is 0.</summary>
internal sealed class NoCallHandler : HttpMessageHandler
{
    public int Calls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        throw new InvalidOperationException("no request may be sent");
    }
}

/// <summary>Answers each statistics request from <c>reply(body)</c> and records the requests.</summary>
internal sealed class DayHandler(Func<string, string> reply) : HttpMessageHandler
{
    public List<(HttpMethod Method, string Url, HttpRequestMessage Request, string Body)> Calls { get; } = [];
    public Exception? Fail { get; set; }
    /// <summary>A redirect answer: this status with a Location header, no body.</summary>
    public HttpStatusCode? Redirect { get; set; }
    /// <summary>The answer for a request with this Authorization header; else <c>reply(body)</c>.</summary>
    public Dictionary<string, string> ByAccess { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add((request.Method, request.RequestUri!.ToString(), request, body));
        if (Fail is not null) throw Fail;
        if (Redirect is { } status)
        {
            var moved = new HttpResponseMessage(status);
            moved.Headers.Location = new Uri("https://gw.bluettipower.com/api/bluiotdata/ha/v1/fulfillment");
            return moved;
        }
        string answer = request.Headers.TryGetValues("Authorization", out var a) && ByAccess.TryGetValue(a.Single(), out var canned) ? canned : reply(body);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
    }
}

public sealed class CloudHistoryTests : IDisposable
{
    private const string PowerUrl = "https://gw.bluettipower.com/api/bluiotdata/dashboard/v2/getDevicePowerStatistics";
    private const string EnergyPath = "/api/bluiotdata/dashboard/v2/getDeviceEnergyStatistics";
    private static readonly TimeZoneInfo Plus3 = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(3), "t", "t");
    private static readonly TimeZoneInfo Kyiv = TimeZoneInfo.FindSystemTimeZoneById("FLE Standard Time");
    private static readonly DateOnly Day = new(2026, 10, 18);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-history-test-" + Guid.NewGuid().ToString("N"));
    private readonly StationStore _store;
    private readonly List<string> _log = [];
    private readonly List<IDisposable> _owned = [];
    private readonly ManualClock _clock = new(At("2026-10-20 12:00"));

    public CloudHistoryTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new StationStore(Path.Combine(_dir, "station.db"));
        File.WriteAllText(Path.Combine(_dir, "cloud-token.json"),
            """{"access_token":"ACCESS-1","refresh_token":"REFRESH-1","token_type":"bearer","expires_in":999999999,"created_at":1790000000}""");
    }

    public void Dispose()
    {
        foreach (var d in _owned) d.Dispose();
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static DateTimeOffset At(string local) =>
        new(DateTime.ParseExact(local, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), TimeSpan.FromHours(3));

    /// <summary>A synthetic answer: one label per <paramref name="labels"/>, soc from <paramref name="soc"/>, fixed powers.</summary>
    private static string Answer(IReadOnlyList<string> labels, Func<int, string> soc, int msgCode = 0)
    {
        string Series(string title, Func<int, string> v) => $$"""{"title":"{{title}}","data":[{{string.Join(',', labels.Select((_, i) => v(i)))}}]}""";
        string x = string.Join(',', labels.Select(l => $"\"{l}\""));
        string ys = string.Join(',', Series("soc", soc), Series("grid", _ => "\"230.5\""), Series("ac", i => (100 + i).ToString(CultureInfo.InvariantCulture)),
            Series("pv", _ => "0"), Series("dc", _ => "7"), Series("totalLoad", _ => "999"));
        return $$"""{"code":200,"msgCode":{{msgCode}},"message":"OK","data":{"chart_x":[{{x}}],"chart_y":[{{ys}}]} }""";
    }

    private static List<string> Labels(DateOnly day, int stepMin, int count) =>
        [.. Enumerable.Range(0, count).Select(i => day.ToDateTime(TimeOnly.MinValue).AddMinutes(i * stepMin).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))];

    /// <summary>The day the request asks for, 288 points with the given SOC.</summary>
    private static string DayFor(string body, string soc)
    {
        using var doc = JsonDocument.Parse(body);
        var day = DateOnly.ParseExact(doc.RootElement.GetProperty("startDate").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Answer(Labels(day, 5, 288), _ => soc);
    }

    private static HistoryDay Parse(string json, TimeZoneInfo zone)
    {
        using var doc = JsonDocument.Parse(json);
        return CloudHistoryReader.ParsePower(doc.RootElement, zone);
    }

    /// <summary>An accepted live row every <paramref name="stepSec"/> in [from, to), none inside the holes.</summary>
    private void Fill(DateTimeOffset from, DateTimeOffset to, int stepSec, params (DateTimeOffset From, DateTimeOffset To)[] holes)
    {
        for (var t = from; t < to; t = t.AddSeconds(stepSec))
            if (!holes.Any(h => t >= h.From && t < h.To)) _store.AddReading(t, new Reading(50, 0, 100, "AC_INVERTER"), ReadingStatus.Accepted, "");
    }

    private List<(long At, int Status, double? Soc, double? InAc, double? InDc, double? OutAc, double? OutDc, string? Reason)> Rows()
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_dir, "station.db"), Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT at, status, soc, in_ac, in_dc, out_ac, out_dc, reason FROM reading ORDER BY at, rowid";
        using var r = cmd.ExecuteReader();
        var rows = new List<(long, int, double?, double?, double?, double?, double?, string?)>();
        double? D(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
        while (r.Read()) rows.Add((r.GetInt64(0), r.GetInt32(1), D(2), D(3), D(4), D(5), D(6), r.IsDBNull(7) ? null : r.GetString(7)));
        return rows;
    }

    private StationConfig Config(bool? history = null) => new() { DataDir = _dir, CloudHistory = history };

    private CloudBackfill Backfill(HttpMessageHandler handler, StationConfig? config = null, int perRun = 4, int perDay = 40,
        Func<string?, CancellationToken, Task<(string?, string?, string)>>? session = null, Action? forget = null) =>
        new(config ?? Config(), Owned(new CloudHistoryReader(handler)), session ?? ((_, _) => Task.FromResult<(string?, string?, string)>(("ACCESS-1", "SERIAL-1", ""))), _store, _clock, _log.Add, forget)
        { CallsPerRun = perRun, CallsPerDay = perDay };

    private T Owned<T>(T d) where T : IDisposable
    {
        _owned.Add(d);
        return d;
    }

    [Theory]
    [InlineData("/api/bluiotdata/ha/v2/devices")]
    [InlineData("/api/bluiotdata/ha/v1/fulfillment")]
    [InlineData("/api/bluiotdata/dashboard/v2/getDevicePowerStatistics?deviceSn=x")]
    [InlineData("/API/BLUIOTDATA/DASHBOARD/V2/GETDEVICEPOWERSTATISTICS")]
    [InlineData("/api/bluiotdata/dashboard/v2/getDevicePowerStatistics/../setDevice")]
    [InlineData("")]
    public async Task PostAsync_PathOffTheAllowList_ThrowsAndSendsNothing(string path)
    {
        using var handler = new NoCallHandler();
        using var reader = new CloudHistoryReader(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.PostAsync(path, "ACCESS-1", "SERIAL-1", Day, 0, TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task PowerDayAsync_SendsOnlyTheReadQueryFieldsWithTheCloudHeaders()
    {
        using var handler = new DayHandler(_ => Answer(Labels(Day, 5, 288), _ => "50"));
        using var reader = new CloudHistoryReader(handler);
        var day = await reader.PowerDayAsync("ACCESS-1", "SERIAL-1", Day, Plus3, TestContext.Current.CancellationToken);
        Assert.Equal("", day.Why);
        var (method, url, req, body) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Equal(PowerUrl, url);
        Assert.Equal("ACCESS-1", req.Headers.GetValues("Authorization").Single());
        Assert.Equal("open", req.Headers.GetValues("x-os").Single());
        Assert.Equal("1.0.5", req.Headers.GetValues("x-app-ver").Single());
        Assert.True(req.Headers.Contains("x-app-key"));
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(["deviceSn", "installerFlag", "queryType", "remoteFlag", "startDate", "typeFlag"], doc.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("SERIAL-1", doc.RootElement.GetProperty("deviceSn").GetString());
        Assert.Equal("2026-10-18", doc.RootElement.GetProperty("startDate").GetString());
        Assert.Equal("pv", doc.RootElement.GetProperty("typeFlag").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("queryType").GetInt32());
        Assert.False(doc.RootElement.GetProperty("remoteFlag").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("installerFlag").GetBoolean());
    }

    [Fact]
    public async Task PostAsync_EnergyPath_SendsOnlyItsReadQueryFields()
    {
        using var handler = new DayHandler(_ => """{"msgCode":0,"data":{}}""");
        using var reader = new CloudHistoryReader(handler);
        await reader.PostAsync(EnergyPath, "ACCESS-1", "SERIAL-1", Day, 1, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => reader.PostAsync(EnergyPath, "ACCESS-1", "SERIAL-1", Day, 5, TestContext.Current.CancellationToken));
        var (_, url, _, body) = Assert.Single(handler.Calls);
        Assert.EndsWith(EnergyPath, url, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(["deviceSn", "flag", "installerFlag", "remoteFlag", "startDate"], doc.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(1, doc.RootElement.GetProperty("flag").GetInt32());
    }

    [Fact]
    public void ParsePower_288Points_MapsEachSeriesToItsColumn()
    {
        var day = Parse(Answer(Labels(Day, 5, 288), _ => "64"), Plus3);
        Assert.Equal("", day.Why);
        Assert.False(day.Hourly);
        Assert.Equal(288, day.Points.Count);
        Assert.Equal(DateTimeOffset.Parse("2026-10-17T21:00:00Z", CultureInfo.InvariantCulture), day.Points[0].At);
        Assert.Equal(DateTimeOffset.Parse("2026-10-18T20:55:00Z", CultureInfo.InvariantCulture), day.Points[^1].At);
        Assert.Equal(new HistoryPoint(day.Points[3].At, 64, 230.5, 0, 103, 7), day.Points[3]);
    }

    [Fact]
    public void ParsePower_24Points_IsHourlyWithNoPoints()
    {
        var day = Parse(Answer(Labels(Day, 60, 24), _ => "64"), Plus3);
        Assert.Equal("", day.Why);
        Assert.True(day.Hourly);
        Assert.Empty(day.Points);
    }

    [Theory]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    public async Task PowerDayAsync_Redirect_FailsAndSendsNoSecondRequest(HttpStatusCode status)
    {
        using var handler = new DayHandler(_ => Answer(Labels(Day, 5, 288), _ => "50")) { Redirect = status };
        using var reader = new CloudHistoryReader(handler);
        var day = await reader.PowerDayAsync("ACCESS-1", "SERIAL-1", Day, Plus3, TestContext.Current.CancellationToken);
        Assert.Equal(string.Create(CultureInfo.InvariantCulture, $"answered a redirect (HTTP {(int)status}), not followed"), day.Why);
        Assert.Null(day.MsgCode);
        Assert.Empty(day.Points);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public void NewHandler_NeverFollowsARedirect()
    {
        using var handler = CloudHistoryReader.NewHandler();
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public void ParsePower_HourlyDayWithAnUnreadableFirstLabel_IsHourly()
    {
        var labels = Labels(Day, 60, 24);
        labels[0] = "--";
        var day = Parse(Answer(labels, _ => "64"), Plus3);
        Assert.True(day.Hourly);
        Assert.Empty(day.Points);
    }

    [Fact]
    public void ParsePower_FallBackDay_RepeatedHourIsSummerTimeThenWinterTime()
    {
        // 25.10.2026 in Kyiv: 03:00-03:55 twice (EEST, then EET), 288 points, the last label 22:55.
        var fall = new DateOnly(2026, 10, 25);
        var labels = Labels(fall, 5, 48);
        labels.AddRange(Labels(fall, 5, 48).Skip(36));
        labels.AddRange(Labels(fall, 5, 276).Skip(48));
        Assert.Equal(288, labels.Count);
        Assert.Equal("2026-10-25 22:55", labels[^1]);
        var day = Parse(Answer(labels, _ => "70"), Kyiv);
        Assert.Equal(288, day.Points.Count);
        Assert.Equal(288, day.Points.Select(p => p.At).Distinct().Count());
        Assert.True(day.Points.Zip(day.Points.Skip(1)).All(p => p.Second.At - p.First.At == TimeSpan.FromMinutes(5)));
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T00:00:00Z", CultureInfo.InvariantCulture), day.Points[36].At);   // 03:00 EEST
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T01:00:00Z", CultureInfo.InvariantCulture), day.Points[48].At);   // 03:00 EET
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T20:55:00Z", CultureInfo.InvariantCulture), day.Points[^1].At);
    }

    [Fact]
    public void ParsePower_SpringForwardHour_IsDroppedNeverInvented()
    {
        var spring = new DateOnly(2026, 3, 29);
        var day = Parse(Answer(Labels(spring, 5, 288), _ => "70"), Kyiv);
        Assert.Equal(276, day.Points.Count);
        Assert.DoesNotContain(day.Points, p => TimeZoneInfo.ConvertTime(p.At, Kyiv).Hour == 3);
    }

    [Fact]
    public void ParsePower_PointWithoutSoc_IsDropped()
    {
        string[] socs = ["null", "\"0\"", "\"\"", "\"55.5\"", "-1", "60"];
        var day = Parse(Answer(Labels(Day, 5, socs.Length), i => socs[i]), Plus3);
        Assert.Equal([55.5, 60], day.Points.Select(p => p.Soc));
    }

    [Fact]
    public void ParsePower_ErrorAnswer_NamesItsMsgCode()
    {
        var day = Parse(Answer(Labels(Day, 5, 288), _ => "50", msgCode: 805), Plus3);
        Assert.Equal(805, day.MsgCode);
        Assert.Equal("msgCode 805", day.Why);
        Assert.Empty(day.Points);
    }

    [Fact]
    public void Gaps_OnlySpansOfTenMinutesOrMoreWithoutAnAcceptedRow()
    {
        var start = At("2026-10-18 09:00");
        Fill(start, At("2026-10-18 12:00"), 60, (At("2026-10-18 10:00"), At("2026-10-18 10:10")), (At("2026-10-18 11:00"), At("2026-10-18 11:09")));
        // A missed reading inside a gap is no reading.
        _store.AddReading(At("2026-10-18 10:04"), null, ReadingStatus.Missed, "");
        var gaps = _store.Gaps(start.ToUnixTimeSeconds(), At("2026-10-18 12:00").ToUnixTimeSeconds(), CloudBackfill.GapMinutes);
        Assert.Equal([(At("2026-10-18 10:00").ToUnixTimeSeconds(), At("2026-10-18 10:10").ToUnixTimeSeconds())], gaps);
    }

    [Fact]
    public void Gaps_AFoldedAggregateCoversItsFiveMinutes()
    {
        var start = At("2026-09-01 09:00");
        Fill(start, At("2026-09-01 11:00"), 240);
        _store.Compact(_clock.Now, 30);
        Assert.Equal(0, _store.Counts().Raw);
        Assert.Empty(_store.Gaps(start.ToUnixTimeSeconds(), At("2026-09-01 11:00").ToUnixTimeSeconds(), CloudBackfill.GapMinutes));
    }

    [Fact]
    public void AddCloudHistory_NeverChangesOrDoublesAnAcceptedRow()
    {
        _store.AddReading(At("2026-10-18 10:00"), new Reading(41, 1, 2, "M"), ReadingStatus.Accepted, "");
        _store.AddReading(At("2026-10-18 10:07"), new Reading(42, 3, 4, "M"), ReadingStatus.Accepted, "");
        _store.AddReading(At("2026-10-18 10:12"), null, ReadingStatus.Missed, "");
        HistoryPoint P(string at) => new(At(at), 90, 9, 9, 9, 9);
        Assert.Equal(1, _store.AddCloudHistory([P("2026-10-18 10:00"), P("2026-10-18 10:05"), P("2026-10-18 10:10")]));
        Assert.Equal(0, _store.AddCloudHistory([P("2026-10-18 10:10")]));
        var rows = Rows();
        Assert.Equal(4, rows.Count);
        Assert.Contains((At("2026-10-18 10:00").ToUnixTimeSeconds(), 0, 41.0, 1.0, null, 2.0, null, null), rows);
        Assert.Contains((At("2026-10-18 10:07").ToUnixTimeSeconds(), 0, 42.0, 3.0, null, 4.0, null, null), rows);
        Assert.Contains((At("2026-10-18 10:10").ToUnixTimeSeconds(), 0, 90.0, 9.0, 9.0, 9.0, 9.0, StationStore.CloudHistoryReason), rows);
        Assert.Contains(rows, r => r.Status == 1);
    }

    [Fact]
    public async Task RunOnce_FillsTheGapFromTheCloudMarkedAsCloudHistory()
    {
        Fill(At("2026-10-17 12:00"), _clock.Now, 300, (At("2026-10-18 10:00"), At("2026-10-18 11:00")));
        using var handler = new DayHandler(b => DayFor(b, "66"));
        using var backfill = Backfill(handler);
        Assert.Equal(1, await backfill.RunOnceAsync(TestContext.Current.CancellationToken));
        using (var doc = JsonDocument.Parse(Assert.Single(handler.Calls).Body))
            Assert.Equal("2026-10-18", doc.RootElement.GetProperty("startDate").GetString());
        var added = Rows().Where(r => r.Reason == StationStore.CloudHistoryReason).ToList();
        Assert.Equal(12, added.Count);
        Assert.Equal(At("2026-10-18 10:00").ToUnixTimeSeconds(), added[0].At);
        Assert.Equal(At("2026-10-18 10:55").ToUnixTimeSeconds(), added[^1].At);
        Assert.All(added, r => Assert.Equal((0, 66.0), (r.Status, r.Soc!.Value)));
        Assert.Contains(_log, l => l.Contains("18.10.2026 - 288 points, 12 rows added (calls today 1/40)", StringComparison.Ordinal));
        Assert.Equal(0, await backfill.RunOnceAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunOnce_PastDayTheCloudCannotFill_IsNotAskedAgain()
    {
        Fill(At("2026-10-17 12:00"), _clock.Now, 300, (At("2026-10-18 10:00"), At("2026-10-18 11:00")));
        using var handler = new DayHandler(b => DayFor(b, "null"));
        using var backfill = Backfill(handler);
        Assert.Equal(1, await backfill.RunOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await backfill.RunOnceAsync(TestContext.Current.CancellationToken));
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task RunOnce_TodayIsAskedAgainUntilTheDayIsOver()
    {
        Fill(At("2026-10-19 12:00"), _clock.Now, 300, (At("2026-10-20 09:00"), At("2026-10-20 10:00")));
        using var handler = new DayHandler(b => DayFor(b, "null"));
        using var backfill = Backfill(handler);
        Assert.Equal(1, await backfill.RunOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await backfill.RunOnceAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunOnce_FailedCall_IsLoggedAndTheDayAskedAgain()
    {
        Fill(At("2026-10-17 12:00"), _clock.Now, 300, (At("2026-10-18 10:00"), At("2026-10-18 11:00")));
        using var handler = new DayHandler(b => DayFor(b, "66")) { Fail = new HttpRequestException("ACCESS-1 leaked?") };
        using var backfill = Backfill(handler);
        Assert.Equal(1, await backfill.RunOnceAsync(TestContext.Current.CancellationToken));
        Assert.Contains(_log, l => l.Contains("18.10.2026 - HTTP failed", StringComparison.Ordinal));
        Assert.DoesNotContain(_log, l => l.Contains("ACCESS-1", StringComparison.Ordinal) || l.Contains("SERIAL-1", StringComparison.Ordinal));
        Assert.Equal(1, await backfill.RunOnceAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunOnce_ExpiredToken_RefreshesOnceAndAsksTheDayAgain()
    {
        Fill(At("2026-10-17 12:00"), _clock.Now, 300, (At("2026-10-18 10:00"), At("2026-10-18 11:00")));
        using var handler = new DayHandler(b => DayFor(b, "66"));
        handler.ByAccess["ACCESS-1"] = Answer(Labels(Day, 5, 288), _ => "50", msgCode: 805);
        var refused = new List<string?>();
        using var backfill = Backfill(handler, session: (r, _) =>
        {
            refused.Add(r);
            return Task.FromResult<(string?, string?, string)>((r is null ? "ACCESS-1" : "ACCESS-2", "SERIAL-1", ""));
        });
        Assert.Equal(2, await backfill.RunOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal([null, "ACCESS-1"], refused);
        Assert.Equal(["ACCESS-1", "ACCESS-2"], handler.Calls.Select(c => c.Request.Headers.GetValues("Authorization").Single()));
        Assert.Equal(12, Rows().Count(r => r.Reason == StationStore.CloudHistoryReason));
        Assert.Contains(_log, l => l.Contains("18.10.2026 - 288 points, 12 rows added (calls today 2/40)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunOnce_TokenStillExpiredAfterTheRefresh_EndsTheRun()
    {
        var holes = Enumerable.Range(16, 3).Select(d => (At($"2026-10-{d} 10:00"), At($"2026-10-{d} 11:00"))).ToArray();
        Fill(At("2026-10-15 12:00"), _clock.Now, 300, holes);
        using var handler = new DayHandler(b => Answer(Labels(Day, 5, 288), _ => "50", msgCode: 805));
        var refused = new List<string?>();
        using var backfill = Backfill(handler, session: (r, _) =>
        {
            refused.Add(r);
            return Task.FromResult<(string?, string?, string)>(("ACCESS-1", "SERIAL-1", ""));
        });
        Assert.Equal(2, await backfill.RunOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal([null, "ACCESS-1"], refused);
        Assert.Equal(2, handler.Calls.Count);
        Assert.DoesNotContain(Rows(), r => r.Reason == StationStore.CloudHistoryReason);
    }

    [Fact]
    public async Task RunOnce_KeepsTheDailyCapAcrossRestarts()
    {
        var holes = Enumerable.Range(13, 5).Select(d => (At($"2026-10-{d} 10:00"), At($"2026-10-{d} 11:00"))).ToArray();
        Fill(At("2026-10-12 12:00"), _clock.Now, 300, holes);
        using var handler = new DayHandler(b => DayFor(b, "null"));
        var ct = TestContext.Current.CancellationToken;
        using (var first = Backfill(handler, perRun: 2, perDay: 3)) Assert.Equal(2, await first.RunOnceAsync(ct));
        using var restarted = Backfill(handler, perRun: 2, perDay: 3);
        Assert.Equal(1, await restarted.RunOnceAsync(ct));
        Assert.Contains(_log, l => l.Contains("daily cap of 3 calls is reached", StringComparison.Ordinal));
        Assert.Equal(0, await restarted.RunOnceAsync(ct));
        Assert.Equal(3, handler.Calls.Count);
        _clock.Now = At("2026-10-21 12:00");
        Assert.Equal(2, await restarted.RunOnceAsync(ct));
    }

    [Fact]
    public async Task RunOnce_Off_SendsNothing()
    {
        Fill(At("2026-10-17 12:00"), _clock.Now, 300, (At("2026-10-18 10:00"), At("2026-10-18 11:00")));
        using var handler = new NoCallHandler();
        using (var off = Backfill(handler, Config(history: false))) Assert.Equal(0, await off.RunOnceAsync(TestContext.Current.CancellationToken));
        using (var cloudOff = Backfill(handler, new StationConfig { DataDir = _dir, CloudEnabled = false })) Assert.Equal(0, await cloudOff.RunOnceAsync(TestContext.Current.CancellationToken));
        File.Delete(Path.Combine(_dir, "cloud-token.json"));
        using (var noToken = Backfill(handler)) Assert.Equal(0, await noToken.RunOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RunOnce_SavesTheSessionSerialForTheNextStart()
    {
        Fill(At("2026-10-17 12:00"), _clock.Now, 300, (At("2026-10-18 10:00"), At("2026-10-18 11:00")));
        using var handler = new DayHandler(b => DayFor(b, "null"));
        using var backfill = Backfill(handler);
        Assert.Equal(1, await backfill.RunOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal("AC200PL;SERIAL-1", _store.GetMeta("cloudHistorySerial"));
    }

    // A refusal of the API (not an expired token) may mean the saved serial is another station's: it is dropped, so the next
    // run reads the device list again.
    [Fact]
    public async Task RunOnce_ApiRefusal_DropsTheSavedSerial()
    {
        Fill(At("2026-10-17 12:00"), _clock.Now, 300, (At("2026-10-18 10:00"), At("2026-10-18 11:00")));
        using var handler = new DayHandler(b => Answer(Labels(Day, 5, 288), _ => "50", msgCode: 1001));
        int forgotten = 0;
        using var backfill = Backfill(handler, forget: () => forgotten++);
        Assert.Equal(1, await backfill.RunOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal("", _store.GetMeta("cloudHistorySerial"));
        Assert.Null(CloudBackfill.SavedSerial(_store, "AC200PL"));
        Assert.Equal(1, forgotten);
        Assert.DoesNotContain(Rows(), r => r.Reason == StationStore.CloudHistoryReason);
    }

    [Fact]
    public async Task RunOnce_TokenStillExpired_KeepsTheSavedSerial()
    {
        Fill(At("2026-10-17 12:00"), _clock.Now, 300, (At("2026-10-18 10:00"), At("2026-10-18 11:00")));
        using var handler = new DayHandler(b => Answer(Labels(Day, 5, 288), _ => "50", msgCode: 805));
        int forgotten = 0;
        using var backfill = Backfill(handler, forget: () => forgotten++);
        Assert.Equal(2, await backfill.RunOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal("AC200PL;SERIAL-1", _store.GetMeta("cloudHistorySerial"));
        Assert.Equal(0, forgotten);
    }

    // The service's reader starts with the serial saved for its own station type (Program.cs): no device-list read.
    [Fact]
    public async Task ForService_SavedSerialOfThisType_ReadsNoDeviceList()
    {
        _store.SetMeta("cloudHistorySerial", "AC200PL;SERIAL-7");
        using var http = new NoCallHandler();
        using var cloud = BluettiCloudReader.ForService(Config(), http, _clock, _log.Add, _store);
        Assert.Equal(("ACCESS-1", "SERIAL-7", ""), await cloud.HistorySessionAsync(null, TestContext.Current.CancellationToken));
        Assert.Equal(0, http.Calls);
    }

    // A serial saved for another type (the station replaced, config.type changed), or with no type, is never used.
    [Theory]
    [InlineData("AC180;SERIAL-X")]
    [InlineData("SERIAL-X")]
    public async Task ForService_SavedSerialOfAnotherType_ReadsTheDeviceList(string saved)
    {
        _store.SetMeta("cloudHistorySerial", saved);
        using var http = new CannedHandler();
        http.Json("""{"msgCode":0,"message":"OK","data":[{"sn":"SERIAL-9","online":"0","model":"AC200PL","stateList":[]}]}""");
        using var cloud = BluettiCloudReader.ForService(Config(), http, _clock, _log.Add, _store);
        Assert.Equal(("ACCESS-1", "SERIAL-9", ""), await cloud.HistorySessionAsync(null, TestContext.Current.CancellationToken));
        Assert.Single(http.Calls);
    }

    // The device list the cloud left unanswered (service.log 26.10.2026): the pass is a logged failure, never "no gap" or a call.
    [Fact]
    public async Task RunOnce_SessionFails_IsALoggedFailedPassWithNoCall()
    {
        Fill(At("2026-10-17 12:00"), _clock.Now, 300, (At("2026-10-18 10:00"), At("2026-10-18 11:00")));
        using var handler = new NoCallHandler();
        using var backfill = Backfill(handler, session: (_, _) => Task.FromResult<(string?, string?, string)>((null, null, "devices: timed out after 15 s, again on retry")));
        Assert.Equal(0, await backfill.RunOnceAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Calls);
        Assert.Contains("cloud history: not run - devices: timed out after 15 s, again on retry", _log);
        Assert.Null(_store.GetMeta(CloudBackfill.DoneKey));
        Assert.Null(_store.GetMeta("cloudHistorySerial"));
        Assert.DoesNotContain(Rows(), r => r.Reason == StationStore.CloudHistoryReason);
    }

    [Fact]
    public async Task HistorySession_KnownSerial_ReadsNoDeviceList()
    {
        using var http = new NoCallHandler();
        using var cloud = new BluettiCloudReader(Config(), http, _clock, _log.Add) { KnownSerial = "SERIAL-7" };
        Assert.Equal(("ACCESS-1", "SERIAL-7", ""), await cloud.HistorySessionAsync(null, TestContext.Current.CancellationToken));
        Assert.Equal(0, http.Calls);
    }

    [Fact]
    public async Task HistorySession_TakesTheSerialFromTheDeviceListOnce()
    {
        using var http = new CannedHandler();
        http.Json("""{"msgCode":0,"message":"OK","data":[{"sn":"SERIAL-9","online":"0","model":"AC200PL","stateList":[]}]}""");
        using var cloud = new BluettiCloudReader(Config(), http, _clock, _log.Add);
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(("ACCESS-1", "SERIAL-9", ""), await cloud.HistorySessionAsync(null, ct));
        Assert.Equal(("ACCESS-1", "SERIAL-9", ""), await cloud.HistorySessionAsync(null, ct));
        Assert.Single(http.Calls);
    }

    [Fact]
    public async Task HistorySession_RefusedToken_IsRefreshedOnceAndWritten()
    {
        using var http = new CannedHandler();
        http.Json("""{"msgCode":0,"message":"OK","data":[{"sn":"SERIAL-9","online":"0","model":"AC200PL","stateList":[]}]}""");
        http.Json("""{"access_token":"ACCESS-2","refresh_token":"REFRESH-2","token_type":"bearer","expires_in":999999999}""");
        using var cloud = new BluettiCloudReader(Config(), http, _clock, _log.Add);
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(("ACCESS-1", "SERIAL-9", ""), await cloud.HistorySessionAsync(null, ct));
        Assert.Equal(("ACCESS-2", "SERIAL-9", ""), await cloud.HistorySessionAsync("ACCESS-1", ct));
        // A token another read already renewed is not refreshed again.
        Assert.Equal(("ACCESS-2", "SERIAL-9", ""), await cloud.HistorySessionAsync("ACCESS-1", ct));
        Assert.Equal(2, http.Calls.Count);
        Assert.Equal("https://sso.bluettipower.com/oauth2/token", http.Calls[1].Url);
        Assert.Contains("ACCESS-2", await File.ReadAllTextAsync(Path.Combine(_dir, "cloud-token.json"), ct), StringComparison.Ordinal);
    }
}
