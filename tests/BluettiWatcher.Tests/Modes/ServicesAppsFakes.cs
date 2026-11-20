using System.Text.Json.Nodes;
using BluettiWatcher.Io;
using BluettiWatcher.Modes;
using BluettiWatcher.Modes.Broker;
using BluettiWatcher.Modes.Modules;

namespace BluettiWatcher.Tests.Modes;

/// <summary>A clock whose timers fire at once and move it forward by their due time: a bounded poll runs in milliseconds.</summary>
internal sealed class JumpClock : TimeProvider
{
    private readonly Lock _lock = new();
    private long _ticks = DateTimeOffset.UnixEpoch.Ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_lock) return _ticks;
    }

    public override DateTimeOffset GetUtcNow() => new(GetTimestamp(), TimeSpan.Zero);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_lock) _ticks += Math.Max(0, dueTime.Ticks);
        ThreadPool.QueueUserWorkItem(_ => callback(state));
        return new NoTimer();
    }

    private sealed class NoTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Records every broker call; answers with <see cref="Answer"/> and runs <see cref="OnRun"/> (the fake machine's reaction).</summary>
internal sealed class FakeBroker : IAdminBroker
{
    public List<(string Op, JsonObject Args)> Calls { get; } = [];
    public Func<string, JsonObject, BrokerResult> Answer { get; set; } = (_, _) => new BrokerResult(true, null, null);
    public Action<string, JsonObject>? OnRun { get; set; }

    public Task<BrokerResult> RunAsync(string operation, JsonObject args, CancellationToken ct)
    {
        Calls.Add((operation, (JsonObject)args.DeepClone()));
        var r = Answer(operation, args);
        if (r.Ok) OnRun?.Invoke(operation, args);
        return Task.FromResult(r);
    }
}

internal sealed class FakeServiceReader : IServiceReader
{
    public Dictionary<string, ServiceInfo> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Broken { get; set; }

    /// <summary>States a query returns before the stored one (a stop that passes through StopPending first).</summary>
    public Queue<ServiceRunState> Transit { get; } = new();

    public void Add(string name, ServiceRunState state, string startType = "manual") => Services[name] = new ServiceInfo(name, name + " display", state, startType);

    public ServiceQuery Query(string name)
    {
        if (Broken) return new(null, "the service manager cannot be opened (error 5)");
        if (!Services.TryGetValue(name, out var s)) return new(null, $"service '{name}' is not installed");
        return Transit.TryDequeue(out var t) ? new(s with { State = t }, "") : new(s, "");
    }

    public IReadOnlyList<ServiceInfo>? List() => Broken ? null : [.. Services.Values];
}

internal sealed class FakeProcessControl : IProcessControl
{
    public Dictionary<string, int> Running { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ClosesOnRequest { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> SurvivesKill { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> CloseRequests { get; } = [];
    public List<string> Kills { get; } = [];
    public bool Broken { get; set; }

    public IReadOnlyList<RunningApp>? ListApps() => Broken ? null : [.. Running.Where(r => r.Value > 0).Select(r => new RunningApp(r.Key, r.Key + " window"))];

    public int? Count(string exeName) => Broken ? null : Running.GetValueOrDefault(exeName);

    public int RequestClose(string exeName)
    {
        CloseRequests.Add(exeName);
        if (ClosesOnRequest.Contains(exeName)) Running[exeName] = 0;
        return 1;
    }

    public int Kill(string exeName)
    {
        Kills.Add(exeName);
        if (!SurvivesKill.Contains(exeName)) Running[exeName] = 0;
        return 1;
    }
}

internal sealed class FakeRunner : IProcessRunner
{
    public List<(string Exe, IReadOnlyList<string> Args)> Runs { get; } = [];
    public Func<string, IReadOnlyList<string>, ProcessResult> Answer { get; set; } = (_, _) => new ProcessResult(0, ["ok"], false);

    public Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, IReadOnlyDictionary<string, string>? env = null)
    {
        Runs.Add((exe, args));
        return Task.FromResult(Answer(exe, args));
    }
}

internal sealed class FakeTaskScheduler : ITaskScheduler
{
    public Dictionary<string, bool> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> AdminOnly { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Path, bool Enabled)> DirectWrites { get; } = [];

    public Task<ResourceRead> ReadEnabledAsync(string path, CancellationToken ct) => Task.FromResult(
        Tasks.TryGetValue(path, out bool e) ? ResourceRead.Of(JsonValue.Create(e)) : ResourceRead.Failed($"task '{path}' cannot be read: not found"));

    public Task<string?> SetEnabledAsync(string path, bool enabled, CancellationToken ct)
    {
        DirectWrites.Add((path, enabled));
        if (AdminOnly.Contains(path)) return Task.FromResult<string?>("schtasks /change: access is denied");
        Tasks[path] = enabled;
        return Task.FromResult<string?>(null);
    }

    public Task<IReadOnlyList<string>?> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>([.. Tasks.Keys]);
}
