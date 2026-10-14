using BluettiWatcher.Decision;
using BluettiWatcher.Io;
using BluettiWatcher.Storage;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

/// <summary>service.log stays small - repeated lines are thinned, never the events.</summary>
public sealed class LogVolumeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-logvol-test-" + Guid.NewGuid().ToString("N"));
    private readonly StationStore _store;
    private readonly List<string> _log = [];

    public LogVolumeTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new StationStore(Path.Combine(_dir, "station.db"));
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Misses_log_the_first_the_turn_to_silent_and_every_15th()
    {
        var clock = new ManualClock(T0);
        var cfg = new StationConfig { DataDir = _dir, SilentAfter = 3 };
        using var s = new StationService(cfg, new QueueReader(), new FakeActions(), new FakeHibernator(true), new FixedFlag(FlagView.Normal), _store, clock, _log.Add);
        for (int m = 0; m < 31; m++)
        {
            clock.Now = T0.AddMinutes(m);
            await s.PollAsync(CancellationToken.None);
        }
        var misses = _log.Where(l => l.StartsWith("no answer from the station", StringComparison.Ordinal)).ToList();
        Assert.Equal(
        [
            "no answer from the station (miss 1)",
            "no answer from the station (miss 3) - station silent",
            "no answer from the station (miss 15) - station silent",
            "no answer from the station (miss 30) - station silent",
        ], misses);
        // Every miss is still a row: the page and the events keep all of them.
        Assert.Equal(31, _store.Events(0, long.MaxValue).Count(e => e.Kind == "missed"));
    }

    [Fact]
    public async Task Power_mode_line_names_the_mode_exit_and_duration_without_paths()
    {
        var cfg = new StationConfig { DataDir = _dir, PowerMode = new PowerModeConfig { Script = @"C:\very\long\path\power-mode\power-mode.ps1", FlagPath = @"C:\ProgramData\BluettiWatcher\power-mode.json" } };
        var adapter = new PowerModeAdapter(cfg, new FakeRunner { Lines = ["power mode: eco"] }, _log.Add);
        var r = await adapter.StationEcoAsync(CancellationToken.None);
        Assert.True(r.Ok);
        var line = Assert.Single(_log);
        Assert.Matches(@"^power-mode eco -> exit 0 in \d+ ms$", line);
        // The failure event keeps the full command line.
        Assert.Contains("power-mode.ps1", r.CommandLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Power_mode_failure_line_keeps_the_last_output_line()
    {
        var cfg = new StationConfig { DataDir = _dir };
        var adapter = new PowerModeAdapter(cfg, new FakeRunner { Exit = 3, Lines = ["power mode: refused - emergency"] }, _log.Add);
        await adapter.LiftAsync(CancellationToken.None);
        Assert.Matches(@"^power-mode off -> exit 3 in \d+ ms: power mode: refused - emergency$", Assert.Single(_log));
    }

    [Fact]
    public async Task Power_mode_line_in_process_names_the_verb_not_the_flag_path()
    {
        var cfg = new StationConfig { DataDir = _dir };
        var runner = new FakeSwitchRunner();
        var adapter = new PowerModeAdapter(cfg, new FakeRunner(), _log.Add, runner);
        Assert.True(adapter.InProcess);
        Assert.True((await adapter.StationEcoAsync(CancellationToken.None)).Ok);
        Assert.True((await adapter.LiftAsync(CancellationToken.None)).Ok);
        Assert.True((await adapter.BatteryAsync(new BatteryReport(19.5, Level.Low, false), 5, CancellationToken.None)).Ok);
        Assert.Equal(3, _log.Count);
        Assert.Matches(@"^power-mode eco -> exit 0 in \d+ ms$", _log[0]);
        Assert.Matches(@"^power-mode off -> exit 0 in \d+ ms$", _log[1]);
        Assert.Matches(@"^power-mode battery -> exit 0 in \d+ ms$", _log[2]);
    }

    [Fact]
    public async Task A_power_mode_run_that_timed_out_is_logged_as_still_running_not_as_a_failure()
    {
        var cfg = new StationConfig { DataDir = _dir };
        var adapter = new PowerModeAdapter(cfg, new FakeRunner(), _log.Add, new FakeSwitchRunner { Result = new ProcessResult(-1, [], true) });
        var r = await adapter.StationEmergencyAsync(CancellationToken.None);
        Assert.False(r.Ok);
        var line = Assert.Single(_log);
        Assert.Matches(@"^power-mode on -> no answer in \d+ ms, still running \(it may yet write the flag\)$", line);
        Assert.DoesNotContain("exit -1", line, StringComparison.Ordinal);
    }

    private sealed class FakeSwitchRunner : BluettiWatcher.PowerMode.IPowerModeRunner
    {
        public ProcessResult Result { get; init; } = new(0, ["power mode: fake"], false);

        public Task<ProcessResult> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, IReadOnlyDictionary<string, string>? env = null) =>
            Task.FromResult(Result);
    }
}
