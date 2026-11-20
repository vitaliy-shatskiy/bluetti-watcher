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

    public CommandPairModuleTests() => _module = new CommandPairModule(Commands, _runner, _log.Add);

    /// <summary>The commands config.json defines; the module's settings can only name them.</summary>
    private static readonly Dictionary<string, CommandSpec> Commands = new(StringComparer.Ordinal)
    {
        ["backup-off"] = new("schtasks.exe", ["/change", "/tn", "\\Backup run", "/disable"], 20),
        ["backup-on"] = new("schtasks.exe", ["/change", "/tn", "\\Backup run", "/enable"], 20),
        ["unload-model"] = new("unload.exe", ["--all"], 30),
    };

    private const string Pair = """{"name":"backup","enter":"backup-off","leave":"backup-on"}""";

    private static JsonObject Settings(string json) => (JsonObject)JsonNode.Parse(json)!;

    private ResourceIntent Enter(string json = Pair) => Assert.Single(_module.Expand(Settings(json)));

    [Fact]
    public async Task EnterThenLeave_RunsEachConfiguredCommandOnceWithItsArgumentList()
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

    [Theory]
    [InlineData("""{"name":"x","enter":"C:\\Windows\\System32\\cmd.exe","leave":""}""")]
    [InlineData("""{"name":"x","enter":"","leave":"powershell.exe"}""")]
    [InlineData("""{"name":"x","enter":{"exe":"cmd.exe","args":["/c","del"]},"leave":""}""")]
    public void Validate_SettingsNamingAnExecutableOrAnUnconfiguredCommand_AreRejected(string json) =>
        Assert.NotEmpty(_module.Validate(Settings(json)));

    [Fact]
    public async Task Apply_TargetCarryingAnExecutable_RunsNothing()
    {
        var crafted = new JsonObject { ["entered"] = true, ["enter"] = new JsonObject { ["exe"] = "cmd.exe", ["args"] = new JsonArray("/c", "del") }, ["leave"] = "" };

        var r = await _module.ApplyAsync(new ResourceIntent("command/x/entered", crafted), CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public async Task Apply_CommandRemovedFromTheConfig_FailsAndRunsNothing()
    {
        var r = await _module.ApplyAsync(Enter("""{"name":"x","enter":"gone","leave":""}"""), CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Contains("not a configured command", r.Error, StringComparison.Ordinal);
        Assert.Empty(_runner.Runs);
    }

    [Fact]
    public async Task Discover_ListsTheConfiguredCommandNames()
    {
        var lists = await _module.DiscoverAsync(CancellationToken.None);

        Assert.Equal(["backup-off", "backup-on", "unload-model"], lists["commands"].Select(i => i.Id));
    }

    [Fact]
    public async Task Available_NoCommandsConfigured_IsUnavailable()
    {
        var empty = new CommandPairModule(new Dictionary<string, CommandSpec>(StringComparer.Ordinal), _runner, _log.Add);

        Assert.False((await empty.AvailableAsync(CancellationToken.None)).Available);
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
    public async Task Enter_NoEnterCommand_RunsNothingAndLeaveStillRuns()
    {
        var intent = Enter("""{"name":"model","enter":"","leave":"unload-model"}""");

        await _module.ApplyAsync(intent, CancellationToken.None);
        Assert.Empty(_runner.Runs);
        await _module.ApplyAsync(new ResourceIntent(intent.Key, false), CancellationToken.None);

        Assert.Equal("unload.exe", Assert.Single(_runner.Runs).Exe);
    }
}
