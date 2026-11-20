using System.Text.Json.Nodes;
using BluettiWatcher.Modes;
using BluettiWatcher.Modes.Broker;
using BluettiWatcher.Modes.Modules;

namespace BluettiWatcher.Tests.Modes;

public sealed class ScheduledTaskModuleTests
{
    private readonly FakeTaskScheduler _tasks = new();
    private readonly FakeBroker _broker = new();
    private readonly ScheduledTaskModule _module;

    public ScheduledTaskModuleTests()
    {
        _module = new ScheduledTaskModule(_tasks, _broker);
        _broker.OnRun = (op, args) => _tasks.Tasks[args["path"]!.GetValue<string>()] = op == "task.enable";
    }

    private ResourceIntent Single(string json) => Assert.Single(_module.Expand((JsonObject)JsonNode.Parse(json)!));

    [Fact]
    public async Task Apply_UserTask_IsDisabledDirectlyWithoutTheBroker()
    {
        _tasks.Tasks[@"\Backup"] = true;

        var r = await _module.ApplyAsync(Single("""{"tasks":[{"path":"\\Backup","enabled":false}]}"""), CancellationToken.None);

        Assert.True(r.Ok);
        Assert.False(_tasks.Tasks[@"\Backup"]);
        Assert.Empty(_broker.Calls);
    }

    [Fact]
    public async Task Apply_AdminTask_GoesThroughTheBroker()
    {
        _tasks.Tasks[@"\Vendor\Updater"] = true;
        _tasks.AdminOnly.Add(@"\Vendor\Updater");

        var r = await _module.ApplyAsync(Single("""{"tasks":[{"path":"\\Vendor\\Updater","enabled":false}]}"""), CancellationToken.None);

        Assert.True(r.Ok);
        var call = Assert.Single(_broker.Calls);
        Assert.Equal("task.disable", call.Op);
        Assert.Equal(@"\Vendor\Updater", call.Args["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task Apply_DirectAndBrokerBothFail_ReportsBoth()
    {
        _tasks.Tasks[@"\Vendor\Updater"] = true;
        _tasks.AdminOnly.Add(@"\Vendor\Updater");
        _broker.Answer = (_, _) => new BrokerResult(false, "task not in the policy", null);

        var r = await _module.ApplyAsync(new ResourceIntent(@"task/\Vendor\Updater/enabled", false), CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Contains("access is denied", r.Error, StringComparison.Ordinal);
        Assert.Contains("task not in the policy", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnterThenLeave_TaskDisabledByTheMode_IsEnabledAgain()
    {
        _tasks.Tasks[@"\Backup"] = true;
        var intent = Single("""{"tasks":[{"path":"\\Backup","enabled":false}]}""");
        var baseline = await _module.ReadAsync(intent.Key, CancellationToken.None);

        await _module.ApplyAsync(intent, CancellationToken.None);
        var restored = await _module.ApplyAsync(new ResourceIntent(intent.Key, baseline.Value), CancellationToken.None);

        Assert.True(restored.Ok);
        Assert.True(_tasks.Tasks[@"\Backup"]);
    }

    [Fact]
    public async Task Read_MissingTask_IsAFailedRead()
    {
        var r = await _module.ReadAsync(@"task/\Nope/enabled", CancellationToken.None);

        Assert.False(r.Ok);
    }

    [Fact]
    public void Validate_RelativePath_IsReported()
    {
        Assert.Contains(_module.Validate((JsonObject)JsonNode.Parse("""{"tasks":[{"path":"Backup","enabled":false}]}""")!),
            p => p.Contains("starts with", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("<Task><Settings><Enabled>false</Enabled></Settings></Task>", false)]
    [InlineData("<Task><Settings><Hidden>true</Hidden></Settings></Task>", true)]
    [InlineData("<Task><Triggers><TimeTrigger><Enabled>false</Enabled></TimeTrigger></Triggers><Settings><Enabled>true</Enabled></Settings></Task>", true)]
    [InlineData("<Task><Triggers><TimeTrigger><Enabled>true</Enabled></TimeTrigger></Triggers><Settings>\n  <Enabled>false</Enabled>\n</Settings></Task>", false)]
    public void ParseEnabled_ReadsOnlyTheSettingsBlock(string xml, bool expected) =>
        Assert.Equal(expected, SchtasksTaskScheduler.ParseEnabled(xml));

    [Fact]
    public void ParseEnabled_NoSettingsBlock_IsUnknown() => Assert.Null(SchtasksTaskScheduler.ParseEnabled("<Task></Task>"));
}
