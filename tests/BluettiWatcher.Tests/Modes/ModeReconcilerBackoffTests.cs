using System.Text.Json.Nodes;
using BluettiWatcher.Modes;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests.Modes;

/// <summary>A write that keeps failing is retried with a growing pause and alerted once per mode entry, not every poll.</summary>
public sealed class ModeReconcilerBackoffTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bw-modes-backoff-").FullName;
    private readonly FakeMachine _m = new();
    private readonly ManualClock _clock = new(T0);
    private readonly List<string> _events = [];
    private readonly ModeJournal _journal;
    private readonly ModeReconciler _reconciler;

    public ModeReconcilerBackoffTests()
    {
        _m.Values["gpu/0"] = 300;
        _m.FailWrite.Add("gpu/0");
        _journal = new ModeJournal(Path.Combine(_dir, ModeJournal.FileName));
        _journal.Load();
        _reconciler = new ModeReconciler(new ModuleRegistry([new FakeModule("gpu", _m)]), _journal, _clock, (k, _) => _events.Add(k));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Mode Gpu(string id, int value) => new()
    {
        Id = id, Name = id, FlagWord = FlagWord.Eco,
        Actions = [new ModeAction { TypeId = "gpu", Settings = new JsonObject { ["key"] = "gpu/0", ["value"] = value } }],
    };

    private async Task PollAsync(Mode mode, int polls)
    {
        for (int i = 0; i < polls; i++)
        {
            await _reconciler.ReconcileAsync(mode, TestContext.Current.CancellationToken);
            _clock.Now = _clock.Now.AddMinutes(1);
        }
    }

    [Fact]
    public async Task Reconcile_WriteFailsEveryMinuteForAnHour_BacksOffAndAlertsOnce()
    {
        await PollAsync(Gpu("eco", 150), 60);

        int lines = (await File.ReadAllLinesAsync(_journal.Path, TestContext.Current.CancellationToken)).Length;
        // Tries at 0, 1, 3, 7, 15, 31 minutes: a prepared and a dropped line each, plus the mode entered.
        Assert.InRange(lines, 1, 13);
        Assert.Equal(1, _events.Count(e => e == "action-failed"));
        Assert.Equal(ActionStatus.Failed, _reconciler.Snapshot.Actions.Single().Status);
    }

    [Fact]
    public async Task Reconcile_ModeChangesAfterAFailure_TriesAtOnceAndAlertsAgain()
    {
        await PollAsync(Gpu("eco", 150), 2);
        await PollAsync(Gpu("cool", 120), 1);

        Assert.Equal(2, _events.Count(e => e == "action-failed"));
        Assert.Equal(3, _m.Writes.Count(w => w == "gpu/0"));
    }

    [Fact]
    public async Task Reconcile_WriteSucceedsAfterABackoff_RetriesOnTimeAndClearsTheFailure()
    {
        await PollAsync(Gpu("eco", 150), 2);   // tries at 0 and 1; the next at 3
        _m.FailWrite.Clear();
        await PollAsync(Gpu("eco", 150), 1);   // minute 2: still waiting
        Assert.Equal(2, _m.Writes.Count(w => w == "gpu/0"));

        await PollAsync(Gpu("eco", 150), 1);   // minute 3: tried, written

        Assert.Equal(ActionStatus.Applied, _reconciler.Snapshot.Actions.Single().Status);
    }
}
