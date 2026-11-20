using System.Text.Json.Nodes;
using BluettiWatcher.Io;
using BluettiWatcher.Modes;

namespace BluettiWatcher.Tests.Modes;

public sealed class ModeReconcilerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bw-modes-rec-").FullName;
    private readonly FakeMachine _m = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly ModuleRegistry _registry;
    private readonly List<string> _events = [];

    public ModeReconcilerTests()
    {
        _registry = new ModuleRegistry([new FakeModule("plan", _m), new FakeModule("gpu", _m), new FakeModule("service", _m),
            new FakeModule("pause", _m, RestorationClass.Token), new FakeModule("close", _m, RestorationClass.Irreversible)]);
        _m.Values["plan/active"] = "high";
        _m.Values["gpu/0"] = 300;
        _m.Values["service/x"] = "running";
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string JournalPath => Path.Combine(_dir, ModeJournal.FileName);

    private ModeReconciler New()
    {
        var j = new ModeJournal(JournalPath);
        var load = j.Load();
        Assert.Null(load.Problem);
        var r = new ModeReconciler(_registry, j, _clock, (k, d) => _events.Add($"{k}: {d}"));
        r.Replay(load.Records, _clock.Now.AddDays(-1));
        return r;
    }

    private static ModeAction Act(string type, string key, JsonNode value, bool enforce = false) =>
        new() { TypeId = type, Enforce = enforce, Settings = new JsonObject { ["key"] = key, ["value"] = value } };

    private static Mode Eco(bool enforcePlan = false) => new()
    {
        Id = "eco", Name = "Eco", FlagWord = FlagWord.Eco,
        Actions = [Act("plan", "plan/active", "saver", enforcePlan), Act("gpu", "gpu/0", 150), Act("service", "service/x", "stopped")],
    };

    private static readonly Mode Normal = new() { Id = "normal", Name = "Normal" };

    [Fact]
    public async Task Reconcile_EcoThenNormal_RestoresPlanGpuAndService()
    {
        var r = New();
        await r.ReconcileAsync(Eco(), TestContext.Current.CancellationToken);
        Assert.Equal(["\"saver\"", "150", "\"stopped\""], [_m.Text("plan/active"), _m.Text("gpu/0"), _m.Text("service/x")]);
        Assert.All(r.Snapshot.Actions, a => Assert.Equal(ActionStatus.Applied, a.Status));

        await r.ReconcileAsync(Normal, TestContext.Current.CancellationToken);

        Assert.Equal(["\"high\"", "300", "\"running\""], [_m.Text("plan/active"), _m.Text("gpu/0"), _m.Text("service/x")]);
        Assert.Empty(r.Owned);
        Assert.Empty(r.Snapshot.Restores);
    }

    [Fact]
    public async Task Reconcile_AlreadyAtTarget_IsSatisfiedAndOwnsNothing()
    {
        _m.Values["plan/active"] = "saver";
        var r = New();
        await r.ReconcileAsync(Eco(), TestContext.Current.CancellationToken);
        Assert.Equal(ActionStatus.Satisfied, r.Snapshot.Actions[0].Status);
        Assert.False(r.Owned.ContainsKey("plan/active"));
        _m.Values["plan/active"] = "balanced";
        await r.ReconcileAsync(Normal, TestContext.Current.CancellationToken);
        Assert.Equal("\"balanced\"", _m.Text("plan/active"));   // never ours: never restored
    }

    [Fact]
    public async Task Reconcile_PlanChangedByHandDuringEco_SurvivesLeavingEco()
    {
        var r = New();
        await r.ReconcileAsync(Eco(), TestContext.Current.CancellationToken);
        _m.Values["plan/active"] = "balanced";

        await r.ReconcileAsync(Eco(), TestContext.Current.CancellationToken);   // the next poll sees the drift
        Assert.Equal(ActionStatus.Relinquished, r.Snapshot.Actions[0].Status);
        Assert.Equal("\"balanced\"", _m.Text("plan/active"));
        await r.ReconcileAsync(Eco(), TestContext.Current.CancellationToken);   // and leaves it alone afterwards
        Assert.Equal("\"balanced\"", _m.Text("plan/active"));

        await r.ReconcileAsync(Normal, TestContext.Current.CancellationToken);
        Assert.Equal("\"balanced\"", _m.Text("plan/active"));
        Assert.Equal("300", _m.Text("gpu/0"));
        Assert.Contains(_events, e => e.StartsWith("mode-drift: plan/active", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reconcile_HandChangeAtLeave_IsNotRolledBack()
    {
        var r = New();
        await r.ReconcileAsync(Eco(), TestContext.Current.CancellationToken);
        _m.Values["plan/active"] = "balanced";
        await r.ReconcileAsync(Normal, TestContext.Current.CancellationToken);
        Assert.Equal("\"balanced\"", _m.Text("plan/active"));
        Assert.Contains(r.Snapshot.Restores, s => s.Key == "plan/active" && s.Status == ActionStatus.Relinquished);
    }

    [Fact]
    public async Task Reconcile_EnforcedPlanChangedByHand_IsReappliedWithinOnePoll()
    {
        var r = New();
        await r.ReconcileAsync(Eco(enforcePlan: true), TestContext.Current.CancellationToken);
        _m.Values["plan/active"] = "balanced";

        await r.ReconcileAsync(Eco(enforcePlan: true), TestContext.Current.CancellationToken);

        Assert.Equal("\"saver\"", _m.Text("plan/active"));
        await r.ReconcileAsync(Normal, TestContext.Current.CancellationToken);
        Assert.Equal("\"high\"", _m.Text("plan/active"));   // the baseline is the one from before eco, not the hand value
    }

    [Fact]
    public async Task Reconcile_FailedRead_WritesNothingAndClaimsNothing()
    {
        _m.FailRead.Add("plan/active");
        var r = New();

        await r.ReconcileAsync(Eco(), TestContext.Current.CancellationToken);

        Assert.DoesNotContain("plan/active", _m.Writes);
        Assert.False(r.Owned.ContainsKey("plan/active"));
        Assert.Equal(ActionStatus.Failed, r.Snapshot.Actions[0].Status);
        // The independent actions went ahead.
        Assert.Equal([ActionStatus.Applied, ActionStatus.Applied], r.Snapshot.Actions.Skip(1).Select(a => a.Status));
    }

    [Fact]
    public async Task Reconcile_FailedReadOfOwnedResource_NeitherRestoresNorLetsGo()
    {
        var r = New();
        await r.ReconcileAsync(Eco(), TestContext.Current.CancellationToken);
        _m.FailRead.Add("plan/active");
        int writes = _m.Writes.Count;

        await r.ReconcileAsync(Normal, TestContext.Current.CancellationToken);

        Assert.Equal("\"saver\"", _m.Text("plan/active"));
        Assert.True(r.Owned.ContainsKey("plan/active"));   // still ours: retried, not dropped on a guess
        Assert.Equal(writes + 2, _m.Writes.Count);          // gpu and service restored, the plan not written
    }

    [Fact]
    public async Task Reconcile_RestoreFailing_IsRetriedThenReportedAndGivenUp()
    {
        var r = New();
        await r.ReconcileAsync(Eco(), TestContext.Current.CancellationToken);
        _m.FailWrite.Add("gpu/0");

        for (int i = 0; i < 6; i++) await r.ReconcileAsync(Normal, TestContext.Current.CancellationToken);

        Assert.Equal(ModeReconciler.MaxRestoreTries, _m.Writes.Count(w => w == "gpu/0") - 1);   // 1 = eco's own write
        var gpu = Assert.Single(r.Snapshot.Restores);
        Assert.Equal(ActionStatus.RestoreFailed, gpu.Status);
        Assert.False(r.Owned.ContainsKey("gpu/0"));
        Assert.Equal("\"high\"", _m.Text("plan/active"));
    }

    [Fact]
    public async Task Reconcile_FailedWrite_IsRetriedAfterItsPause()
    {
        _m.FailWrite.Add("gpu/0");
        var r = New();
        await r.ReconcileAsync(Eco(), TestContext.Current.CancellationToken);
        Assert.Equal(ActionStatus.Failed, r.Snapshot.Actions[1].Status);
        _m.FailWrite.Clear();
        _clock.Now = _clock.Now.AddMinutes(1);   // the first pause after a failure

        await r.ReconcileAsync(Eco(), TestContext.Current.CancellationToken);

        Assert.Equal("150", _m.Text("gpu/0"));
        Assert.Equal(ActionStatus.Applied, r.Snapshot.Actions[1].Status);
    }

    [Fact]
    public async Task Reconcile_UnavailableModule_IsUnsupportedOthersRun()
    {
        ((FakeModule)_registry.All[1]).Unavailable = true;
        var r = New();
        await r.ReconcileAsync(Eco(), TestContext.Current.CancellationToken);
        Assert.Equal(ActionStatus.Unsupported, r.Snapshot.Actions[1].Status);
        Assert.Equal("\"saver\"", _m.Text("plan/active"));
        Assert.Equal("300", _m.Text("gpu/0"));
    }

    [Fact]
    public async Task Restart_StoppedAfterPreparedBeforeWrite_DropsTheWriteAndChangesNothing()
    {
        _m.StopBeforeWrite.Add("gpu/0");
        await Assert.ThrowsAsync<OperationCanceledException>(() => New().ReconcileAsync(Eco(), TestContext.Current.CancellationToken));

        var r = New();
        Assert.False(r.Owned["gpu/0"].Applied);   // the open write survived the stop
        await r.ReconcileAsync(Normal, TestContext.Current.CancellationToken);

        Assert.Equal("300", _m.Text("gpu/0"));
        Assert.Empty(r.Owned);
        Assert.Equal("\"high\"", _m.Text("plan/active"));
    }

    [Fact]
    public async Task Restart_StoppedAfterWriteBeforeApplied_RestoresIt()
    {
        _m.StopAfterWrite.Add("gpu/0");
        await Assert.ThrowsAsync<OperationCanceledException>(() => New().ReconcileAsync(Eco(), TestContext.Current.CancellationToken));
        Assert.Equal("150", _m.Text("gpu/0"));

        var r = New();
        await r.ReconcileAsync(Normal, TestContext.Current.CancellationToken);

        Assert.Equal("300", _m.Text("gpu/0"));
        Assert.Equal("\"high\"", _m.Text("plan/active"));
        Assert.Empty(r.Owned);
    }

    [Fact]
    public async Task Restart_AfterApplied_RestoresOnlyWhatIsStillOurs()
    {
        await New().ReconcileAsync(Eco(), TestContext.Current.CancellationToken);
        _m.Values["service/x"] = "running";   // started by hand while the service was down

        var r = New();
        await r.ReconcileAsync(Normal, TestContext.Current.CancellationToken);

        Assert.Equal(["\"high\"", "300", "\"running\""], [_m.Text("plan/active"), _m.Text("gpu/0"), _m.Text("service/x")]);
        Assert.Contains(r.Snapshot.Restores, s => s.Key == "service/x" && s.Status == ActionStatus.Relinquished);
    }

    [Fact]
    public async Task Restart_AfterReboot_TokenWriteIsLetGoNotRestored()
    {
        _m.Values["pause/backup"] = false;
        var paused = new Mode { Id = "eco", Name = "Eco", Actions = [Act("pause", "pause/backup", true)] };
        await New().ReconcileAsync(paused, TestContext.Current.CancellationToken);
        _m.Writes.Clear();

        var j = new ModeJournal(JournalPath);
        var r = new ModeReconciler(_registry, j, _clock);
        r.Replay(j.Load().Records, bootAt: _clock.Now.AddMinutes(1));   // booted after the write
        await r.ReconcileAsync(Normal, TestContext.Current.CancellationToken);

        Assert.Empty(_m.Writes);
        Assert.Empty(r.Owned);
    }

    [Fact]
    public async Task Restart_AfterHibernate_TokenWriteStaysOwned()
    {
        // The machine booted an hour before the write, then hibernated for five hours; the service restarts ten minutes after
        // the wake. Windows' time since boot counts the hibernation (GetTickCount64); the awake time does not.
        var sinceBootAtWrite = TimeSpan.FromHours(1);
        _m.Values["pause/backup"] = false;
        var paused = new Mode { Id = "eco", Name = "Eco", Actions = [Act("pause", "pause/backup", true)] };
        await New().ReconcileAsync(paused, TestContext.Current.CancellationToken);
        var slept = TimeSpan.FromHours(5);
        var awakeAfter = TimeSpan.FromMinutes(10);
        _clock.Now += slept + awakeAfter;

        var j = new ModeJournal(JournalPath);
        var r = new ModeReconciler(_registry, j, _clock, (k, d) => _events.Add($"{k}: {d}"));
        r.Replay(j.Load().Records, BootClock.BootAt(_clock, () => sinceBootAtWrite + slept + awakeAfter));

        Assert.True(r.Owned.ContainsKey("pause/backup"));
        Assert.DoesNotContain(_events, e => e.Contains("did not survive the reboot", StringComparison.Ordinal));
    }

    [Fact]
    public void SinceBoot_AfterAwakeTimeRead_IsAtLeastIt()
    {
        // Environment.TickCount64 is the awake time on .NET 11 (sleep and hibernation excluded); the time since boot holds it,
        // give or take GetTickCount64's resolution (the system timer, 10-16 ms).
        var awake = TimeSpan.FromMilliseconds(Environment.TickCount64);
        Assert.InRange(BootClock.SinceBoot(), awake - TimeSpan.FromSeconds(1), TimeSpan.MaxValue);
    }

    [Fact]
    public async Task Reconcile_IrreversibleWrite_IsNeverUndone()
    {
        _m.Values["close/app"] = "running";
        var closing = new Mode { Id = "eco", Name = "Eco", Actions = [Act("close", "close/app", "closed")] };
        var r = New();
        await r.ReconcileAsync(closing, TestContext.Current.CancellationToken);
        await r.ReconcileAsync(Normal, TestContext.Current.CancellationToken);
        Assert.Equal("\"closed\"", _m.Text("close/app"));
        Assert.Empty(r.Owned);
    }

    [Fact]
    public async Task Reconcile_ModeToModeOnSameResource_KeepsTheFirstBaseline()
    {
        var r = New();
        await r.ReconcileAsync(Eco(), TestContext.Current.CancellationToken);
        var max = new Mode { Id = "max", Name = "Max", Actions = [Act("plan", "plan/active", "ultimate")] };
        await r.ReconcileAsync(max, TestContext.Current.CancellationToken);
        Assert.Equal("\"ultimate\"", _m.Text("plan/active"));
        Assert.Equal("300", _m.Text("gpu/0"));

        await r.ReconcileAsync(Normal, TestContext.Current.CancellationToken);

        Assert.Equal("\"high\"", _m.Text("plan/active"));
    }
}
