using System.Text.Json.Nodes;
using BluettiWatcher.Modes;
using BluettiWatcher.Modes.Modules;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests.Modes;

/// <summary>A command pair entered before a service restart still runs its leave command: the journal, not the module's memory, says it was entered.</summary>
public sealed class CommandPairRestartTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bw-pair-restart-").FullName;
    private readonly ManualClock _clock = new(T0);
    private readonly FakeRunner _runner = new();

    private static readonly Dictionary<string, CommandSpec> Commands = new(StringComparer.Ordinal)
    {
        ["pause"] = new("backup.exe", ["pause"], 20),
        ["resume"] = new("backup.exe", ["resume"], 20),
    };

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string JournalPath => Path.Combine(_dir, ModeJournal.FileName);

    private static Mode Eco() => new()
    {
        Id = "eco", Name = "Eco", FlagWord = FlagWord.Eco,
        Actions = [new ModeAction { TypeId = CommandPairModule.Id, Settings = new JsonObject { ["name"] = "backup", ["enter"] = "pause", ["leave"] = "resume" } }],
    };

    private static Mode Normal() => new() { Id = "normal", Name = "Normal", FlagWord = FlagWord.Normal, Actions = [] };

    /// <summary>One service lifetime: a fresh module (empty memory) and a reconciler replayed from the journal on disk.</summary>
    private ModeReconciler Start()
    {
        var registry = new ModuleRegistry([new CommandPairModule(Commands, _runner, _ => { })]);
        var journal = new ModeJournal(JournalPath);
        var r = new ModeReconciler(registry, journal, _clock);
        r.Replay(journal.Load().Records, _clock.Now.AddDays(-1));
        return r;
    }

    [Fact]
    public async Task Leave_AfterARestartInTheMode_RunsTheLeaveCommand()
    {
        await Start().ReconcileAsync(Eco(), TestContext.Current.CancellationToken);
        _clock.Now = _clock.Now.AddMinutes(5);

        await Start().ReconcileAsync(Normal(), TestContext.Current.CancellationToken);

        Assert.Equal(["pause", "resume"], _runner.Runs.Select(r => r.Args[0]));
    }

    [Fact]
    public async Task Stay_AfterARestartInTheMode_DoesNotRunEnterAgain()
    {
        await Start().ReconcileAsync(Eco(), TestContext.Current.CancellationToken);
        _clock.Now = _clock.Now.AddMinutes(5);

        var after = Start();
        await after.ReconcileAsync(Eco(), TestContext.Current.CancellationToken);

        Assert.Equal(["pause"], _runner.Runs.Select(r => r.Args[0]));
        Assert.Equal(ActionStatus.Applied, Assert.Single(after.Snapshot.Actions).Status);
    }
}
