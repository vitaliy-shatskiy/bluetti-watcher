using System.Text.Json.Nodes;
using BluettiWatcher.Modes;
using BluettiWatcher.Modes.Modules;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests.Modes;

/// <summary><c>rgb.lighting</c> through the reconciler: off on entering, the chosen profile on leaving, the journal across a restart.</summary>
public sealed class RgbLightingModuleTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bw-rgb-").FullName;
    private readonly ManualClock _clock = new(T0);
    private readonly FakeOpenRgb _rgb = new();
    private readonly List<string> _events = [];

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Mode Eco(string profile = "Normal", bool enabled = true, params string[] devices) => new()
    {
        Id = "eco", Name = "Eco", FlagWord = FlagWord.Eco,
        Actions =
        [
            new ModeAction
            {
                TypeId = RgbLightingModule.Id, Enabled = enabled,
                Settings = new JsonObject { ["devices"] = new JsonArray([.. devices.Select(d => (JsonNode)d)]), ["profile"] = profile },
            },
        ],
    };

    private static Mode Normal() => new() { Id = "normal", Name = "Normal", FlagWord = FlagWord.Normal, Actions = [] };

    /// <summary>One service lifetime: a fresh module (empty memory) and a reconciler replayed from the journal on disk.</summary>
    private ModeReconciler Start()
    {
        var registry = new ModuleRegistry([new RgbLightingModule(_rgb, _ => { })]);
        var journal = new ModeJournal(Path.Combine(_dir, ModeJournal.FileName));
        var r = new ModeReconciler(registry, journal, _clock, (kind, _) => _events.Add(kind));
        r.Replay(journal.Load().Records, _clock.Now.AddDays(-1));
        return r;
    }

    [Fact]
    public async Task EnterThenLeave_TurnsTheDevicesOffThenLoadsTheChosenProfile()
    {
        var r = Start();
        await r.ReconcileAsync(Eco("Normal", true, "Z790 GAMING X"), TestContext.Current.CancellationToken);
        await r.ReconcileAsync(Normal(), TestContext.Current.CancellationToken);

        Assert.Equal(["off:Z790 GAMING X", "profile:Normal"], _rgb.Calls);
    }

    [Fact]
    public async Task Leave_WithNoProfileChosen_LeavesTheLightingOff()
    {
        var r = Start();
        await r.ReconcileAsync(Eco(""), TestContext.Current.CancellationToken);
        await r.ReconcileAsync(Normal(), TestContext.Current.CancellationToken);

        Assert.Equal(["off:"], _rgb.Calls);
        Assert.Empty(r.Owned);
    }

    [Fact]
    public async Task Leave_AfterARestartInEco_LoadsTheProfileFromTheJournal()
    {
        await Start().ReconcileAsync(Eco("Night"), TestContext.Current.CancellationToken);
        _clock.Now = _clock.Now.AddMinutes(5);

        await Start().ReconcileAsync(Normal(), TestContext.Current.CancellationToken);

        Assert.Equal(["off:", "profile:Night"], _rgb.Calls);
    }

    [Fact]
    public async Task Stay_AfterARestartInEco_DoesNotTouchTheLightingAgain()
    {
        await Start().ReconcileAsync(Eco(), TestContext.Current.CancellationToken);
        var after = Start();
        await after.ReconcileAsync(Eco(), TestContext.Current.CancellationToken);

        Assert.Equal(["off:"], _rgb.Calls);
        Assert.Equal(ActionStatus.Applied, Assert.Single(after.Snapshot.Actions).Status);
    }

    [Fact]
    public async Task Enter_OpenRgbMissing_IsUnavailableAndRunsNothing()
    {
        _rgb.ExePath = null;
        var r = Start();
        await r.ReconcileAsync(Eco(), TestContext.Current.CancellationToken);

        var report = Assert.Single(r.Snapshot.Actions);
        Assert.Equal(ActionStatus.Unsupported, report.Status);
        Assert.Contains("OpenRGB", report.Detail, StringComparison.Ordinal);
        Assert.Empty(_rgb.Calls);
    }

    [Fact]
    public async Task Enter_ActionDisabled_NeverTouchesTheLighting()
    {
        var r = Start();
        await r.ReconcileAsync(Eco(enabled: false), TestContext.Current.CancellationToken);
        await r.ReconcileAsync(Normal(), TestContext.Current.CancellationToken);

        Assert.Empty(_rgb.Calls);
    }

    [Fact]
    public async Task Enter_FailingCall_IsReportedOnceAndNotRetriedEachPass()
    {
        _rgb.Failure = "OpenRGB's server did not answer";
        var r = Start();
        await r.ReconcileAsync(Eco(), TestContext.Current.CancellationToken);
        await r.ReconcileAsync(Eco(), TestContext.Current.CancellationToken);
        await r.ReconcileAsync(Eco(), TestContext.Current.CancellationToken);

        Assert.Single(_rgb.Calls);
        Assert.Equal(["action-failed"], _events);
        var report = Assert.Single(r.Snapshot.Actions);
        Assert.Equal(ActionStatus.Failed, report.Status);
        Assert.Contains("did not answer", report.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discover_NoServerRunning_ListsProfilesButNoDevicesAndStartsNothing()
    {
        var lists = await new RgbLightingModule(_rgb, _ => { }).DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Night", "Normal"], lists["profiles"].Select(p => p.Id));
        Assert.False(lists.ContainsKey("devices"));
        Assert.Empty(_rgb.Calls);
    }

    [Theory]
    [InlineData("""{"devices":["A","a"],"profile":""}""")]
    [InlineData("""{"devices":[""],"profile":""}""")]
    [InlineData("""{"devices":[],"profile":"x/y"}""")]
    public void Validate_RepeatedOrEmptyDeviceOrAProfilePath_IsRejected(string json) =>
        Assert.NotEmpty(new RgbLightingModule(_rgb, _ => { }).Validate((JsonObject)JsonNode.Parse(json)!));

    [Fact]
    public void Defaults_PassTheSchemaAndTheModule()
    {
        var registry = new ModuleRegistry([new RgbLightingModule(_rgb, _ => { })]);
        Assert.Empty(registry.Validate(RgbLightingModule.Id, JsonFields.Parse("""{"devices":[],"profile":""}""")));
    }
}
