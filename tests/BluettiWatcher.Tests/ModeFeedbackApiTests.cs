using System.Text;
using System.Text.Json;
using BluettiWatcher.Http;
using BluettiWatcher.PowerMode;
using BluettiWatcher.Storage;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

/// <summary>What the page needs to show a mode press honestly: /api/now?settle=1, the flag's mode and the active plan (api.md /api/now).</summary>
public sealed class ModeFeedbackApiTests : IDisposable
{
    private const int Port = 18940;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-watcher-test-" + Guid.NewGuid().ToString("N"));
    private readonly StationStore _store;
    private readonly FakeActions _actions = new();
    private readonly FixedFlag _flag = new(Flag("normal", "manual"));
    private readonly StationService _service;
    private readonly StationConfig _cfg;
    private readonly ManualClock _clock = new(T0);

    public ModeFeedbackApiTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new StationStore(Path.Combine(_dir, "station.db"));
        _cfg = new StationConfig { DataDir = _dir, Port = Port, FlagPath = Path.Combine(_dir, "flag", "power-mode.json") };
        _service = new StationService(_cfg, new QueueReader(), _actions, new FakeHibernator(true), _flag, _store, _clock, _ => { });
    }

    public void Dispose()
    {
        _service.Dispose();
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private StationApi Api(Func<(string, string)>? plan = null) => new(_cfg, _service, _store, _clock, T0, activePlan: plan);

    private static HttpRequest Req(string method, string path, string? body = null)
    {
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = $"127.0.0.1:{Port}", ["X-Station"] = "1" };
        string q = path.Contains('?', StringComparison.Ordinal) ? path[(path.IndexOf('?', StringComparison.Ordinal) + 1)..] : "";
        var query = q.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(kv => kv.Split('=')).ToDictionary(a => a[0], a => a.Length > 1 ? a[1] : "");
        return new HttpRequest(method, path.Split('?')[0], query, h, Encoding.UTF8.GetBytes(body ?? ""));
    }

    private static JsonElement Json(HttpResponse r)
    {
        using var doc = JsonDocument.Parse(r.Body);
        return doc.RootElement.Clone();
    }

    private static Task<HttpResponse> Press(StationApi api, string mode) => api.HandleAsync(Req("POST", "/api/mode", $$"""{ "mode": "{{mode}}" }"""), CancellationToken.None);

    [Fact]
    public async Task Settle_waits_for_the_saved_choice_and_answers_once_it_is_applied()
    {
        var api = Api();
        Assert.Equal(202, (await Press(api, "eco")).Status);
        var settle = api.HandleAsync(Req("GET", "/api/now?settle=1"), CancellationToken.None);
        Assert.False(settle.IsCompleted);   // the choice is saved, not applied: the read waits for it
        Assert.Equal("eco", Json(await api.HandleAsync(Req("GET", "/api/now"), CancellationToken.None)).GetProperty("modePending").GetString());
        await _service.ApplyPendingAsync(CancellationToken.None);
        var now = Json(await settle.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(JsonValueKind.Null, now.GetProperty("modePending").ValueKind);
        Assert.Equal("", now.GetProperty("modeError").GetString());
        Assert.Contains("eco", _actions.Calls);
    }

    [Fact]
    public async Task Settle_answers_at_once_when_nothing_waits()
    {
        var r = await Api().HandleAsync(Req("GET", "/api/now?settle=1"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(200, r.Status);
    }

    [Fact]
    public async Task Settle_answers_when_the_action_fails_with_the_failure()
    {
        _actions.FailEco = true;
        var api = Api();
        Assert.Equal(202, (await Press(api, "eco")).Status);
        var settle = api.HandleAsync(Req("GET", "/api/now?settle=1"), CancellationToken.None);
        await _service.ApplyPendingAsync(CancellationToken.None);
        var now = Json(await settle.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.StartsWith("Eco: ", now.GetProperty("modeError").GetString(), StringComparison.Ordinal);
        Assert.Equal("mode-failed", now.GetProperty("modeErrorNote").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Settle_answers_when_an_emergency_holds_the_choice_back_and_says_why()
    {
        var api = Api();
        Assert.Equal(202, (await Press(api, "eco")).Status);
        var settle = api.HandleAsync(Req("GET", "/api/now?settle=1"), CancellationToken.None);
        _flag.View = Flag("emergency", "station");
        await _service.ApplyPendingAsync(CancellationToken.None);
        var now = Json(await settle.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(JsonValueKind.Null, now.GetProperty("modePending").ValueKind);
        Assert.Equal(PageCodes.EcoHeld, now.GetProperty("modeErrorNote").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(PowerModeSwitch.BalancedPlan, "Balanced", "balanced")]
    [InlineData(PowerModeSwitch.HighPlan, "High performance", "high")]
    [InlineData(PowerModeSwitch.SaverPlan, "Power saver", "saver")]
    [InlineData("0a1b2c3d-1111-2222-3333-444455556666", "My plan", "other")]
    [InlineData("", "", "")]
    public async Task Now_names_the_active_plan_as_the_switch_reads_it(string plan, string name, string id)
    {
        var now = Json(await Api(() => (plan, name)).HandleAsync(Req("GET", "/api/now"), CancellationToken.None));
        Assert.Equal(id, now.GetProperty("powerPlan").GetProperty("id").GetString());
        Assert.Equal(name, now.GetProperty("powerPlan").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Now_with_a_plan_read_that_throws_answers_without_a_plan()
    {
        var now = Json(await Api(() => throw new InvalidOperationException("powercfg gone")).HandleAsync(Req("GET", "/api/now"), CancellationToken.None));
        Assert.Equal("", now.GetProperty("powerPlan").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Now_carries_the_flag_mode_in_force()
    {
        var api = Api();
        Assert.Equal("unreadable", Json(await api.HandleAsync(Req("GET", "/api/now"), CancellationToken.None)).GetProperty("powerMode").GetString());   // nothing read yet
        _flag.View = Flag("eco", "manual");
        await _service.PollAsync(CancellationToken.None);   // the service reads the flag on a poll
        Assert.Equal("eco", Json(await api.HandleAsync(Req("GET", "/api/now"), CancellationToken.None)).GetProperty("powerMode").GetString());
    }
}
