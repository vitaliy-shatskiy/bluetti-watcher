using System.Text.Json.Nodes;
using BluettiWatcher.Modes;
using BluettiWatcher.Modes.Modules;

namespace BluettiWatcher.Tests.Modes;

public sealed class ProcessCloseModuleTests
{
    private readonly FakeProcessControl _processes = new();
    private readonly ProcessCloseModule _module;
    private readonly PhoneLinkModule _phone;

    public ProcessCloseModuleTests()
    {
        var clock = new JumpClock();
        _module = new ProcessCloseModule(_processes, clock);
        _phone = new PhoneLinkModule(_processes, clock);
    }

    private static JsonObject Settings(string json) => (JsonObject)JsonNode.Parse(json)!;

    private ResourceIntent Single(string json) => Assert.Single(_module.Expand(Settings(json)));

    [Fact]
    public async Task Apply_AppClosesWhenAsked_IsNotKilled()
    {
        _processes.Running["game.exe"] = 1;
        _processes.ClosesOnRequest.Add("game.exe");

        var r = await _module.ApplyAsync(Single("""{"apps":["game"],"graceSec":5,"kill":true}"""), CancellationToken.None);

        Assert.True(r.Ok);
        Assert.False(r.ReadBack!.GetValue<bool>());
        Assert.Equal(["game.exe"], _processes.CloseRequests);
        Assert.Empty(_processes.Kills);
    }

    [Fact]
    public async Task Apply_AppIgnoresTheCloseAndKillIsOff_FailsAndKillsNothing()
    {
        _processes.Running["editor.exe"] = 1;

        var r = await _module.ApplyAsync(Single("""{"apps":["editor.exe"],"graceSec":5,"kill":false}"""), CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Contains("still runs 5 s after", r.Error, StringComparison.Ordinal);
        Assert.Empty(_processes.Kills);
        Assert.Equal(1, _processes.Running["editor.exe"]);
    }

    [Fact]
    public async Task Apply_AppIgnoresTheCloseAndKillIsOn_IsEnded()
    {
        _processes.Running["editor.exe"] = 2;

        var r = await _module.ApplyAsync(Single("""{"apps":["editor.exe"],"graceSec":5,"kill":true}"""), CancellationToken.None);

        Assert.True(r.Ok);
        Assert.Equal(["editor.exe"], _processes.Kills);
    }

    [Fact]
    public async Task Apply_RestoreToRunning_NeverStartsAnApp()
    {
        var r = await _module.ApplyAsync(new ResourceIntent("process/game.exe/running", true), CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Contains("nothing is started", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_NotRunning_WritesFalseWithoutAsking()
    {
        var r = await _module.ApplyAsync(Single("""{"apps":["game.exe"]}"""), CancellationToken.None);

        Assert.True(r.Ok);
        Assert.Empty(_processes.CloseRequests);
    }

    [Fact]
    public async Task Apply_ProtectedProcessKey_IsRefusedEvenWhenCraftedByHand()
    {
        _processes.Running["explorer.exe"] = 1;

        var r = await _module.ApplyAsync(new ResourceIntent("process/Explorer.exe/running", ProcessCloser.CloseTarget(1, kill: true)), CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Empty(_processes.CloseRequests);
        Assert.Empty(_processes.Kills);
    }

    [Fact]
    public void ValidateAndExpand_ProtectedApp_IsReportedAndNeverExpanded()
    {
        var settings = Settings("""{"apps":["explorer","game.exe"]}""");

        Assert.Contains(_module.Validate(settings), p => p.Contains("never closed", StringComparison.Ordinal));
        Assert.Equal(["process/game.exe/running"], _module.Expand(settings).Select(i => i.Key));
    }

    [Fact]
    public void Validate_PathInsteadOfName_IsReported()
    {
        Assert.Contains(_module.Validate(Settings("""{"apps":["C:\\Games\\game.exe"]}""")), p => p.Contains("not a path", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Read_ProcessListUnreadable_IsAFailedRead()
    {
        _processes.Broken = true;

        var r = await _module.ReadAsync("process/game.exe/running", CancellationToken.None);

        Assert.False(r.Ok);
    }

    [Fact]
    public void Equivalent_ComparesTheRunningFlagOfACloseTarget()
    {
        Assert.True(_module.Equivalent("process/a.exe/running", false, ProcessCloser.CloseTarget(5, kill: false)));
        Assert.False(_module.Equivalent("process/a.exe/running", true, ProcessCloser.CloseTarget(5, kill: false)));
    }

    [Fact]
    public void PhoneLink_Expand_EndsTheCrossDeviceProcesses()
    {
        var intents = _phone.Expand([]);

        Assert.Equal(PhoneLinkModule.Processes.Select(ProcessCloser.Key), intents.Select(i => i.Key));
        Assert.All(intents, i => Assert.True(i.Target!["kill"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task PhoneLink_Apply_KeyOutsideThePreset_IsRefused()
    {
        _processes.Running["game.exe"] = 1;

        var r = await _phone.ApplyAsync(new ResourceIntent("process/game.exe/running", ProcessCloser.CloseTarget(1, kill: true)), CancellationToken.None);

        Assert.False(r.Ok);
        Assert.Empty(_processes.Kills);
    }

    [Fact]
    public async Task PhoneLink_Apply_BackgroundHostWithoutWindows_IsEnded()
    {
        _processes.Running["PhoneExperienceHost.exe"] = 1;
        var intent = _phone.Expand([]).Single(i => i.Key.Contains("PhoneExperienceHost", StringComparison.Ordinal));

        var r = await _phone.ApplyAsync(intent, CancellationToken.None);

        Assert.True(r.Ok);
        Assert.Equal(["PhoneExperienceHost.exe"], _processes.Kills);
    }
}
