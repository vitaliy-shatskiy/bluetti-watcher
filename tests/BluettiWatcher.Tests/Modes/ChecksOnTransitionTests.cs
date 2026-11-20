using System.Text.Json.Nodes;
using BluettiWatcher.Modes;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests.Modes;

/// <summary>An expensive-to-read module (an NVIDIA profile export) is read on entering and leaving a mode, not at every poll.</summary>
public sealed class ChecksOnTransitionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bw-transition-").FullName;
    private readonly FakeMachine _m = new();
    private readonly CountingModule _module;
    private readonly ModeReconciler _r;

    public ChecksOnTransitionTests()
    {
        _module = new CountingModule(new FakeModule("fake.profile", _m, RestorationClass.Compensatable));
        _r = new ModeReconciler(new ModuleRegistry([_module]), new ModeJournal(Path.Combine(_dir, ModeJournal.FileName)), new ManualClock(T0));
        _m.Values["p"] = "stock";
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Mode Eco() => new()
    {
        Id = "eco", Name = "Eco", FlagWord = FlagWord.Eco,
        Actions = [new ModeAction { TypeId = "fake.profile", Settings = new JsonObject { ["key"] = "p", ["value"] = "quiet" } }],
    };

    private static Mode Normal() => new() { Id = "normal", Name = "Normal", FlagWord = FlagWord.Normal, Actions = [] };

    [Fact]
    public async Task Reconcile_PollsWithinOneMode_ReadOnlyOnEnteringAndLeaving()
    {
        var ct = TestContext.Current.CancellationToken;
        await _r.ReconcileAsync(Eco(), ct);
        int afterEnter = _module.Reads;
        for (int i = 0; i < 5; i++) await _r.ReconcileAsync(Eco(), ct);

        Assert.Equal(afterEnter, _module.Reads);
        Assert.Equal(ActionStatus.Applied, Assert.Single(_r.Snapshot.Actions).Status);

        await _r.ReconcileAsync(Normal(), ct);
        Assert.True(_module.Reads > afterEnter);
        Assert.Equal("\"stock\"", _m.Text("p"));
    }

    [Fact]
    public async Task Reconcile_FailedRead_IsTriedAgainOnTheNextPoll()
    {
        var ct = TestContext.Current.CancellationToken;
        _m.FailRead.Add("p");
        await _r.ReconcileAsync(Eco(), ct);
        Assert.Equal(ActionStatus.Failed, Assert.Single(_r.Snapshot.Actions).Status);

        _m.FailRead.Clear();
        await _r.ReconcileAsync(Eco(), ct);
        Assert.Equal(ActionStatus.Applied, Assert.Single(_r.Snapshot.Actions).Status);
    }

    private sealed class CountingModule(FakeModule inner) : IActionModule, IChecksOnTransition
    {
        public int Reads { get; private set; }
        public ActionModuleDescriptor Descriptor => inner.Descriptor;
        public Task<ModuleAvailability> AvailableAsync(CancellationToken ct) => inner.AvailableAsync(ct);
        public Task<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>>> DiscoverAsync(CancellationToken ct) => inner.DiscoverAsync(ct);
        public IReadOnlyList<string> Validate(JsonObject settings) => inner.Validate(settings);
        public IReadOnlyList<ResourceIntent> Expand(JsonObject settings) => inner.Expand(settings);
        public bool Equivalent(string resourceKey, JsonNode? a, JsonNode? b) => inner.Equivalent(resourceKey, a, b);
        public Task<ApplyResult> ApplyAsync(ResourceIntent intent, CancellationToken ct) => inner.ApplyAsync(intent, ct);

        public Task<ResourceRead> ReadAsync(string resourceKey, CancellationToken ct)
        {
            Reads++;
            return inner.ReadAsync(resourceKey, ct);
        }
    }
}
