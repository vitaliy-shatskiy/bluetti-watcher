using System.Text;
using System.Text.Json;
using BluettiWatcher.Http;
using BluettiWatcher.Modes;

namespace BluettiWatcher.Tests.Modes;

/// <summary>
/// A mode transition, once started, runs to its end on the engine's own lifetime: the request that asked for it only
/// waits for it (bounded), and only the service's shutdown stops it.
/// </summary>
public sealed class ModesTransitionTests : IDisposable
{
    private const string Fake = "fake.a";
    private const string TwoActions = """
        {"id":"two","name":"Two","flagWord":"eco","actions":[
          {"typeId":"fake.a","enabled":true,"settings":{"key":"k1","value":"on"}},
          {"typeId":"fake.a","enabled":true,"settings":{"key":"k2","value":"on"}}]}
        """;

    private readonly string _dir = Directory.CreateTempSubdirectory("bw-modes-transition-").FullName;
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeMachine _m = new();
    private readonly StationConfig _config;
    private readonly ModesEngine _engine;

    public ModesTransitionTests()
    {
        _config = new StationConfig { DataDir = _dir, PowerMode = new PowerModeConfig { FlagPath = Path.Combine(_dir, "flag", "power-mode.json") } };
        _engine = new ModesEngine(_config, new ModuleRegistry([new FakeModule(BuiltInModes.PowerPlanType, _m), new FakeModule(Fake, _m)]),
            new ModesStore(_dir), new ModeJournal(Path.Combine(_dir, ModeJournal.FileName)), new FakeFlagWriter(), _clock, _ => { });
        _engine.Start(_clock.Now.AddDays(-1));
    }

    public void Dispose()
    {
        _engine.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private HttpRequest Req(string method, string path, string body = "") =>
        new(method, path, new Dictionary<string, string>(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["host"] = $"127.0.0.1:{_config.Port}", ["x-station"] = "1" },
            Encoding.UTF8.GetBytes(body));

    private static JsonElement Json(HttpResponse r) => JsonDocument.Parse(r.Body).RootElement.Clone();

    private async Task AddTwoAsync(ModesApi api) =>
        Assert.Equal(200, (await api.HandleAsync(Req("POST", "/api/modes", TwoActions), TestContext.Current.CancellationToken)).Status);

    [Fact]
    public async Task Select_RequestCancelledMidTransition_StillRunsEveryActionAndPublishesTheMode()
    {
        var api = new ModesApi(_config, _engine);
        await AddTwoAsync(api);
        using var request = new CancellationTokenSource();
        // The request's limit runs out while the first action works (the HTTP server's 10 s limit in the live case).
        _m.Hold["k1"] = async _ => { await request.CancelAsync(); await Task.Yield(); };

        try { await api.HandleAsync(Req("POST", "/api/modes/select", """{"mode":"two"}"""), request.Token); }
        catch (OperationCanceledException) { /* the request gave up waiting: the transition must not have */ }
        await _engine.Pending;

        Assert.Equal("on", _m.Text("k2")?.Trim('"'));
        Assert.Equal("two", _engine.Snapshot.ModeId);
        Assert.Equal("two", Json(await api.HandleAsync(Req("GET", "/api/modes"), TestContext.Current.CancellationToken)).GetProperty("activeModeId").GetString());
    }

    [Fact]
    public async Task Select_ActionSlowerThanTheRequestWait_Answers202ThenTheModeIsPublished()
    {
        var api = new ModesApi(_config, _engine, changeWait: TimeSpan.Zero);
        await AddTwoAsync(api);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _m.Hold["k1"] = _ => release.Task;

        var answer = api.HandleAsync(Req("POST", "/api/modes/select", """{"mode":"two"}"""), TestContext.Current.CancellationToken);

        Assert.True(answer.IsCompleted, "the request must not wait for an action past its bounded wait");
        var r = await answer;
        Assert.Equal(202, r.Status);
        Assert.True(Json(r).GetProperty("pending").GetBoolean());
        Assert.Equal("two", Json(r).GetProperty("switching").GetString());
        release.SetResult();
        await _engine.Pending;
        var view = Json(await api.HandleAsync(Req("GET", "/api/modes"), TestContext.Current.CancellationToken));
        Assert.Equal("two", view.GetProperty("activeModeId").GetString());
        Assert.False(view.GetProperty("pending").GetBoolean());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("switching").ValueKind);
    }

    [Fact]
    public async Task Select_ServiceShutdownMidTransition_CancelsTheRest()
    {
        var api = new ModesApi(_config, _engine, changeWait: TimeSpan.Zero);
        await AddTwoAsync(api);
        _m.Hold["k1"] = ct => Task.Delay(Timeout.Infinite, ct);

        using var request = new CancellationTokenSource();
        var answer = api.HandleAsync(Req("POST", "/api/modes/select", """{"mode":"two"}"""), request.Token);
        _engine.Shutdown();

        // Bounded: a transition the shutdown does not reach would block here for ever (expiry = TimeoutException, red).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _engine.Pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await request.CancelAsync();
        Assert.True(answer.IsCompleted, "the request answered within its wait, before the shutdown");
        Assert.Null(_m.Text("k1"));
        Assert.Null(_m.Text("k2"));
    }

    [Fact]
    public async Task Select_TrayPathWithACancelledToken_StillRunsTheTransition()
    {
        await _engine.PutModeAsync(JsonSerializer.Deserialize(TwoActions, ModesJson.Default.Mode) ?? throw new InvalidOperationException("no mode"), create: true, TestContext.Current.CancellationToken);
        using var caller = new CancellationTokenSource();
        _m.Hold["k1"] = async _ => { await caller.CancelAsync(); await Task.Yield(); };

        var wait = _engine.SelectAsync("two", caller.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        await _engine.Pending;

        Assert.Equal("two", _engine.Snapshot.ModeId);
        Assert.Equal("on", _m.Text("k2")?.Trim('"'));
    }
}
