using System.Text.Json.Nodes;
using BluettiWatcher.Modes;
using BluettiWatcher.Modes.Broker;
using BluettiWatcher.Modes.Modules;

namespace BluettiWatcher.Tests.Modes;

public sealed class ServiceStateModuleTests
{
    private readonly FakeServiceReader _services = new();
    private readonly FakeBroker _broker = new();
    private readonly ServiceStateModule _module;

    public ServiceStateModuleTests()
    {
        _module = new ServiceStateModule(_services, _broker, new JumpClock());
        // The fake machine: a broker start or stop settles the service at once.
        _broker.OnRun = (op, args) =>
        {
            string name = args["name"]!.GetValue<string>();
            var s = _services.Services[name];
            _services.Services[name] = op switch
            {
                "service.stop" => s with { State = ServiceRunState.Stopped },
                "service.start" => s with { State = ServiceRunState.Running },
                "service.setStartType" => s with { StartType = args["startType"]!.GetValue<string>() },
                _ => s,
            };
        };
    }

    private static JsonObject Settings(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static string RunningKey(string name) => $"service/{name}/running";

    [Fact]
    public async Task EnterThenLeave_ServiceStoppedByTheMode_IsStartedAgain()
    {
        _services.Add("Spooler", ServiceRunState.Running);
        var intent = Assert.Single(_module.Expand(Settings("""{"services":[{"name":"Spooler","running":false}]}""")));

        var baseline = await _module.ReadAsync(intent.Key, CancellationToken.None);
        var written = await _module.ApplyAsync(intent, CancellationToken.None);
        var current = await _module.ReadAsync(intent.Key, CancellationToken.None);
        Assert.True(written.Ok);
        Assert.True(_module.Equivalent(intent.Key, current.Value, written.ReadBack)); // still ours
        var restored = await _module.ApplyAsync(new ResourceIntent(intent.Key, baseline.Value), CancellationToken.None);

        Assert.True(restored.Ok);
        Assert.Equal(["service.stop", "service.start"], _broker.Calls.Select(c => c.Op));
        Assert.Equal(ServiceRunState.Running, _services.Services["Spooler"].State);
    }

    [Fact]
    public async Task Read_ServiceStartedByTheUserAfterTheStop_NoLongerEquivalentToWhatWasWritten()
    {
        _services.Add("Spooler", ServiceRunState.Running);
        var written = await _module.ApplyAsync(new ResourceIntent(RunningKey("Spooler"), false), CancellationToken.None);
        _services.Services["Spooler"] = _services.Services["Spooler"] with { State = ServiceRunState.Running };

        var current = await _module.ReadAsync(RunningKey("Spooler"), CancellationToken.None);

        Assert.False(_module.Equivalent(RunningKey("Spooler"), current.Value, written.ReadBack));
    }

    [Fact]
    public async Task Apply_AlreadyAtTarget_WritesNothing()
    {
        _services.Add("Spooler", ServiceRunState.Stopped);

        var r = await _module.ApplyAsync(new ResourceIntent(RunningKey("Spooler"), false), CancellationToken.None);

        Assert.True(r.Ok);
        Assert.Empty(_broker.Calls);
    }

    [Fact]
    public async Task Apply_BrokerRefuses_FailsWithItsError()
    {
        _services.Add("Spooler", ServiceRunState.Running);
        _broker.Answer = (_, _) => new BrokerResult(false, "service not in the policy", null);

        var r = await _module.ApplyAsync(new ResourceIntent(RunningKey("Spooler"), false), CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Contains("service not in the policy", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_StopPassesThroughStopPending_WaitsAndReadsBackStopped()
    {
        _services.Add("Spooler", ServiceRunState.Running);
        _broker.OnRun = (_, _) =>
        {
            _services.Services["Spooler"] = _services.Services["Spooler"] with { State = ServiceRunState.Stopped };
            _services.Transit.Enqueue(ServiceRunState.StopPending);
            _services.Transit.Enqueue(ServiceRunState.StopPending);
        };

        var r = await _module.ApplyAsync(new ResourceIntent(RunningKey("Spooler"), false), CancellationToken.None);

        Assert.True(r.Ok);
        Assert.False(r.ReadBack!.GetValue<bool>());
        Assert.Empty(_services.Transit);
    }

    [Fact]
    public async Task Apply_ServiceNeverStops_FailsAfterTheBudget()
    {
        _services.Add("Spooler", ServiceRunState.Running);
        _broker.OnRun = null; // the broker answers ok, the service keeps running

        var r = await _module.ApplyAsync(new ResourceIntent(RunningKey("Spooler"), false), CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Contains("running 10 s after the stop", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_ServiceManagerUnreadable_IsAFailedRead()
    {
        _services.Add("Spooler", ServiceRunState.Running);
        _services.Broken = true;

        var r = await _module.ReadAsync(RunningKey("Spooler"), CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Null(r.Value);
    }

    [Fact]
    public async Task Read_NotInstalled_IsAFailedRead()
    {
        var r = await _module.ReadAsync(RunningKey("Missing"), CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Contains("not installed", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Expand_StartType_ComesBeforeRunningAndUnchangedIsSkipped()
    {
        var intents = _module.Expand(Settings("""
            {"services":[{"name":"A","running":false,"startType":"manual"},{"name":"B","running":true,"startType":"unchanged"}]}
            """));

        Assert.Equal(["service/A/startType", "service/A/running", "service/B/running"], intents.Select(i => i.Key));
    }

    [Fact]
    public async Task Apply_StartType_GoesThroughTheBrokerAndReadsBack()
    {
        _services.Add("A", ServiceRunState.Running, "automatic");

        var r = await _module.ApplyAsync(new ResourceIntent("service/A/startType", "manual"), CancellationToken.None);

        Assert.True(r.Ok);
        Assert.Equal("manual", r.ReadBack!.GetValue<string>());
        var call = Assert.Single(_broker.Calls);
        Assert.Equal("service.setStartType", call.Op);
        Assert.Equal("manual", call.Args["startType"]!.GetValue<string>());
    }

    [Fact]
    public void Equivalent_StartType_IgnoresCase()
    {
        Assert.True(_module.Equivalent("service/A/startType", "Manual", "manual"));
        Assert.False(_module.Equivalent("service/A/startType", "manual", "automatic"));
    }

    [Fact]
    public void Validate_RepeatedAndSlashedNames_AreReported()
    {
        var problems = _module.Validate(Settings("""
            {"services":[{"name":"A","running":false},{"name":"a","running":true},{"name":"x/y","running":true}]}
            """));

        Assert.Contains(problems, p => p.Contains("listed twice", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("cannot contain '/'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Discover_ListsInstalledServices_AbsentWhenUnreadable()
    {
        _services.Add("Spooler", ServiceRunState.Running);
        var lists = await _module.DiscoverAsync(CancellationToken.None);
        Assert.Equal("Spooler", Assert.Single(lists["services"]).Id);

        _services.Broken = true;
        Assert.Empty(await _module.DiscoverAsync(CancellationToken.None));
    }
}
