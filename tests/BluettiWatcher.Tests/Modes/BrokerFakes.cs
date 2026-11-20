using System.Text.Json.Nodes;
using BluettiWatcher.Modes.Broker;

namespace BluettiWatcher.Tests.Modes;

/// <summary>Services by name with a state each; records every call.</summary>
internal sealed class FakeServiceControl : IServiceControl
{
    public Dictionary<string, string> State { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Calls { get; } = [];

    private Task<BrokerResult> Answer(string name) =>
        Task.FromResult(State.TryGetValue(name, out var s) ? new BrokerResult(true, null, s) : new BrokerResult(false, $"no service {name}", null));

    public Task<BrokerResult> QueryAsync(string name, CancellationToken ct) { Calls.Add($"query {name}"); return Answer(name); }

    public Task<BrokerResult> StartAsync(string name, CancellationToken ct)
    {
        Calls.Add($"start {name}");
        if (State.ContainsKey(name)) State[name] = "running";
        return Answer(name);
    }

    public Task<BrokerResult> StopAsync(string name, CancellationToken ct)
    {
        Calls.Add($"stop {name}");
        if (State.ContainsKey(name)) State[name] = "stopped";
        return Answer(name);
    }
}

/// <summary>GPUs in memory; a set changes the limit in force. <see cref="FailQuery"/> makes every read fail.</summary>
internal sealed class FakeNvidiaSmi : INvidiaSmi
{
    public List<GpuInfo> Gpus { get; } = [new GpuInfo("0", "Test GPU", 300, 100, 350, 300)];
    public List<string> Calls { get; } = [];
    public bool FailQuery { get; set; }
    public string? FailSet { get; set; }

    public Task<GpuQuery> QueryAsync(CancellationToken ct)
    {
        Calls.Add("query");
        return Task.FromResult(FailQuery ? GpuQuery.Failed("nvidia-smi exit 9") : GpuQuery.Of([.. Gpus]));
    }

    public Task<string?> SetPowerLimitAsync(string gpu, double watts, CancellationToken ct)
    {
        Calls.Add(FormattableString.Invariant($"set {gpu} {watts}"));
        if (FailSet is not null) return Task.FromResult<string?>(FailSet);
        int i = Gpus.FindIndex(g => g.Id == gpu);
        Gpus[i] = Gpus[i] with { Watts = watts };
        return Task.FromResult<string?>(null);
    }
}

internal sealed class FakeProfileImporter : IProfileImporter
{
    public List<string> Calls { get; } = [];

    public Task<string?> ImportAsync(string inspectorExe, string nipFile, CancellationToken ct)
    {
        Calls.Add($"{inspectorExe} {nipFile}");
        return Task.FromResult<string?>(null);
    }
}

/// <summary>A broker that answers from a function and records what it was asked.</summary>
internal sealed class FakeAdminBroker(Func<string, JsonObject, BrokerResult> answer) : IAdminBroker
{
    public List<string> Calls { get; } = [];

    public Task<BrokerResult> RunAsync(string operation, JsonObject args, CancellationToken ct)
    {
        Calls.Add($"{operation} {args.ToJsonString()}");
        return Task.FromResult(answer(operation, args));
    }
}
