using System.Runtime.Versioning;
using BluettiWatcher.Decision;
using BluettiWatcher.Io;
using BluettiWatcher.PowerMode;
using BluettiWatcher.Storage;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

/// <summary>What keeps a mode change from stalling the service unseen: flag reads off the thread pool, a long-held gate logged, a bounded drain.</summary>
public sealed class ModeStallTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-watcher-test-" + Guid.NewGuid().ToString("N"));
    private readonly StationStore _store;
    private readonly StationConfig _cfg;
    private readonly List<string> _log = [];

    public ModeStallTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new StationStore(Path.Combine(_dir, "station.db"));
        _cfg = new StationConfig { DataDir = _dir, FlagPath = Path.Combine(_dir, "flag", "power-mode.json") };
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Records whether each read ran on a thread-pool thread (the real read may block on the writers' mutex for seconds).</summary>
    private sealed class PoolProbeFlag : IFlagReader
    {
        public System.Collections.Concurrent.ConcurrentQueue<bool> OnPool { get; } = new();
        public PowerModeFlag? Read(DateTimeOffset now)
        {
            OnPool.Enqueue(Thread.CurrentThread.IsThreadPoolThread);
            return PowerModeFlag.Normal;
        }
    }

    /// <summary>A clock whose monotonic time moves only when the test says so.</summary>
    private sealed class StepClock(DateTimeOffset start) : TimeProvider
    {
        private long _ticks;
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(3), "t", "t");
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan by) { Interlocked.Add(ref _ticks, by.Ticks); Now += by; }
    }

    /// <summary>Commands that take <see cref="Takes"/> on the step clock, as a power-mode call that hangs.</summary>
    private sealed class SlowActions(StepClock clock) : IPowerModeActions
    {
        public TimeSpan Takes { get; set; }
        private Task<ActionResult> R(string name) { clock.Advance(Takes); return Task.FromResult(new ActionResult(true, 0, name, "ok")); }
        public Task<ActionResult> StationEcoAsync(CancellationToken ct) => R("eco");
        public Task<ActionResult> StationEmergencyAsync(CancellationToken ct) => R("on");
        public Task<ActionResult> LiftAsync(CancellationToken ct) => R("off");
        public Task<ActionResult> BatteryAsync(BatteryReport battery, double hibernateAt, CancellationToken ct) => R("battery");
    }

    [Fact]
    public async Task Service_flag_reads_never_block_a_thread_pool_thread()
    {
        var flag = new PoolProbeFlag();
        using var service = new StationService(_cfg, new QueueReader(), new FakeActions(), new FakeHibernator(true), flag, _store, new ManualClock(T0), _log.Add);
        var ct = TestContext.Current.CancellationToken;
        await service.PollAsync(ct);
        await service.SetModeAsync(ManualMode.Max, ct);
        await service.ApplyPendingAsync(ct);
        Assert.NotEmpty(flag.OnPool);
        Assert.DoesNotContain(true, flag.OnPool);
    }

    [Theory]
    [InlineData(40, true)]
    [InlineData(5, false)]
    public async Task ApplyPending_gate_held_long_is_logged(int seconds, bool logged)
    {
        var clock = new StepClock(T0);
        var actions = new SlowActions(clock) { Takes = TimeSpan.FromSeconds(seconds) };
        using var service = new StationService(_cfg, new QueueReader(), actions, new FakeHibernator(true), new FixedFlag(Flag()), _store, clock, _log.Add);
        var ct = TestContext.Current.CancellationToken;
        await service.SetModeAsync(ManualMode.Max, ct);
        await service.ApplyPendingAsync(ct);
        Assert.Equal(logged, _log.Contains($"slow: the mode change held the station gate for {seconds} s"));
    }

    // drainMs 1: the drain wait ends before the reader thread has delivered the line; the kill must not lose it.
    [Theory]
    [InlineData(500)]
    [InlineData(1)]
    [SupportedOSPlatform("windows")]
    public async Task RunTimed_tool_that_exits_while_a_child_keeps_its_output_open_returns_with_its_output(int drainMs)
    {
        // cmd exits at once; the ping it started in the background keeps the output pipe open for about 30 s
        string cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var run = Task.Run(() => PowerModeSwitch.RunTimed(cmd, ["/c", "start /b ping -n 30 127.0.0.1 >nul & echo started"], 10000, drainMs),
            TestContext.Current.CancellationToken);
        var r = await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.Equal(0, r.Code);
        Assert.Contains("started", r.Out);
    }
}
