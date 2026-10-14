using System.Diagnostics;
using BluettiWatcher.Io;
using BluettiWatcher.Storage;
using Microsoft.Data.Sqlite;

namespace BluettiWatcher.Tests;

/// <summary>Memory and disk: known folders without the shell, the memory calls after each poll, SQLite through Windows' own library.</summary>
public sealed class MemoryLeverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-watcher-test-" + Guid.NewGuid().ToString("N"));

    public MemoryLeverTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    // --- known folders -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_known_folder_comes_from_its_environment_variable()
    {
        int shellCalls = 0;
        string got = KnownFolders.Resolve("LOCALAPPDATA", Environment.SpecialFolder.LocalApplicationData,
            name => name == "LOCALAPPDATA" ? @"D:\Profiles\u\AppData\Local" : null,
            _ => { shellCalls++; return @"C:\from-shell"; });
        Assert.Equal(@"D:\Profiles\u\AppData\Local", got);
        Assert.Equal(0, shellCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"relative\dir")]
    [InlineData(@"\no-drive")]
    public void A_missing_or_unusable_variable_falls_back_to_the_shell_call(string? value)
    {
        Environment.SpecialFolder? asked = null;
        string got = KnownFolders.Resolve("ProgramData", Environment.SpecialFolder.CommonApplicationData, _ => value, f => { asked = f; return @"C:\from-shell"; });
        Assert.Equal(@"C:\from-shell", got);
        Assert.Equal(Environment.SpecialFolder.CommonApplicationData, asked);
    }

    [Fact]
    public void The_default_paths_are_the_ones_the_shell_gives()
    {
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), KnownFolders.LocalAppData, ignoreCase: true);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), KnownFolders.ProgramData, ignoreCase: true);
        var cfg = new StationConfig();
        Assert.Equal(Path.Combine(KnownFolders.LocalAppData, "bluetti-watcher"), cfg.DataDir);
        Assert.Equal(Path.Combine(KnownFolders.ProgramData, "BluettiWatcher", "power-mode.json"), cfg.FlagFile);
    }

    // --- trimWorkingSet ----------------------------------------------------------------------------------------------

    [Fact]
    public void TrimWorkingSet_is_on_by_default_and_config_json_turns_it_off()
    {
        Assert.True(new StationConfig().TrimWorkingSet);
        string on = Path.Combine(_dir, "on.json");
        File.WriteAllText(on, "{\"mac\":\"AA:BB:CC:DD:EE:FF\"}");
        Assert.True(StationConfig.Load(["--config", on, "--data-dir", _dir]).TrimWorkingSet);
        string off = Path.Combine(_dir, "off.json");
        File.WriteAllText(off, "{\"mac\":\"AA:BB:CC:DD:EE:FF\",\"trimWorkingSet\":false}");
        Assert.False(StationConfig.Load(["--config", off, "--data-dir", _dir]).TrimWorkingSet);
    }

    // --- after each poll ---------------------------------------------------------------------------------------------

    private sealed class FakeCalls : IMemoryCalls
    {
        public List<string> Made { get; } = [];
        public int CompactError { get; init; }
        public int OptimizeError { get; init; }
        public int TrimError { get; init; }
        public int HeapCompact() { Made.Add("compact"); return CompactError; }
        public int HeapOptimizeResources() { Made.Add("optimize"); return OptimizeError; }
        public int TrimWorkingSet() { Made.Add("trim"); return TrimError; }
    }

    [Fact]
    public void Each_poll_compacts_the_heap_then_trims_the_working_set()
    {
        var calls = new FakeCalls();
        var log = new List<string>();
        var memory = new ProcessMemory(new StationConfig(), calls, log.Add);
        memory.AfterPoll();
        memory.AfterPoll();
        Assert.Equal(["compact", "optimize", "trim", "compact", "optimize", "trim"], calls.Made);
        Assert.Empty(log);
    }

    [Fact]
    public void TrimWorkingSet_false_keeps_the_heap_calls_and_makes_no_trim()
    {
        var calls = new FakeCalls();
        var memory = new ProcessMemory(new StationConfig { TrimWorkingSet = false }, calls, _ => { });
        memory.AfterPoll();
        memory.AfterPoll();
        Assert.Equal(["compact", "optimize", "compact", "optimize"], calls.Made);
    }

    [Fact]
    public void A_failing_call_is_logged_once_never_throws_and_is_still_made_each_poll()
    {
        var calls = new FakeCalls { CompactError = 87, TrimError = 5 };
        var log = new List<string>();
        var memory = new ProcessMemory(new StationConfig(), calls, log.Add);
        for (int i = 0; i < 3; i++) memory.AfterPoll();
        Assert.Equal(9, calls.Made.Count);
        Assert.Equal(2, log.Count);
        Assert.Contains(log, l => l.Contains("HeapCompact", StringComparison.Ordinal) && l.Contains("87", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Contains("working set", StringComparison.Ordinal) && l.Contains('5', StringComparison.Ordinal));
    }

    [Fact]
    public void The_Windows_calls_succeed_on_this_process()
    {
        var calls = new Win32MemoryCalls();
        Assert.Equal(0, calls.HeapCompact());
        Assert.Equal(0, calls.HeapOptimizeResources());
        Assert.Equal(0, calls.TrimWorkingSet());
    }

    // --- SQLite ------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_Windows_SQLite_older_than_3_24_is_refused_with_one_line_and_exit_3()
    {
        var log = new List<string>();
        int? exit = SqliteSetup.StartGate(() => 3_023_002, () => "3.23.2", log.Add);
        Assert.Equal(3, exit);
        Assert.Equal(SqliteSetup.ExitTooOld, exit);
        string line = Assert.Single(log);
        Assert.StartsWith("DID NOT RUN - ", line, StringComparison.Ordinal);
        Assert.Contains("3.23.2", line, StringComparison.Ordinal);
        Assert.Contains("3.24.0", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3_024_000, "3.24.0")]
    [InlineData(3_051_001, "3.51.1")]
    public void SQLite_3_24_or_later_passes_the_start_check_silently(int number, string version)
    {
        var log = new List<string>();
        Assert.Null(SqliteSetup.StartGate(() => number, () => version, log.Add));
        Assert.Empty(log);
    }

    [Fact]
    public void A_missing_winsqlite3_is_refused_not_thrown()
    {
        var log = new List<string>();
        int? exit = SqliteSetup.StartGate(() => throw new DllNotFoundException("winsqlite3"), () => "?", log.Add);
        Assert.Equal(SqliteSetup.ExitTooOld, exit);
        Assert.Contains("could not be loaded", Assert.Single(log), StringComparison.Ordinal);
    }

    [Fact]
    public void A_provider_that_fails_its_type_initializer_is_refused_not_thrown()
    {
        var log = new List<string>();
        int? exit = SqliteSetup.StartGate(
            () => throw new TypeInitializationException("BluettiWatcher.Storage.SqliteSetup", new DllNotFoundException("winsqlite3 missing")), () => "?", log.Add);
        Assert.Equal(SqliteSetup.ExitTooOld, exit);
        string line = Assert.Single(log);
        Assert.StartsWith("DID NOT RUN - ", line, StringComparison.Ordinal);
        Assert.Contains("could not be loaded", line, StringComparison.Ordinal);
        Assert.Contains("winsqlite3 missing", line, StringComparison.Ordinal);
    }

    [Fact]
    public void This_Windows_SQLite_passes_the_start_check()
    {
        var log = new List<string>();
        Assert.Null(SqliteSetup.StartGate(log.Add));
        Assert.Empty(log);
    }

    [Fact]
    public void The_store_runs_on_the_SQLite_Windows_ships_and_no_bundled_copy_is_loaded()
    {
        using (var store = new StationStore(Path.Combine(_dir, "station.db")))
        {
            store.AddEvent(DateTimeOffset.Now, "test", "winsqlite3");
            Assert.Contains(store.Events(0, long.MaxValue), e => e.Detail == "winsqlite3");
        }
        Assert.Equal("winsqlite3", SQLitePCL.raw.GetNativeLibraryName());
        var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Select(m => m.ModuleName).ToList();
        Assert.Contains(modules, m => string.Equals(m, "winsqlite3.dll", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(modules, m => string.Equals(m, "e_sqlite3.dll", StringComparison.OrdinalIgnoreCase));
    }
}
