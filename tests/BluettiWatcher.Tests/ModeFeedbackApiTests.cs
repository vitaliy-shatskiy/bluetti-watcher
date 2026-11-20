using System.Text;
using System.Text.Json;
using BluettiWatcher.Decision;
using BluettiWatcher.Http;
using BluettiWatcher.Io;
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

    private StationApi Api(Func<(string, string)>? plan = null, StationService? service = null) => new(_cfg, service ?? _service, _store, _clock, T0, activePlan: plan);

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
    public async Task A_plan_read_that_fails_with_any_exception_is_dropped_and_read_again()
    {
        int reads = 0;
        var api = Api(() => Interlocked.Increment(ref reads) == 1 ? throw new ArgumentException("odd powercfg output") : (PowerModeSwitch.BalancedPlan, "Balanced"));
        var first = await api.HandleAsync(Req("GET", "/api/now"), CancellationToken.None);
        Assert.Equal(200, first.Status);
        Assert.Equal("", Json(first).GetProperty("powerPlan").GetProperty("id").GetString());
        await _service.PollAsync(CancellationToken.None);   // a poll moves the epoch: the plan is read again
        var second = await api.HandleAsync(Req("GET", "/api/now"), CancellationToken.None);
        Assert.Equal(200, second.Status);
        Assert.Equal("balanced", Json(second).GetProperty("powerPlan").GetProperty("id").GetString());
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
    [Fact]
    public async Task A_press_while_the_command_backs_off_shows_the_last_failure_and_the_retry_time()
    {
        _actions.FailEco = true;
        var api = Api();
        Assert.Equal(202, (await Press(api, "eco")).Status);
        await _service.ApplyPendingAsync(TestContext.Current.CancellationToken);
        Assert.Equal(202, (await Press(api, "eco")).Status);   // again, inside the backoff
        var settle = api.HandleAsync(Req("GET", "/api/now?settle=1"), TestContext.Current.CancellationToken);
        await _service.ApplyPendingAsync(TestContext.Current.CancellationToken);
        var now = Json(await settle.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal("Eco: COULD NOT RUN - retry at 20:01", now.GetProperty("modeError").GetString());
        Assert.Single(_actions.Calls, c => c == "eco");   // the closed backoff ran nothing
    }

    [Fact]
    public async Task The_active_plan_is_read_once_per_poll_or_choice_not_per_request()
    {
        int reads = 0;
        var api = Api(() => { Interlocked.Increment(ref reads); return (PowerModeSwitch.BalancedPlan, "Balanced"); });
        for (int i = 0; i < 3; i++) await api.HandleAsync(Req("GET", "/api/now"), TestContext.Current.CancellationToken);
        Assert.Equal(1, reads);
        await _service.PollAsync(TestContext.Current.CancellationToken);
        await api.HandleAsync(Req("GET", "/api/now"), TestContext.Current.CancellationToken);
        await api.HandleAsync(Req("GET", "/api/now"), TestContext.Current.CancellationToken);
        Assert.Equal(2, reads);
        Assert.Equal(202, (await Press(api, "eco")).Status);
        await _service.ApplyPendingAsync(TestContext.Current.CancellationToken);
        await api.HandleAsync(Req("GET", "/api/now"), TestContext.Current.CancellationToken);
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task A_hanging_plan_read_keeps_the_settle_answer_under_the_request_limit()
    {
        using var hold = new ManualResetEventSlim(false);
        var api = Api(() => { hold.Wait(TimeSpan.FromSeconds(30)); return ("", ""); });
        try
        {
            Assert.True(StationApi.SettleWait + StationApi.PlanWait < TimeSpan.FromSeconds(10));
            Assert.Equal(202, (await Press(api, "eco")).Status);
            var started = System.Diagnostics.Stopwatch.StartNew();
            var r = await api.HandleAsync(Req("GET", "/api/now?settle=1"), TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(200, r.Status);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"{started.Elapsed}");
            Assert.Equal("", Json(r).GetProperty("powerPlan").GetProperty("id").GetString());
        }
        finally { hold.Set(); }
    }
    // A request that already spent 9 s of the server's 10 (a slow head, a busy machine) gets what is left, never a
    // fresh settle window plus the plan wait: it answers at once with the state as it is.
    [Fact]
    public async Task Settle_and_plan_waits_are_bounded_by_the_time_left_in_the_request()
    {
        using var hold = new ManualResetEventSlim(false);
        var api = Api(() => { hold.Wait(TimeSpan.FromSeconds(30)); return ("", ""); });
        try
        {
            Assert.Equal(202, (await Press(api, "eco")).Status);
            var late = Req("GET", "/api/now?settle=1") with { StartedAt = System.Diagnostics.Stopwatch.GetTimestamp() - 9 * System.Diagnostics.Stopwatch.Frequency };
            var r = await api.HandleAsync(late, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(200, r.Status);
        }
        finally { hold.Set(); }
    }

    /// <summary>A station read that never answers until released, as a Bluetooth read stuck on its timeout.</summary>
    private sealed class HangingReader : IStationReader
    {
        public readonly TaskCompletionSource<Reading?> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Reading?> ReadAsync(CancellationToken ct) => Answer.Task.WaitAsync(ct);
    }

    [Fact]
    public async Task A_press_settles_while_a_station_read_hangs()
    {
        var reader = new HangingReader();
        using var service = new StationService(_cfg, reader, _actions, new FakeHibernator(true), _flag, _store, _clock, _ => { });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var poll = service.PollAsync(stop.Token);
        var presses = service.RunPressesAsync(stop.Token);
        Assert.Equal(202, (await service.SetModeAsync(ManualMode.Eco, stop.Token)).Status);
        await service.WaitModeSettledAsync(StationApi.SettleWait, stop.Token);
        Assert.Null(service.State.ModePending);   // applied within the settle window, the read still hanging
        Assert.Contains("eco", _actions.Calls);
        Assert.False(poll.IsCompleted);
        reader.Answer.SetResult(null);
        await poll;
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => presses);
    }

    [Fact]
    public async Task ApplyPending_choice_applied_now_shows_the_flag_it_wrote_before_the_next_poll()
    {
        var api = Api();
        Assert.Equal(202, (await Press(api, "eco")).Status);
        _flag.View = Flag("eco", "manual");   // what the eco command writes
        await _service.ApplyPendingAsync(TestContext.Current.CancellationToken);
        var now = Json(await api.HandleAsync(Req("GET", "/api/now"), TestContext.Current.CancellationToken));
        Assert.Equal("eco", now.GetProperty("powerMode").GetString());
    }

    [Fact]
    public async Task SetMode_press_replacing_one_still_waiting_leaves_a_superseded_event()
    {
        var api = Api();
        Assert.Equal(202, (await Press(api, "eco")).Status);
        Assert.Equal(202, (await Press(api, "max")).Status);   // the eco never ran
        var e = Assert.Single(_store.Events(0, long.MaxValue), e => e.Kind == "mode-superseded");
        Assert.Equal("eco replaced by max before it ran", e.Detail);
        Assert.Equal(ManualMode.Max, _service.State.ModePending);   // the latest press wins
    }

    [Fact]
    public async Task SetMode_same_press_again_or_after_it_applied_supersedes_nothing()
    {
        var api = Api();
        Assert.Equal(202, (await Press(api, "eco")).Status);
        Assert.Equal(202, (await Press(api, "eco")).Status);
        await _service.ApplyPendingAsync(TestContext.Current.CancellationToken);
        Assert.Equal(202, (await Press(api, "max")).Status);
        Assert.DoesNotContain(_store.Events(0, long.MaxValue), e => e.Kind == "mode-superseded");
    }

    [Fact]
    public async Task Mode_reply_carries_the_choice_left_waiting()
    {
        var api = Api();
        var max = Json(await Press(api, "max"));
        Assert.Equal("max", max.GetProperty("manual").GetString());
        Assert.Equal("max", max.GetProperty("modePending").GetString());
        await _service.ApplyPendingAsync(TestContext.Current.CancellationToken);
        var auto = Json(await Press(api, "auto"));   // Auto after Max runs no command: nothing waits
        Assert.Equal(JsonValueKind.Null, auto.GetProperty("modePending").ValueKind);
    }

    [Theory]
    [InlineData("max", false, PageCodes.MaxPinnedMains)]
    [InlineData("max", true, PageCodes.MaxPinnedCut)]
    [InlineData("eco", false, PageCodes.EcoPinnedMains)]
    [InlineData("eco", true, PageCodes.EcoPinnedCut)]
    public async Task Now_names_what_holds_a_pinned_choice_and_until_when(string mode, bool cut, string code)
    {
        using var service = cut ? InCut() : null;
        var api = Api(service: service);
        Assert.Equal(202, (await Press(api, mode)).Status);
        await (service ?? _service).ApplyPendingAsync(TestContext.Current.CancellationToken);
        var hold = Json(await api.HandleAsync(Req("GET", "/api/now"), TestContext.Current.CancellationToken)).GetProperty("modeHold");
        Assert.Equal(code, hold.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Now_has_no_hold_under_Auto()
    {
        var now = Json(await Api().HandleAsync(Req("GET", "/api/now"), TestContext.Current.CancellationToken));
        Assert.Equal(JsonValueKind.Null, now.GetProperty("modeHold").ValueKind);
    }

    [Fact]
    public async Task SetMode_Auto_after_Max_says_the_rules_take_over_at_the_next_reading_and_asks_for_it_now()
    {
        _cfg.Mac = "AA:BB:CC:DD:EE:FF";   // a station to poll: the reading the Auto asks for comes
        using var service = InCut();
        var api = Api(service: service);
        Assert.Equal(202, (await Press(api, "max")).Status);
        await service.ApplyPendingAsync(TestContext.Current.CancellationToken);
        Assert.Equal(202, (await Press(api, "auto")).Status);
        var hold = Json(await api.HandleAsync(Req("GET", "/api/now"), TestContext.Current.CancellationToken)).GetProperty("modeHold");
        Assert.Equal(PageCodes.AutoNext, hold.GetProperty("code").GetString());
        Assert.True(service.TakePollRequest());   // the loop reads the station now instead of at its minute
        Assert.False(service.TakePollRequest());
        var settle = api.HandleAsync(Req("GET", "/api/now?settle=1"), TestContext.Current.CancellationToken);
        Assert.False(settle.IsCompleted);   // settles on the reading the Auto asked for
        await service.PollAsync(TestContext.Current.CancellationToken);
        var now = Json(await settle.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(JsonValueKind.Null, now.GetProperty("modeHold").ValueKind);
    }

    // No station to poll (no address, no cloud): the loop only idles, so the Auto waits for no reading and the settle
    // answers at once instead of after its whole window.
    [Fact]
    public async Task SetMode_Auto_after_Max_with_nothing_to_poll_waits_for_no_reading()
    {
        using var service = InCut();
        var api = Api(service: service);
        Assert.NotNull(_cfg.PollProblem);
        Assert.Equal(202, (await Press(api, "max")).Status);
        await service.ApplyPendingAsync(TestContext.Current.CancellationToken);
        Assert.Equal(202, (await Press(api, "auto")).Status);
        Assert.False(service.AutoAwaitsReading);
        Assert.False(service.TakePollRequest());
        var now = Json(await api.HandleAsync(Req("GET", "/api/now?settle=1"), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(JsonValueKind.Null, now.GetProperty("modeHold").ValueKind);
    }

    /// <summary>A service that starts inside a confirmed cut (its saved state), the station not answering.</summary>
    private StationService InCut()
    {
        _store.SaveState(new StationState { Cut = true, CutSince = T0.AddMinutes(-10), OffSince = T0.AddMinutes(-15) });
        return new StationService(_cfg, new QueueReader(), _actions, new FakeHibernator(true), _flag, _store, _clock, _ => { });
    }
}