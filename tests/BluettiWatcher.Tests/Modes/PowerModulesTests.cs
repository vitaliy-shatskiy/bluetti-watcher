using System.Text.Json.Nodes;
using BluettiWatcher.Modes;
using BluettiWatcher.Modes.Broker;
using BluettiWatcher.Modes.Modules;

namespace BluettiWatcher.Tests.Modes;

/// <summary>The power modules against fakes: discovery, expand, apply with read-back, restore of the baseline, failed reads.</summary>
public sealed class PowerModulesTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly FakePowerSchemes _schemes = new();
    private readonly FakeNvidiaSmi _smi = new();
    private readonly FakeAdminBroker _broker;

    public PowerModulesTests()
    {
        // The broker stands in for the SYSTEM task: it sets the fake GPU's limit as the real one would.
        _broker = new FakeAdminBroker((op, args) =>
        {
            if (op != "gpu.setPowerLimit") return new BrokerResult(false, "refused: unknown operation", null);
            _smi.SetPowerLimitAsync(args["gpu"]!.GetValue<string>(), args["watts"]!.GetValue<double>(), Ct);
            return new BrokerResult(true, null, null);
        });
    }

    private static async Task<(ResourceRead Baseline, ApplyResult Written)> ApplyAsync(IActionModule m, ResourceIntent intent)
    {
        var before = await m.ReadAsync(intent.Key, Ct);
        return (before, await m.ApplyAsync(intent, Ct));
    }

    // ---- windows.powerPlan --------------------------------------------------------------------------------------------

    [Fact]
    public async Task PowerPlan_Discover_OffersUserCreatedPlan()
    {
        var lists = await new PowerPlanModule(_schemes).DiscoverAsync(Ct);
        Assert.Contains(lists["plans"], i => i.Id == FakePowerSchemes.UserMade.ToString("D") && i.Label == "My quiet plan");
    }

    [Fact]
    public async Task PowerPlan_UserCreatedPlan_AppliedAndReadBack()
    {
        var m = new PowerPlanModule(_schemes);
        var intent = Assert.Single(m.Expand(new JsonObject { ["plan"] = FakePowerSchemes.UserMade.ToString("D").ToUpperInvariant() }));
        Assert.Equal("windows.activePlan", intent.Key);
        var (baseline, written) = await ApplyAsync(m, intent);
        Assert.True(written.Ok, written.Error);
        Assert.Equal(FakePowerSchemes.UserMade, _schemes.Active);
        Assert.True(m.Equivalent(intent.Key, intent.Target, written.ReadBack));
        // The restore writes the baseline through the same call.
        Assert.True((await m.ApplyAsync(new ResourceIntent(intent.Key, baseline.Value), Ct)).Ok);
        Assert.Equal(FakePowerSchemes.Balanced, _schemes.Active);
    }

    [Fact]
    public async Task PowerPlan_MissingPlan_FailsWithoutFallback()
    {
        var m = new PowerPlanModule(_schemes);
        var r = await m.ApplyAsync(new ResourceIntent("windows.activePlan", "11111111-2222-3333-4444-555555555555"), Ct);
        Assert.False(r.Ok);
        Assert.Contains("not on this machine", r.Error, StringComparison.Ordinal);
        Assert.Empty(_schemes.Writes);
    }

    [Fact]
    public async Task PowerPlan_ListUnreadable_UnavailableAndListAbsent()
    {
        _schemes.FailList = true;
        var m = new PowerPlanModule(_schemes);
        Assert.False((await m.AvailableAsync(Ct)).Available);
        Assert.False((await m.DiscoverAsync(Ct)).ContainsKey("plans"));
        Assert.False((await m.ApplyAsync(new ResourceIntent("windows.activePlan", FakePowerSchemes.Saver.ToString("D")), Ct)).Ok);
    }

    [Fact]
    public async Task PowerPlan_ActiveUnreadable_ReadFailed()
    {
        _schemes.FailActive = true;
        Assert.False((await new PowerPlanModule(_schemes).ReadAsync("windows.activePlan", Ct)).Ok);
    }

    [Fact]
    public void PowerPlan_Equivalent_IgnoresCase() =>
        Assert.True(new PowerPlanModule(_schemes).Equivalent("windows.activePlan", "A1841308-3542-4F1F-A8D6-F8EF75CE11D1", FakePowerSchemes.Saver.ToString("D")));

    [Theory]
    [InlineData("""{"plan":"saver"}""")]
    [InlineData("""{"plan":5}""")]
    [InlineData("{}")]
    public void PowerPlan_Validate_RefusesNonGuid(string json) =>
        Assert.NotEmpty(new PowerPlanModule(_schemes).Validate((JsonObject)JsonNode.Parse(json)!));

    // ---- windows.cpuPolicy --------------------------------------------------------------------------------------------

    [Fact]
    public void CpuPolicy_Expand_OneKeyPerFieldWithPlan()
    {
        var m = new CpuPolicyModule(_schemes);
        var intents = m.Expand(new JsonObject { ["plan"] = FakePowerSchemes.Saver.ToString("D"), ["maxStateDc"] = 80, ["boostAc"] = "efficientEnabled" });
        Assert.Equal([$"plan/{FakePowerSchemes.Saver:D}/dc/maxProcessorState", $"plan/{FakePowerSchemes.Saver:D}/ac/boostMode"], intents.Select(i => i.Key));
        Assert.Equal(3, intents[1].Target!["value"]!.GetValue<long>());
    }

    [Fact]
    public async Task CpuPolicy_ActivePlan_AppliedToActiveAndRestoredToSamePlanAfterSwitch()
    {
        var m = new CpuPolicyModule(_schemes);
        var intent = Assert.Single(m.Expand(new JsonObject { ["maxStateAc"] = 70 }));
        Assert.Equal("plan/active/ac/maxProcessorState", intent.Key);
        var (baseline, written) = await ApplyAsync(m, intent);
        Assert.True(written.Ok, written.Error);
        Assert.Equal(70u, _schemes.Values[(FakePowerSchemes.Balanced, PowerSetting.MaxProcessorState.Setting, PowerSource.Ac)]);
        Assert.True(m.Equivalent(intent.Key, intent.Target, written.ReadBack));
        // The user switches plans; the baseline still goes back to the plan it was read from.
        _schemes.Active = FakePowerSchemes.Saver;
        Assert.True((await m.ApplyAsync(new ResourceIntent(intent.Key, baseline.Value), Ct)).Ok);
        Assert.Equal(100u, _schemes.Values[(FakePowerSchemes.Balanced, PowerSetting.MaxProcessorState.Setting, PowerSource.Ac)]);
        Assert.False(_schemes.Values.ContainsKey((FakePowerSchemes.Saver, PowerSetting.MaxProcessorState.Setting, PowerSource.Ac)));
    }

    [Fact]
    public async Task CpuPolicy_ReadFails_IsFailedNotAGuess()
    {
        _schemes.FailRead = true;
        var r = await new CpuPolicyModule(_schemes).ReadAsync("plan/active/dc/boostMode", Ct);
        Assert.False(r.Ok);
        Assert.Null(r.Value);
    }

    [Fact]
    public async Task CpuPolicy_WriteOkButReadBackFails_IsFailure()
    {
        var m = new CpuPolicyModule(_schemes);
        var intent = Assert.Single(m.Expand(new JsonObject { ["maxStateDc"] = 50 }));
        _schemes.FailRead = true;
        Assert.False((await m.ApplyAsync(intent, Ct)).Ok);
    }

    [Fact]
    public void CpuPolicy_Equivalent_DifferentPlansDiffer()
    {
        var a = PlanSettingAccess.Value(FakePowerSchemes.Balanced, 80);
        var b = PlanSettingAccess.Value(FakePowerSchemes.Saver, 80);
        Assert.False(new CpuPolicyModule(_schemes).Equivalent("plan/active/ac/maxProcessorState", a, b));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"maxStateAc":0}""")]
    [InlineData("""{"maxStateAc":100.5}""")]
    [InlineData("""{"boostDc":"turbo"}""")]
    [InlineData("""{"plan":"saver","maxStateAc":50}""")]
    public void CpuPolicy_Validate_Refuses(string json) =>
        Assert.NotEmpty(new CpuPolicyModule(_schemes).Validate((JsonObject)JsonNode.Parse(json)!));

    // ---- display.offTimeout -------------------------------------------------------------------------------------------

    [Fact]
    public async Task DisplayOff_MinutesWrittenAsSeconds_ReadBackEquivalent()
    {
        var m = new DisplayOffTimeoutModule(_schemes);
        var intents = m.Expand(new JsonObject { ["acMinutes"] = 15, ["dcMinutes"] = 0 });
        Assert.Equal(["display/ac/offTimeout", "display/dc/offTimeout"], intents.Select(i => i.Key));
        foreach (var intent in intents)
        {
            var written = await m.ApplyAsync(intent, Ct);
            Assert.True(written.Ok, written.Error);
            Assert.True(m.Equivalent(intent.Key, intent.Target, written.ReadBack));
        }
        Assert.Equal(900u, _schemes.Values[(FakePowerSchemes.Balanced, PowerSetting.DisplayOff.Setting, PowerSource.Ac)]);
        Assert.Equal(0u, _schemes.Values[(FakePowerSchemes.Balanced, PowerSetting.DisplayOff.Setting, PowerSource.Dc)]);
    }

    [Fact]
    public async Task DisplayOff_WriteRefused_Failed()
    {
        _schemes.FailWrite = "PowerWriteValueIndex failed: Windows error 5";
        var m = new DisplayOffTimeoutModule(_schemes);
        var r = await m.ApplyAsync(m.Expand(new JsonObject { ["acMinutes"] = 1 })[0], Ct);
        Assert.False(r.Ok);
        Assert.Contains("error 5", r.Error, StringComparison.Ordinal);
    }

    // ---- gpu.powerLimit -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task GpuLimit_PercentOfDefault_SetThroughBrokerAndRestored()
    {
        var m = new GpuPowerLimitModule(_smi, _broker);
        var intent = Assert.Single(m.Expand(new JsonObject { ["gpu"] = "0", ["percent"] = 70 }));
        Assert.Equal("gpu/0/powerLimit", intent.Key);
        var (baseline, written) = await ApplyAsync(m, intent);
        Assert.True(written.Ok, written.Error);
        Assert.Equal(210.0, written.ReadBack!["watts"]!.GetValue<double>());
        Assert.Equal(["gpu.setPowerLimit {\"gpu\":\"0\",\"watts\":210}"], _broker.Calls);
        Assert.True(m.Equivalent(intent.Key, intent.Target, written.ReadBack));
        Assert.False(m.Equivalent(intent.Key, intent.Target, baseline.Value));
        Assert.True((await m.ApplyAsync(new ResourceIntent(intent.Key, baseline.Value), Ct)).Ok);
        Assert.Equal(300.0, _smi.Gpus[0].Watts);
    }

    [Fact]
    public async Task GpuLimit_LimitMin_SetsTheCardsMinimumThroughBroker()
    {
        var m = new GpuPowerLimitModule(_smi, _broker);
        var intent = Assert.Single(m.Expand(new JsonObject { ["gpu"] = "0", ["limit"] = "min" }));

        var (baseline, written) = await ApplyAsync(m, intent);

        Assert.True(written.Ok, written.Error);
        Assert.Equal(["gpu.setPowerLimit {\"gpu\":\"0\",\"watts\":100}"], _broker.Calls);
        Assert.True(m.Equivalent(intent.Key, intent.Target, written.ReadBack));
        Assert.False(m.Equivalent(intent.Key, intent.Target, baseline.Value));
    }

    [Fact]
    public async Task GpuLimit_OutsideRange_FailsWithoutCallingBroker()
    {
        var m = new GpuPowerLimitModule(_smi, _broker);
        var r = await m.ApplyAsync(new ResourceIntent("gpu/0/powerLimit", new JsonObject { ["watts"] = 50 }), Ct);
        Assert.False(r.Ok);
        Assert.Empty(_broker.Calls);
    }

    [Fact]
    public async Task GpuLimit_BrokerRefuses_Failed()
    {
        var m = new GpuPowerLimitModule(_smi, new FakeAdminBroker((_, _) => new BrokerResult(false, "refused: no policy", null)));
        var r = await m.ApplyAsync(new ResourceIntent("gpu/0/powerLimit", new JsonObject { ["watts"] = 200 }), Ct);
        Assert.False(r.Ok);
        Assert.Contains("no policy", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GpuLimit_NvidiaSmiFails_ReadFailedAndUnavailable()
    {
        _smi.FailQuery = true;
        var m = new GpuPowerLimitModule(_smi, _broker);
        Assert.False((await m.ReadAsync("gpu/0/powerLimit", Ct)).Ok);
        Assert.False((await m.AvailableAsync(Ct)).Available);
        Assert.False((await m.DiscoverAsync(Ct)).ContainsKey("gpus"));
    }

    [Theory]
    [InlineData("""{"gpu":"0"}""")]
    [InlineData("""{"gpu":"0","percent":70,"watts":200}""")]
    [InlineData("""{"gpu":"x","percent":70}""")]
    [InlineData("""{"gpu":"0","percent":5}""")]
    [InlineData("""{"gpu":"0","limit":"max"}""")]
    [InlineData("""{"gpu":"0","limit":"min","watts":200}""")]
    public void GpuLimit_Validate_Refuses(string json) =>
        Assert.NotEmpty(new GpuPowerLimitModule(_smi, _broker).Validate((JsonObject)JsonNode.Parse(json)!));

    [Fact]
    public void NvidiaSmiParse_SkipsGpuWithoutPowerManagement_FailsOnGarbage()
    {
        var ok = NvidiaSmi.Parse(["0, NVIDIA GeForce Test, 285.00, 100.00, 350.00, 300.00", "1, Old Card, [N/A], [N/A], [N/A], [N/A]"]);
        var gpu = Assert.Single(ok.Gpus!);
        Assert.Equal(("0", 285.0, 100.0, 350.0, 300.0), (gpu.Id, gpu.Watts, gpu.Min, gpu.Max, gpu.Default));
        Assert.Null(NvidiaSmi.Parse(["Failed to initialize NVML: Driver/library version mismatch"]).Gpus);
    }

    // ---- every module -------------------------------------------------------------------------------------------------

    [Fact]
    public void All_DefaultsValidateAndExpand()
    {
        foreach (var m in PowerModules.All(_schemes, _smi, _broker))
        {
            var d = m.Descriptor;
            Assert.Empty(m.Validate((JsonObject)d.DefaultSettings.DeepClone()));
            var keys = m.Expand((JsonObject)d.DefaultSettings.DeepClone()).Select(i => i.Key).ToList();
            Assert.NotEmpty(keys);
            Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        }
    }
}
