using System.Text.Json.Nodes;
using BluettiWatcher.Modes;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests.Modes;

/// <summary>Compacting the journal drops history only: a replay of the compacted file owns what a replay of the whole one owns.</summary>
public sealed class ModeJournalCompactTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bw-modes-compact-").FullName;
    private readonly FakeMachine _m = new();
    private readonly ManualClock _clock = new(T0);
    private readonly ModuleRegistry _registry;

    public ModeJournalCompactTests()
    {
        _m.Values["kept"] = 300;
        _m.Values["churn"] = 0;
        _registry = new ModuleRegistry([new FakeModule("fake", _m)]);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ModeAction Set(string key, int value) => new() { TypeId = "fake", Settings = new JsonObject { ["key"] = key, ["value"] = value } };

    private static Mode ModeOf(string id, params ModeAction[] actions) => new() { Id = id, Name = id, FlagWord = FlagWord.Normal, Actions = actions };

    private static IReadOnlyList<string> Owned(ModeReconciler r) =>
        [.. r.Owned.Values.OrderBy(o => o.Key, StringComparer.Ordinal).Select(o => $"{o.Key} {o.ModeId} {o.Applied} {o.Before?.ToJsonString()} {o.Target?.ToJsonString()} {o.Written?.ToJsonString()} {o.HadPrior} {o.PriorWritten?.ToJsonString()} {o.RestoreTries}")];

    private ModeReconciler Replayed(string path)
    {
        var j = new ModeJournal(path);
        var load = j.Load();
        Assert.Null(load.Problem);
        var r = new ModeReconciler(_registry, j, _clock);
        r.Replay(load.Records, T0.AddDays(-1));
        return r;
    }

    [Fact]
    public async Task Compact_LongHistoryWithAnOldOwnedWriteAndAnOpenOne_ReplaysTheSameOwnership()
    {
        string path = Path.Combine(_dir, ModeJournal.FileName);
        var journal = new ModeJournal(path);
        journal.Load();
        var r = new ModeReconciler(_registry, journal, _clock);
        var ct = TestContext.Current.CancellationToken;
        // "kept" is written once, first, and stays owned through hundreds of mode changes that write and restore "churn".
        var a = ModeOf("a", Set("kept", 150), Set("churn", 1));
        var b = ModeOf("b", Set("kept", 150));
        for (int i = 0; i < 300; i++)
        {
            await r.ReconcileAsync(i % 2 == 0 ? a : b, ct);
            _clock.Now = _clock.Now.AddMinutes(1);
        }
        // An open write: "churn" prepared, then the process stops before Applied.
        _m.StopAfterWrite.Add("churn");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.ReconcileAsync(a, ct));
        var whole = Owned(Replayed(path));
        Assert.True(journal.Count > 2 * ModeJournal.KeepRecent);

        var j = new ModeJournal(path);
        Assert.Null(j.Load().Problem);
        Assert.True(j.Compact() > 0);

        var compacted = Replayed(path);
        Assert.Equal(whole, Owned(compacted));
        Assert.Equal("a", compacted.ActiveModeId);
        Assert.True((await File.ReadAllLinesAsync(path, ct)).Length <= ModeJournal.KeepRecent + 4);
    }
}
