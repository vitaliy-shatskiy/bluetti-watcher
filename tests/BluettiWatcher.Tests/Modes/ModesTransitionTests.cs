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

    /// <summary>Adds mode two through the API and waits for the change (an api built with no wait answers 202 at once).</summary>
    private async Task AddTwoAsync(ModesApi api)
    {
        int status = (await api.HandleAsync(Req("POST", "/api/modes", TwoActions), TestContext.Current.CancellationToken)).Status;
        Assert.True(status is 200 or 202, $"add answered {status}");
        await _engine.Pending;
        Assert.Contains(_engine.Settings.Modes, m => m.Id == "two");
    }

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
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _m.Hold["k1"] = _ => { entered.TrySetResult(); return release.Task; };

        var answer = api.HandleAsync(Req("POST", "/api/modes/select", """{"mode":"two"}"""), TestContext.Current.CancellationToken);

        Assert.True(answer.IsCompleted, "the request must not wait for an action past its bounded wait");
        var r = await answer;
        Assert.Equal(202, r.Status);
        Assert.True(Json(r).GetProperty("pending").GetBoolean());
        // The change runs on the thread pool: once its action is under way the view names the mode being entered.
        await entered.Task.WaitAsync(AnswerBound, TestContext.Current.CancellationToken);
        Assert.Equal("two", Json(await api.HandleAsync(Req("GET", "/api/modes"), TestContext.Current.CancellationToken)).GetProperty("switching").GetString());
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

    private const string TwoK2Off = """
        {"id":"two","name":"Two","flagWord":"eco","actions":[
          {"typeId":"fake.a","enabled":true,"settings":{"key":"k1","value":"on"}},
          {"typeId":"fake.a","enabled":true,"settings":{"key":"k2","value":"off"}}]}
        """;

    /// <summary>The longest a test waits for an answer that must come at once: past it the request was held (red).</summary>
    private static readonly TimeSpan AnswerBound = TimeSpan.FromSeconds(5);

    private async Task ActivateTwoAsync(ModesApi api)
    {
        await AddTwoAsync(api);
        await api.HandleAsync(Req("POST", "/api/modes/select", """{"mode":"two"}"""), TestContext.Current.CancellationToken);
        await _engine.Pending;
        Assert.Equal("two", _engine.Snapshot.ModeId);
    }

    /// <summary>
    /// The PUT on a thread of its own, its answer awaited at most <see cref="AnswerBound"/>: a handler that blocks its caller
    /// expires the wait (TimeoutException) instead of hanging the test.
    /// </summary>
    private Task<HttpResponse> PutBoundedAsync(ModesApi api, string body) =>
        Task.Run(() => api.HandleAsync(Req("PUT", "/api/modes/two", body), TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)
            .WaitAsync(AnswerBound, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Put_ModuleBlocksItsThreadWhileApplying_Answers202ThenTheChangeIsApplied()
    {
        // The live case: display.refreshRate's ChangeDisplaySettingsEx blocks its thread while the screens resync.
        var api = new ModesApi(_config, _engine, changeWait: TimeSpan.Zero);
        await ActivateTwoAsync(api);
        using var release = new ManualResetEventSlim();
        _m.Hold["k2"] = _ => { release.Wait(TestContext.Current.CancellationToken); return Task.CompletedTask; };
        try
        {
            var r = await PutBoundedAsync(api, TwoK2Off);

            Assert.Equal(202, r.Status);
            Assert.True(Json(r).GetProperty("pending").GetBoolean());
        }
        finally { release.Set(); }
        await _engine.Pending;
        Assert.Equal("off", _m.Text("k2")?.Trim('"'));
    }

    [Fact]
    public async Task Put_ModuleValidationBlocks_Answers202ThenTheModeIsSaved()
    {
        var api = new ModesApi(_config, _engine, changeWait: TimeSpan.Zero);
        await ActivateTwoAsync(api);
        using var release = new ManualResetEventSlim();
        _m.OnValidate = () => release.Wait(TestContext.Current.CancellationToken);
        try
        {
            var r = await PutBoundedAsync(api, TwoK2Off);

            Assert.Equal(202, r.Status);
        }
        finally { release.Set(); }
        await _engine.Pending;
        Assert.Equal("off", _engine.Settings.Modes.Single(m => m.Id == "two").Actions[1].Settings["value"]?.GetValue<string>());
    }

    [Fact]
    public async Task Put_WhileATransitionHoldsTheEngine_Answers202ThenTheModeIsSaved()
    {
        var api = new ModesApi(_config, _engine, changeWait: TimeSpan.Zero);
        await ActivateTwoAsync(api);
        var three = JsonSerializer.Deserialize("""
            {"id":"three","name":"Three","flagWord":"eco","actions":[{"typeId":"fake.a","enabled":true,"settings":{"key":"k3","value":"on"}}]}
            """, ModesJson.Default.Mode) ?? throw new InvalidOperationException("no mode");
        await _engine.PutModeAsync(three, create: true, TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _m.Hold["k3"] = _ => { entered.TrySetResult(); return release.Task; };
        // The transition to three holds the engine's lock until its action ends.
        _ = _engine.SelectAsync("three", TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(AnswerBound, TestContext.Current.CancellationToken);
        try
        {
            var r = await PutBoundedAsync(api, TwoK2Off);

            Assert.Equal(202, r.Status);
            Assert.Equal("three", Json(r).GetProperty("switching").GetString());
        }
        finally { release.SetResult(); }
        await _engine.Pending;
        Assert.Equal("three", _engine.Snapshot.ModeId);
        Assert.Equal("off", _engine.Settings.Modes.Single(m => m.Id == "two").Actions[1].Settings["value"]?.GetValue<string>());
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
