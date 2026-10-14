using BluettiWatcher.Decision;
using BluettiWatcher.Storage;
using Microsoft.Data.Sqlite;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

/// <summary>What station.db costs on disk: page size, WAL bytes per poll, WAL file size, the layout upgrade.</summary>
public sealed class StationStoreDiskTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-watcher-test-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "station.db");

    public StationStoreDiskTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static Reading Steady => new(100, 350, 220, "BYPASS_OUTPUT", 0, 0, 230.1, 50.0, "cloud");

    /// <summary>One poll's writes as the service makes them: the reading, then the decision state.</summary>
    private static void Poll(StationStore s, StationState state, DateTimeOffset at)
    {
        s.AddReading(at, Steady, ReadingStatus.Accepted, "");
        state.LastAt = at;
        state.LastSoc = 100;
        s.SaveState(state);
    }

    private void Checkpoint()
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        using var r = cmd.ExecuteReader();
        r.Read();
        Assert.Equal(0L, r.GetInt64(0));   // not busy: the checkpoint ran
    }

    private long WalBytes() => new FileInfo(DbPath + "-wal") is { Exists: true } f ? f.Length : 0;

    [Fact]
    public void A_new_database_uses_1_KB_pages_wal_and_a_capped_wal_file()
    {
        using var s = new StationStore(DbPath);
        // synchronous 1 = NORMAL: a commit appends to the WAL and is not flushed.
        Assert.Equal((1024L, StationStore.WalLimitBytes, 1L, "wal"), s.Layout());
    }

    [Fact]
    public void A_steady_poll_appends_under_4_KB_to_the_wal()
    {
        using var s = new StationStore(DbPath);
        var state = new StationState();
        var t = T0;
        for (int i = 0; i < 3000; i++) Poll(s, state, t = t.AddMinutes(1));   // two days: a real tree depth
        Checkpoint();
        const int polls = 200;
        for (int i = 0; i < polls; i++) Poll(s, state, t = t.AddMinutes(1));
        double perPoll = (double)WalBytes() / polls;
        Assert.True(perPoll < 4096, $"WAL bytes per poll {perPoll:0}");
    }

    [Fact]
    public void The_wal_file_stays_near_1_MB_however_long_the_service_runs()
    {
        using var s = new StationStore(DbPath);
        var state = new StationState();
        var t = T0;
        for (int i = 0; i < 800; i++) Poll(s, state, t = t.AddMinutes(1));   // past one auto-checkpoint
        Assert.True(WalBytes() <= 1_150_000, $"WAL {WalBytes()} bytes");
    }

    /// <summary>The main file as it is on disk (SQLite may hold it open).</summary>
    private byte[] MainFile()
    {
        using var f = new FileStream(DbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var ms = new MemoryStream();
        f.CopyTo(ms);
        return ms.ToArray();
    }

    private static string Cs(string path) => new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();

    private static long Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
#pragma warning disable CA2100 // test-only: every caller passes a literal
        cmd.CommandText = sql;
#pragma warning restore CA2100
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A 4 KB-page WAL database as the previous version made it; the returned connection keeps its frames in the -wal.</summary>
    private SqliteConnection Old4KbDatabaseWithWal(int rows)
    {
        var c = new SqliteConnection(Cs(DbPath));
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA page_size=4096; PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;
            CREATE TABLE reading(at INTEGER NOT NULL, status INTEGER NOT NULL, soc REAL, in_ac REAL, in_dc REAL,
              out_ac REAL, out_dc REAL, volt REAL, hz REAL, mode TEXT, reason TEXT);
            CREATE INDEX reading_at ON reading(at);
            CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            INSERT INTO meta VALUES('k','v');
            """;
        cmd.ExecuteNonQuery();
        for (int i = 0; i < rows; i++)
        {
            cmd.CommandText = "INSERT INTO reading(at,status,soc) VALUES($at,0,$soc)";
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$at", 1_000_000 + i * 60);
            cmd.Parameters.AddWithValue("$soc", 50 + i % 40);
            cmd.ExecuteNonQuery();
        }
        return c;
    }

    [Fact]
    public void An_existing_4_KB_database_with_a_non_empty_wal_is_opened_as_it_is()
    {
        using var writer = Old4KbDatabaseWithWal(300);
        // A process that is not SQLite (backup, antivirus, indexer) holds the -wal open: SQLite cannot delete it.
        using var holder = new FileStream(DbPath + "-wal", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Assert.True(WalBytes() > 0);
        byte[] before = MainFile();

        using (var s = new StationStore(DbPath))
        {
            Assert.Equal((4096L, StationStore.WalLimitBytes, 1L, "wal"), s.Layout());
            Assert.Equal((300, 0), s.Counts());
            Assert.Equal("v", s.GetMeta("k"));
            s.AddReading(T0, Bat(80), ReadingStatus.Accepted, "");
            Assert.Equal((301, 0), s.Counts());
        }
        // Opening and writing touched the WAL only; the main file is byte for byte what it was.
        Assert.Equal(before, MainFile());
        Assert.Equal(301L, Scalar(writer, "SELECT COUNT(*) FROM reading"));
        using var check = new SqliteConnection(Cs(DbPath));
        check.Open();
        using var ic = check.CreateCommand();
        ic.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", (string)ic.ExecuteScalar()!);
        Assert.Equal(4096L, Scalar(check, "PRAGMA page_size"));
        Assert.Equal(301L, Scalar(check, "SELECT COUNT(*) FROM reading"));
    }

    [Fact]
    public void An_existing_database_is_opened_without_waiting_when_other_connections_read_or_write()
    {
        using (var old = new StationStore(DbPath)) old.AddReading(T0, Bat(80), ReadingStatus.Accepted, "");
        using var reader = new SqliteConnection(Cs(DbPath));
        reader.Open();
        using var tx = reader.BeginTransaction(deferred: true);
        Assert.Equal(1L, Scalar(reader, "SELECT COUNT(*) FROM reading"));   // a read transaction stays open
        using var writer = new SqliteConnection(Cs(DbPath));
        writer.Open();
        using var wtx = writer.BeginTransaction();
        using (var w = writer.CreateCommand())
        {
            w.Transaction = wtx;
            w.CommandText = "INSERT INTO event VALUES(1,'x','y')";   // and a write lock is held
            w.ExecuteNonQuery();
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var s = new StationStore(DbPath);
        Assert.True(sw.ElapsedMilliseconds < 2000, $"open took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(1, s.Counts().Raw);
        Assert.Equal("wal", s.Layout().Journal);
    }

    [Fact]
    public void An_existing_1_KB_database_keeps_its_layout_and_rows_across_opens()
    {
        using (var s = new StationStore(DbPath)) s.AddReading(T0, Bat(80), ReadingStatus.Accepted, "");
        using var again = new StationStore(DbPath);
        Assert.Equal((1024L, StationStore.WalLimitBytes, 1L, "wal"), again.Layout());
        Assert.Equal(1, again.Counts().Raw);
    }

    /// <summary>
    /// The statements that change an existing file's layout (journal mode, page size, VACUUM) must not be reachable for it:
    /// the only ones left run for a new file. The source is the check because SQLite offers no hook on a pragma here.
    /// </summary>
    [Fact]
    public void The_store_issues_no_layout_statement_except_for_a_new_file()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "BluettiWatcher", "Storage", "StationStore.cs"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string source = File.ReadAllText(Path.Combine(dir.FullName, "src", "BluettiWatcher", "Storage", "StationStore.cs"));
        var code = string.Join('\n', source.Split('\n').Select(l => l.TrimStart()).Where(l => !l.StartsWith("//", StringComparison.Ordinal)));
        Assert.DoesNotContain("VACUUM", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LayoutError", code, StringComparison.Ordinal);
        Assert.DoesNotContain("legacyPages", code, StringComparison.Ordinal);
        // Every journal_mode / page_size statement sits on the line inside the isNew branch.
        // (reads such as "PRAGMA page_size;" carry no "=" and change nothing)
        var layoutLines = code.Split('\n').Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"(journal_mode|page_size)\s*=", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToArray();
        Assert.Single(layoutLines);
        Assert.Contains("PRAGMA page_size=1024; PRAGMA journal_mode=WAL;", layoutLines[0], StringComparison.Ordinal);
        int at = code.IndexOf(layoutLines[0], StringComparison.Ordinal);
        Assert.Contains("if (isNew)", code[..at][^200..], StringComparison.Ordinal);
        // wal_autocheckpoint and journal_size_limit are per-connection and not switched by any other statement.
        Assert.DoesNotContain("journal_mode=DELETE", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("journal_mode=OFF", code, StringComparison.OrdinalIgnoreCase);
    }
}