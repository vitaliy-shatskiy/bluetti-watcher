using System.Text.Json;
using BluettiWatcher.DtekWatcher;
using BluettiWatcher.Storage;
using static BluettiWatcher.Tests.DtekWatcher.Dx;

namespace BluettiWatcher.Tests.DtekWatcher;

/// <summary>
/// A short mains loss. The station's cut is no AC input for mainsLostSec (180 s, design.md §5), so a loss of 180-300 s is a cut
/// (eco comes on, the module follows it); a shorter one is none. The module reads a finished cut only against planned
/// windows (§11 rules 3, 9, 25): one that matches no window of the house's queues neither counts nor contradicts and is no
/// lateness. The states are the real decisions of the readings, one per minute from 20:00 on 06.10.2026.
/// </summary>
public sealed class DtekWatcherShortCutTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bluetti-watcher-test-" + Guid.NewGuid().ToString("N"));
    private readonly StationStore _store;
    private readonly ManualClock _clock = new(At("2026-10-06 12:00"));
    private readonly FakeSource _source = new();

    public DtekWatcherShortCutTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new StationStore(Path.Combine(_dir, "station.db"));
    }

    public void Dispose()
    {
        _source.Dispose();
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>The planned windows of 06.10 are all in the small hours: A 00:00-02:00, B 03:00-04:00, C 05:00-06:00.</summary>
    private async Task<DtekWatcherService> WatcherWithPlan(string[] queues)
    {
        var c = Config(queues: queues);
        c.Site = false;
        var w = new DtekWatcherService(new StationConfig { DataDir = _dir, DtekWatcher = c }, () => _source, new DtekStore(_store), _clock, _ => { });
        _source.Datasets.Enqueue(FakeSource.Data(Updated("2026-10-06 11:43", "2026-10-06 11:45", "dataset", Day("2026-10-06", ("A", "1-2:0"), ("B", "4:0"), ("C", "6:0")))));
        await w.TickAsync(CancellationToken.None);
        return w;
    }

    private static object[] Outage(int offReadings) =>
        [.. Fx.Seq(new Fx.S(Fx.Grid(80), Dt: 0)), .. Enumerable.Range(0, offReadings).Select(_ => (object)new Fx.S(Fx.Bat(80), Dt: 60)),
         .. Enumerable.Range(0, 4).Select(_ => (object)new Fx.S(Fx.Grid(80), Dt: 60))];

    private bool OpenCutSaved()
    {
        using var doc = JsonDocument.Parse(_store.GetMeta("dtekWatcher")!);
        return doc.RootElement.TryGetProperty("openCut", out var open) && open.ValueKind == JsonValueKind.Object;
    }

    private List<DtekLate> Saved() => JsonSerializer.Deserialize(_store.GetMeta("dtekWatcherLateness") ?? "[]", StationJson.Default.ListDtekLate)!;

    // design.md §5 (cut = 180 s off) and §11 rules 3, 9, 25
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_4_minute_loss_in_no_planned_window_is_a_cut_the_module_follows_yet_it_counts_for_nothing(bool wholeHouse)
    {
        using var w = await WatcherWithPlan(wholeHouse ? House : ["A"]);
        // off readings at +60 .. +240 s (the loss lasts 240 s), mains back at +300 s, confirmed 180 s later
        var d = Fx.Step(Outage(4), cfg: Fx.Defaults());
        bool followed = false;
        for (int i = 0; i < d.Count; i++)
        {
            w.AfterPoll(d[i].State);
            if (i == 4)
            {
                Assert.True(d[4].State.Cut, "the loss reached mainsLostSec: a cut");
                followed |= OpenCutSaved();
            }
        }
        Assert.True(followed, "the module follows the station's cut from its first reading off mains");
        Assert.False(d[8].State.Cut);
        Assert.Equal(Fx.T0.AddSeconds(300), d[8].State.MainsBackAt);
        Assert.False(OpenCutSaved(), "the finished cut is handed over once");
        // it matches no planned window: no queue counted for or against, no lateness entry
        Assert.Equal((null, null, 0), (w.Match.Fixed, w.Match.Candidate, w.Match.Count));
        Assert.Empty(Saved());
    }

    // design.md §5: a loss under mainsLostSec is no cut, so the module never sees one
    [Fact]
    public async Task A_loss_under_180_s_is_no_cut_and_the_module_follows_nothing()
    {
        using var w = await WatcherWithPlan(["A"]);
        var d = Fx.Step(Outage(3), cfg: Fx.Defaults());   // off readings at +60 .. +180 s: 120 s off, then on
        foreach (var r in d)
        {
            Assert.False(r.State.Cut);
            w.AfterPoll(r.State);
            Assert.False(OpenCutSaved());
        }
        Assert.Empty(Saved());
    }
}
