using System.Text.Json.Nodes;
using BluettiWatcher.Io;
using BluettiWatcher.Modes;
using BluettiWatcher.Modes.Modules;

namespace BluettiWatcher.Tests.Modes;

public sealed class CommandPairModuleTests
{
    private readonly FakeRunner _runner = new();
    private readonly List<string> _log = [];
    private readonly CommandPairModule _module;

    public CommandPairModuleTests() => _module = new CommandPairModule(_runner, _log.Add);

    private const string Pair = """
        {"name":"backup","enter":{"exe":"schtasks.exe","args":["/change","/tn","\\Backup run","/disable"]},
         "leave":{"exe":"schtasks.exe","args":["/change","/tn","\\Backup run","/enable"]},"timeoutSec":20}
        """;

    private ResourceIntent Enter(string json = Pair) => Assert.Single(_module.Expand((JsonObject)JsonNode.Parse(json)!));

    [Fact]
    public async Task EnterThenLeave_RunsEachCommandOnceWithItsArgumentList()
    {
        var intent = Enter();
        var baseline = await _module.ReadAsync(intent.Key, CancellationToken.None);

        var entered = await _module.ApplyAsync(intent, CancellationToken.None);
        var left = await _module.ApplyAsync(new ResourceIntent(intent.Key, baseline.Value), CancellationToken.None);

        Assert.True(entered.Ok);
        Assert.True(left.Ok);
        Assert.Equal(2, _runner.Runs.Count);
        Assert.Equal(["/change", "/tn", "\\Backup run", "/disable"], _runner.Runs[0].Args);
        Assert.Equal(["/change", "/tn", "\\Backup run", "/enable"], _runner.Runs[1].Args);
        Assert.Equal(2, _log.Count);
    }

    [Fact]
    public async Task Read_AfterEnter_IsEnteredAndEquivalentToTheTarget()
    {
        var intent = Enter();
        await _module.ApplyAsync(intent, CancellationToken.None);

        var r = await _module.ReadAsync(intent.Key, CancellationToken.None);

        Assert.True(_module.Equivalent(intent.Key, r.Value, intent.Target));
    }

    [Fact]
    public async Task Enter_NonZeroExit_FailsAndStaysLeft()
    {
        _runner.Answer = (_, _) => new ProcessResult(1, ["ERROR: access is denied"], false);
        var intent = Enter();

        var r = await _module.ApplyAsync(intent, CancellationToken.None);
        var read = await _module.ReadAsync(intent.Key, CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Contains("exit 1", r.Error, StringComparison.Ordinal);
        Assert.False(read.Value!.GetValue<bool>());
    }

    [Fact]
    public async Task Enter_TimedOut_Fails()
    {
        _runner.Answer = (_, _) => new ProcessResult(-1, [], true);

        var r = await _module.ApplyAsync(Enter(), CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Contains("killed after 20 s", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Enter_Twice_RunsTheCommandOnce()
    {
        var intent = Enter();

        await _module.ApplyAsync(intent, CancellationToken.None);
        var again = await _module.ApplyAsync(intent, CancellationToken.None);

        Assert.True(again.Ok);
        Assert.Single(_runner.Runs);
    }

    [Fact]
    public async Task Leave_NeverEntered_RunsNothing()
    {
        var r = await _module.ApplyAsync(new ResourceIntent("command/backup/entered", false), CancellationToken.None);

        Assert.True(r.Ok);
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public async Task Enter_EmptyEnterCommand_RunsNothingAndLeaveStillRuns()
    {
        var intent = Enter("""{"name":"model","enter":{"exe":"","args":[]},"leave":{"exe":"unload.exe","args":["--all"]}}""");

        await _module.ApplyAsync(intent, CancellationToken.None);
        Assert.Empty(_runner.Runs);
        await _module.ApplyAsync(new ResourceIntent(intent.Key, false), CancellationToken.None);

        Assert.Equal("unload.exe", Assert.Single(_runner.Runs).Exe);
    }

    [Fact]
    public void Validate_ArgsNotStrings_IsReported()
    {
        var problems = _module.Validate((JsonObject)JsonNode.Parse("""{"name":"x","enter":{"exe":"a.exe","args":[1]},"leave":{"exe":"","args":[]}}""")!);

        Assert.Contains(problems, p => p.Contains("enter.args", StringComparison.Ordinal));
    }
}
