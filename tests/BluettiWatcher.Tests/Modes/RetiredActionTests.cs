using System.Text.Json;
using System.Text.Json.Nodes;
using BluettiWatcher.Modes;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests.Modes;

/// <summary>
/// An action type this build no longer has (<c>gpu.idleWatcher</c>, removed in 1.10.6): a saved <c>modes.json</c> that lists it
/// loads without a problem, the action is dropped and saved away with one log line, and a resource it held in the journal is
/// let go instead of failing to restore on every pass.
/// </summary>
public sealed class RetiredActionTests : IDisposable
{
    private const string Retired = "gpu.idleWatcher";
    private readonly string _dir = Directory.CreateTempSubdirectory("bw-modes-retired-").FullName;
    private readonly ManualClock _clock = new(T0);
    private readonly FakeMachine _m = new();
    private readonly List<string> _log = [];

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ModesEngine Engine() => new(new StationConfig { DataDir = _dir }, new ModuleRegistry([new FakeModule(BuiltInModes.PowerPlanType, _m)]),
        new ModesStore(_dir), new ModeJournal(Path.Combine(_dir, ModeJournal.FileName)), new FakeFlagWriter(), _clock, _log.Add);

    private void SaveWithRetiredAction()
    {
        var s = BuiltInModes.Defaults(300, 300);
        var watcher = new ModeAction { TypeId = Retired, Enabled = true, Settings = new JsonObject { ["minutes"] = 5, ["minClockMhz"] = 1000, ["maxUtilization"] = 2 } };
        s = s with { Modes = [.. s.Modes.Select(m => m.Id == BuiltInModes.Eco ? m with { Actions = [.. m.Actions, watcher] } : m)] };
        File.WriteAllBytes(Path.Combine(_dir, ModesStore.FileName), JsonSerializer.SerializeToUtf8Bytes(s, ModesJson.Default.ModesSettings));
    }

    [Fact]
    public void Start_SavedModeListsTheRetiredType_LoadsWithoutItSavesAndLogsOnce()
    {
        SaveWithRetiredAction();
        using var engine = Engine();

        engine.Start(T0.AddDays(-1));

        Assert.Empty(engine.Problems);
        Assert.DoesNotContain(engine.Settings.Modes.SelectMany(m => m.Actions), a => a.TypeId == Retired);
        Assert.Single(_log, l => l.Contains(Retired, StringComparison.Ordinal));
        Assert.DoesNotContain(Retired, File.ReadAllText(Path.Combine(_dir, ModesStore.FileName)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Step_JournalHoldsAResourceOfTheRetiredType_LetGoNotARestoreFailure()
    {
        var journal = new ModeJournal(Path.Combine(_dir, ModeJournal.FileName));
        journal.Load();
        journal.Append(new JournalRecord { At = T0, Kind = JournalKind.ModeEntered, ModeId = BuiltInModes.Eco });
        journal.Append(new JournalRecord { At = T0, Kind = JournalKind.Prepared, ModeId = BuiltInModes.Eco, TypeId = Retired, Key = "gpu/idleWatcher", Before = false, Value = true });
        journal.Append(new JournalRecord { At = T0, Kind = JournalKind.Applied, ModeId = BuiltInModes.Eco, TypeId = Retired, Key = "gpu/idleWatcher", Value = true });
        using var engine = Engine();
        engine.Start(T0.AddDays(-1));
        _clock.Now = T0.AddMinutes(1);

        await engine.SelectAsync(BuiltInModes.Normal, TestContext.Current.CancellationToken);
        await engine.Pending;

        Assert.DoesNotContain(engine.Snapshot.Restores, r => r.TypeId == Retired);
    }
}
