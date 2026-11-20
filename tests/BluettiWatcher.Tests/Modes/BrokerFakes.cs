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

    public Dictionary<string, string> StartType { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<BrokerResult> SetStartTypeAsync(string name, string startType, CancellationToken ct)
    {
        Calls.Add($"startType {name} {startType}");
        if (!State.ContainsKey(name)) return Answer(name);
        StartType[name] = startType;
        return Task.FromResult(new BrokerResult(true, null, startType));
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

/// <summary>Scheduled tasks by full name, enabled or not.</summary>
internal sealed class FakeTaskControl : ITaskControl
{
    public Dictionary<string, bool> Enabled { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Calls { get; } = [];

    public Task<BrokerResult> SetEnabledAsync(string name, bool enabled, CancellationToken ct)
    {
        Calls.Add($"{(enabled ? "enable" : "disable")} {name}");
        if (!Enabled.ContainsKey(name)) return Task.FromResult(new BrokerResult(false, $"no task {name}", null));
        Enabled[name] = enabled;
        return Task.FromResult(new BrokerResult(true, null, enabled ? "enabled" : "disabled"));
    }
}

/// <summary>Imports are recorded; an export writes a small .nip into the exports folder as Profile Inspector's move would.</summary>
internal sealed class FakeProfileImporter : IProfileImporter
{
    public List<string> Calls { get; } = [];

    public Task<string?> ImportAsync(string inspectorExe, string nipFile, CancellationToken ct)
    {
        Calls.Add($"{inspectorExe} {nipFile}");
        return Task.FromResult<string?>(null);
    }

    public async Task<ProfileExport> ExportAsync(string inspectorExe, string exportsFolder, CancellationToken ct)
    {
        Calls.Add($"export {inspectorExe}");
        Directory.CreateDirectory(exportsFolder);
        string file = Path.Combine(exportsFolder, "customized_20261120.nip");
        await File.WriteAllTextAsync(file, "<profiles/>", ct);
        return new ProfileExport(file, "");
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
