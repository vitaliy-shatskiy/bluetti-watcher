using BluettiWatcher.Io;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests;

public sealed class FileLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-watcher-test-" + Guid.NewGuid().ToString("N"));
    private readonly TimerClock _clock = new(T0);
    private string LogPath => Path.Combine(_dir, "service.log");

    public FileLogTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string[] Lines() => File.ReadAllText(LogPath).TrimEnd('\n').Split('\n');

    private const string Steady = "reading accepted from the cloud: battery 100 %";

    [Fact]
    public void Distinct_lines_are_written_each_with_its_time()
    {
        using var log = new FileLog(LogPath, _clock);
        log.Write("started");
        _clock.Now = T0.AddMinutes(1);
        log.Write("reading accepted from the cloud: battery 99 %");
        Assert.Equal(["2026-10-06T20:00:00+03:00  started", "2026-10-06T20:01:00+03:00  reading accepted from the cloud: battery 99 %"], Lines());
    }

    [Fact]
    public void Identical_lines_fold_into_one_count_line_written_before_the_next_different_line()
    {
        using var log = new FileLog(LogPath, _clock);
        for (int m = 0; m < 5; m++) { _clock.Now = T0.AddMinutes(m); log.Write(Steady); }
        _clock.Now = T0.AddMinutes(5).AddSeconds(30);
        log.Write("no answer from the station (miss 1)");
        Assert.Equal([
            "2026-10-06T20:00:00+03:00  " + Steady,
            "2026-10-06T20:04:00+03:00  repeated 4 times since 20:01:00: " + Steady,
            "2026-10-06T20:05:30+03:00  no answer from the station (miss 1)"], Lines());
    }

    [Fact]
    public void A_single_repeat_is_written_as_the_line_itself_at_its_time()
    {
        using var log = new FileLog(LogPath, _clock);
        log.Write(Steady);
        _clock.Now = T0.AddMinutes(1);
        log.Write(Steady);
        _clock.Now = T0.AddMinutes(2);
        log.Write("other");
        Assert.Equal(["2026-10-06T20:00:00+03:00  " + Steady, "2026-10-06T20:01:00+03:00  " + Steady, "2026-10-06T20:02:00+03:00  other"], Lines());
    }

    [Fact]
    public void A_steady_line_still_reaches_the_file_every_15_minutes()
    {
        using var log = new FileLog(LogPath, _clock);
        for (int m = 0; m <= 45; m++) { _clock.Now = T0.AddMinutes(m); log.Write(Steady); }
        Assert.Equal([
            "2026-10-06T20:00:00+03:00  " + Steady,
            "2026-10-06T20:15:00+03:00  repeated 15 times since 20:01:00: " + Steady,
            "2026-10-06T20:30:00+03:00  repeated 15 times since 20:16:00: " + Steady,
            "2026-10-06T20:45:00+03:00  repeated 15 times since 20:31:00: " + Steady], Lines());
    }

    [Fact]
    public void Every_distinct_event_between_steady_lines_is_kept_in_order()
    {
        using var log = new FileLog(LogPath, _clock);
        string[] events = ["eco-on: pwsh eco", "mode: max", "action-failed: x", "hibernate: refused - y"];
        int m = 0;
        foreach (var e in events)
        {
            for (int i = 0; i < 3; i++) { _clock.Now = T0.AddMinutes(m++); log.Write(Steady); }
            _clock.Now = T0.AddMinutes(m++);
            log.Write(e);
        }
        var texts = Lines().Select(l => l[27..]).ToArray();
        Assert.Equal(events, texts.Where(t => !t.Contains("battery", StringComparison.Ordinal)).ToArray());
        // Each run of three: the first line, then "repeated 2 times".
        Assert.Equal(4, texts.Count(t => t.StartsWith("repeated 2 times since", StringComparison.Ordinal)));
    }

    [Fact]
    public void Flush_writes_a_pending_count_and_nothing_when_none_is_pending()
    {
        using var log = new FileLog(LogPath, _clock);
        for (int m = 0; m < 3; m++) { _clock.Now = T0.AddMinutes(m); log.Write(Steady); }
        log.Flush();
        log.Flush();
        Assert.Equal(["2026-10-06T20:00:00+03:00  " + Steady, "2026-10-06T20:02:00+03:00  repeated 2 times since 20:01:00: " + Steady], Lines());
    }

    [Fact]
    public void The_log_rolls_to_service_log_1_past_its_cap_and_never_holds_more_than_two_files()
    {
        using var log = new FileLog(LogPath, _clock, rollBytes: 1000);
        for (int i = 0; i < 100; i++) { _clock.Now = T0.AddMinutes(i); log.Write($"event {i}"); }
        Assert.True(new FileInfo(LogPath).Length <= 1000 + 40);
        Assert.True(new FileInfo(LogPath + ".1").Length <= 1000 + 40);
        Assert.Equal(["service.log", "service.log.1"], Directory.GetFiles(_dir).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray());
        Assert.EndsWith("  event 99", Lines()[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void The_first_occurrence_of_a_line_reaches_the_file_at_once()
    {
        using var log = new FileLog(LogPath, _clock);
        log.Write("started");
        Assert.Equal(["2026-10-06T20:00:00+03:00  started"], Lines());
        log.Write(Steady);
        Assert.Equal(["2026-10-06T20:00:00+03:00  started", "2026-10-06T20:00:00+03:00  " + Steady], Lines());
    }

    [Fact]
    public void A_pending_count_is_written_by_the_timer_without_another_write()
    {
        using var log = new FileLog(LogPath, _clock);
        log.Write(Steady);
        for (int m = 1; m <= 5; m++) { _clock.Now = T0.AddMinutes(m); log.Write(Steady); }
        Assert.Equal(["2026-10-06T20:00:00+03:00  " + Steady], Lines());   // 5 repeats pending, nothing else is written
        Assert.Equal(FileLog.RepeatFlush, _clock.Timer.Due);               // armed when the count began
        _clock.Now = T0.AddMinutes(1).Add(FileLog.RepeatFlush);
        _clock.Timer.Fire();                                               // the process is killed after this: nothing else runs
        Assert.Equal(["2026-10-06T20:00:00+03:00  " + Steady, "2026-10-06T20:05:00+03:00  repeated 5 times since 20:01:00: " + Steady], Lines());
    }

    [Fact]
    public void A_stale_timer_does_not_write_a_newer_count_early_and_re_arms()
    {
        using var log = new FileLog(LogPath, _clock);
        log.Write(Steady);
        _clock.Now = T0.AddMinutes(1);
        log.Write(Steady);
        log.Write("other");   // takes the count; the timer stays armed
        _clock.Now = T0.AddMinutes(10);
        log.Write("other");   // a new count begins at 10:00 of the run
        _clock.Now = T0.AddMinutes(16);
        _clock.Timer.Fire();  // the first timer: the new count is 6 minutes old
        Assert.Equal(3, Lines().Length);
        Assert.Equal(TimeSpan.FromMinutes(9), _clock.Timer.Due);
        _clock.Now = T0.AddMinutes(25);
        _clock.Timer.Fire();
        Assert.EndsWith("  other", Lines()[^1], StringComparison.Ordinal);
        Assert.Equal(4, Lines().Length);
    }

    [Fact]
    public void Dispose_writes_a_pending_count_and_stops_the_timer()
    {
        var log = new FileLog(LogPath, _clock);
        log.Write(Steady);
        _clock.Now = T0.AddMinutes(1);
        log.Write(Steady);
        _clock.Now = T0.AddMinutes(2);
        log.Write(Steady);
        log.Dispose();
        log.Dispose();
        Assert.True(_clock.Timer.Disposed);
        Assert.EndsWith("repeated 2 times since 20:01:00: " + Steady, Lines()[^1], StringComparison.Ordinal);
        int count = Lines().Length;
        _clock.Timer.Fire();
        Assert.Equal(count, Lines().Length);
    }

    [Fact]
    public void A_disposed_log_is_not_kept_alive_by_the_process_exit_event()
    {
        WeakReference weak = Create();
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Assert.False(weak.IsAlive);

        WeakReference Create()
        {
            var log = new FileLog(LogPath, new TimerClock(T0));
            log.Write("x");
            log.Dispose();
            return new WeakReference(log);
        }
    }

    /// <summary>A clock with one manually fired timer.</summary>
    private sealed class TimerClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public FakeTimer Timer { get; private set; } = null!;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone { get; } = TimeZoneInfo.CreateCustomTimeZone("t", TimeSpan.FromHours(3), "t", "t");
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Timer = new FakeTimer(callback, state, dueTime);
    }

    private sealed class FakeTimer(TimerCallback callback, object? state, TimeSpan due) : ITimer
    {
        public TimeSpan Due { get; private set; } = due;
        public bool Disposed { get; private set; }
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) { Due = dueTime; return !Disposed; }
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}