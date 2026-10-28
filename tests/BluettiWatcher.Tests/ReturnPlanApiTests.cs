using System.Text;
using System.Text.Json;
using BluettiWatcher.Http;
using BluettiWatcher.PowerMode;
using BluettiWatcher.Storage;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

/// <summary>The page's "after eco" choice: returnPlan in power-mode.config.json beside the flag (api.md, /api/return-plan).</summary>
public sealed class ReturnPlanApiTests : IDisposable
{
    private const int Port = 18940;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-watcher-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _flagDir;
    private readonly string _configFile;
    private readonly StationStore _store;
    private readonly StationService _service;
    private readonly StationApi _api;

    public ReturnPlanApiTests()
    {
        _flagDir = Path.Combine(_dir, "flag");
        Directory.CreateDirectory(_flagDir);
        _configFile = Path.Combine(_flagDir, ReturnPlanFile.FileName);
        _store = new StationStore(Path.Combine(_dir, "station.db"));
        var cfg = new StationConfig { DataDir = _dir, Port = Port, FlagPath = Path.Combine(_flagDir, "power-mode.json") };
        var clock = new ManualClock(T0);
        _service = new StationService(cfg, new QueueReader(), new FakeActions(), new FakeHibernator(true), new FixedFlag(Station), _store, clock, _ => { });
        _api = new StationApi(cfg, _service, _store, clock, T0);
    }

    public void Dispose()
    {
        _service.Dispose();
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    // Sync on purpose: the file is tiny and the test thread owns it.
    private void Put(string text) => File.WriteAllText(_configFile, text);
    private string Got() => File.ReadAllText(_configFile);

    private static readonly (string, string) Host = ("Host", $"127.0.0.1:{Port}");
    private static readonly (string, string) XStation = ("X-Station", "1");

    private static HttpRequest Req(string method, string path, string? body, params (string, string)[] headers)
    {
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in headers) h[k] = v;
        return new HttpRequest(method, path, new Dictionary<string, string>(), h, Encoding.UTF8.GetBytes(body ?? ""));
    }

    private Task<HttpResponse> Set(string value, params (string, string)[] headers) =>
        _api.HandleAsync(Req("POST", "/api/return-plan", $$"""{ "returnPlan": "{{value}}" }""", headers.Length > 0 ? headers : [Host, XStation]), CancellationToken.None);

    private async Task<string> Shown(string path = "/api/return-plan")
    {
        var r = await _api.HandleAsync(Req("GET", path, null, Host), CancellationToken.None);
        Assert.Equal(200, r.Status);
        using var doc = JsonDocument.Parse(r.Body);
        return doc.RootElement.GetProperty("returnPlan").GetString()!;
    }

    [Fact]
    public async Task No_config_file_shows_high_in_both_answers()
    {
        Assert.Equal("high", await Shown());
        Assert.Equal("high", await Shown("/api/now"));
    }

    [Theory]
    [InlineData("high")]
    [InlineData("balanced")]
    [InlineData("previous")]
    public async Task Each_choice_is_written_and_read_back_as_the_switch_reads_it(string value)
    {
        var r = await Set(value);
        Assert.Equal(200, r.Status);
        Assert.Equal(value, await Shown());
        Assert.Equal(value, await Shown("/api/now"));
        string want = value switch { "high" => PowerModeSwitch.HighPlan, "balanced" => PowerModeSwitch.BalancedPlan, _ => "previous" };
        var cfg = PowerModeSwitch.ReadConfig(Path.Combine(_flagDir, "power-mode.json"));
        Assert.Equal(want, cfg.ReturnPlan);
        Assert.Empty(cfg.Warn);
    }

    [Fact]
    public async Task Other_keys_are_kept_and_a_duplicate_returnPlan_is_dropped()
    {
        Put("""{ "gpuCap": false, "returnPlan": "high", "extra": { "a": [1, 2] }, "ReturnPlan": "high" }""");
        Assert.Equal(200, (await Set("balanced")).Status);
        using var doc = JsonDocument.Parse(Got());
        var props = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["gpuCap", "returnPlan", "extra"], props);
        Assert.False(doc.RootElement.GetProperty("gpuCap").GetBoolean());
        Assert.Equal(2, doc.RootElement.GetProperty("extra").GetProperty("a")[1].GetInt32());
        Assert.False(PowerModeSwitch.ReadConfig(Path.Combine(_flagDir, "power-mode.json")).GpuCap);
        Assert.Empty(Directory.GetFiles(_flagDir, "*.tmp"));
    }

    [Fact]
    public async Task A_plan_guid_set_by_hand_shows_as_custom_and_stays_until_a_choice()
    {
        const string text = """{"returnPlan":"0a1b2c3d-1111-2222-3333-444455556666"}""";
        Put(text);
        Assert.Equal("custom", await Shown());
        Assert.Equal(text, Got());
        Assert.Equal(200, (await Set("previous")).Status);
        Assert.Equal("previous", await Shown());
    }

    [Theory]
    [InlineData("saver")]
    [InlineData("custom")]
    [InlineData("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")]
    [InlineData("HIGH")]
    public async Task Any_other_value_is_400_and_nothing_written(string value)
    {
        Put("""{"gpuCap":true}""");
        var r = await Set(value);
        Assert.Equal(400, r.Status);
        Assert.Equal("""{"gpuCap":true}""", Got());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "returnPlan": 1 """)]
    [InlineData("""["high"]""")]
    public async Task A_bad_body_is_400(string body)
    {
        var r = await _api.HandleAsync(Req("POST", "/api/return-plan", body, Host, XStation), CancellationToken.None);
        Assert.Equal(400, r.Status);
        Assert.False(File.Exists(_configFile));
    }

    [Fact]
    public async Task Without_the_guard_header_or_with_a_foreign_origin_it_is_403_and_nothing_written()
    {
        Assert.Equal(403, (await Set("balanced", Host)).Status);
        Assert.Equal(403, (await Set("balanced", Host, XStation, ("Origin", "http://evil.example"))).Status);
        Assert.Equal(403, (await Set("balanced", ("Host", "evil.example"), XStation)).Status);
        Assert.False(File.Exists(_configFile));
    }

    [Fact]
    public async Task Other_methods_are_405()
    {
        var r = await _api.HandleAsync(Req("PUT", "/api/return-plan", "{}", Host, XStation), CancellationToken.None);
        Assert.Equal(405, r.Status);
    }

    [Fact]
    public async Task A_config_that_is_not_json_is_409_and_kept()
    {
        Put("{ broken");
        Assert.Equal(409, (await Set("balanced")).Status);
        Assert.Equal("{ broken", Got());
    }

    [Fact]
    public async Task A_missing_flag_folder_is_409_and_never_created()
    {
        Directory.Delete(_flagDir);
        Assert.Equal(409, (await Set("balanced")).Status);
        Assert.False(Directory.Exists(_flagDir));
    }

    [Fact]
    public async Task A_failed_write_leaves_the_old_file_and_no_temp()
    {
        const string old = """{"returnPlan":"high","gpuCap":false}""";
        Put(old);
        HttpResponse r;
        // Held open without delete sharing: the move over it fails as a locked file does.
        using (new FileStream(_configFile, FileMode.Open, FileAccess.Read, FileShare.Read)) r = await Set("balanced");
        Assert.Equal(500, r.Status);
        Assert.Equal(old, Got());
        Assert.Empty(Directory.GetFiles(_flagDir, "*.tmp"));
        Assert.Equal("high", await Shown());
    }
}
