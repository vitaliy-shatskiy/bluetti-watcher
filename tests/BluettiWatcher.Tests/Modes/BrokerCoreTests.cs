using System.Text.Json.Nodes;
using BluettiWatcher.Modes.Broker;

namespace BluettiWatcher.Tests.Modes;

/// <summary>The broker's default deny: every refusal the policy promises, each with the machine left untouched.</summary>
public sealed class BrokerCoreTests : IDisposable
{
    private const string User = "S-1-5-21-1000000001-1000000002-1000000003-1001";
    private const string Stranger = "S-1-5-21-1000000001-1000000002-1000000003-1002";
    private const string Admins = "S-1-5-32-544";
    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bw-broker-core-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServiceControl _services = new();
    private readonly FakeNvidiaSmi _gpu = new();
    private readonly FakeProfileImporter _importer = new();
    private readonly FakeTaskControl _tasks = new();
    private readonly ManualClock _clock = new(T0);
    private readonly BrokerCore _core;

    public BrokerCoreTests()
    {
        _services.State["SomeService"] = "running";
        _services.State["OtherService"] = "running";
        Directory.CreateDirectory(Path.Combine(_root, "profiles"));
        File.WriteAllText(Path.Combine(_root, "profiles", "eco.nip"), "<profiles/>");
        _tasks.Enabled["\\Vendor\\SomeTask"] = true;
        _tasks.Enabled["\\Vendor\\OtherTask"] = true;
        _core = new BrokerCore(new BrokerPaths(_root, Path.Combine(_root, "exchange")), _services, _tasks, _gpu, _importer, _clock);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Inspector => Path.Combine(_root, "npi", "nvidiaProfileInspector.exe");

    private BrokerPolicy Policy() => new(User, ["SomeService"], ["\\Vendor\\SomeTask"], ["eco.nip"], Inspector);

    private static string Id => "0123456789abcdef0123456789abcdef";

    private static string RequestText(string op, JsonObject args, DateTimeOffset issued, string id = "0123456789abcdef0123456789abcdef") =>
        new BrokerRequest(id, op, args, issued).ToJson();

    private Task<BrokerAnswer> Run(string op, JsonObject args, BrokerPolicy? policy = null, string owner = User, bool plain = true, DateTimeOffset? issued = null) =>
        _core.HandleAsync($"{Id}.json", new SecuredFile(RequestText(op, args, issued ?? T0), owner, plain, []), policy ?? Policy(), "", CancellationToken.None);

    private static void AssertRefused(BrokerAnswer a, string why)
    {
        Assert.False(a.Result.Ok);
        Assert.StartsWith("refused: ", a.Result.Error, StringComparison.Ordinal);
        Assert.Contains(why, a.Result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handle_ListedServiceStop_StopsItAndReadsBack()
    {
        var a = await Run("service.stop", new JsonObject { ["name"] = "someservice" });
        Assert.True(a.Result.Ok, a.Result.Error);
        Assert.Equal("stopped", a.Result.Value!.GetValue<string>());
        Assert.Equal(["stop SomeService"], _services.Calls);   // the policy's spelling reaches the SCM
    }

    [Fact]
    public async Task Handle_UnlistedService_RefusedUntouched()
    {
        var a = await Run("service.stop", new JsonObject { ["name"] = "OtherService" });
        AssertRefused(a, "not in the policy");
        Assert.Empty(_services.Calls);
    }

    [Theory]
    [InlineData("Some Service")]
    [InlineData("..\\SomeService")]
    [InlineData("SomeService*")]
    [InlineData("SomeService\" & calc")]
    public async Task Handle_MalformedServiceName_Refused(string name)
    {
        var a = await Run("service.start", new JsonObject { ["name"] = name });
        AssertRefused(a, "not a service key name");
        Assert.Empty(_services.Calls);
    }

    [Fact]
    public async Task Handle_UnknownOperation_Refused()
    {
        var a = await Run("service.delete", new JsonObject { ["name"] = "SomeService" });
        AssertRefused(a, "unknown operation 'service.delete'");
        Assert.Empty(_services.Calls);
    }

    [Fact]
    public async Task Handle_MissingArgument_Refused()
    {
        AssertRefused(await Run("service.stop", []), "missing argument 'name'");
        Assert.Empty(_services.Calls);
    }

    [Fact]
    public async Task Handle_ExtraArgument_Refused()
    {
        AssertRefused(await Run("service.stop", new JsonObject { ["name"] = "SomeService", ["force"] = true }), "unexpected argument 'force'");
        Assert.Empty(_services.Calls);
    }

    [Fact]
    public async Task Handle_ArgumentOfWrongType_Refused()
    {
        AssertRefused(await Run("service.stop", new JsonObject { ["name"] = 5 }), "not a service key name");
        Assert.Empty(_services.Calls);
    }

    [Theory]
    [InlineData(99.0)]
    [InlineData(351.0)]
    [InlineData(-1.0)]
    public async Task Handle_GpuLimitOutsideRange_RefusedUnset(double watts)
    {
        var a = await Run("gpu.setPowerLimit", new JsonObject { ["gpu"] = "0", ["watts"] = watts });
        AssertRefused(a, "outside GPU 0's 100..350 W");
        Assert.DoesNotContain(_gpu.Calls, c => c.StartsWith("set", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Handle_GpuLimitInRange_SetsAndReadsBack()
    {
        var a = await Run("gpu.setPowerLimit", new JsonObject { ["gpu"] = "0", ["watts"] = 200.0 });
        Assert.True(a.Result.Ok, a.Result.Error);
        Assert.Equal(200.0, a.Result.Value!["watts"]!.GetValue<double>());
        Assert.Contains("set 0 200", _gpu.Calls);
    }

    [Fact]
    public async Task Handle_GpuLimitWhenLimitsUnreadable_Refused()
    {
        _gpu.FailQuery = true;
        AssertRefused(await Run("gpu.setPowerLimit", new JsonObject { ["gpu"] = "0", ["watts"] = 200.0 }), "could not be read");
        Assert.DoesNotContain(_gpu.Calls, c => c.StartsWith("set", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0 -pl 999")]
    public async Task Handle_UnknownOrMalformedGpu_Refused(string gpu)
    {
        var a = await Run("gpu.setPowerLimit", new JsonObject { ["gpu"] = gpu, ["watts"] = 200.0 });
        Assert.False(a.Result.Ok);
        Assert.StartsWith("refused: ", a.Result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(_gpu.Calls, c => c.StartsWith("set", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Handle_NonFiniteWatts_Refused()
    {
        var args = (JsonObject)JsonNode.Parse("""{"gpu":"0","watts":"NaN"}""")!;
        AssertRefused(await Run("gpu.setPowerLimit", args), "watts is not a number");
    }

    [Fact]
    public async Task Handle_ListedProfile_ImportsFromProfilesFolder()
    {
        var a = await Run("nvidia.applyProfile", new JsonObject { ["file"] = "eco.nip" });
        Assert.True(a.Result.Ok, a.Result.Error);
        Assert.Equal([$"{Inspector} {Path.Combine(_root, "profiles", "eco.nip")}"], _importer.Calls);
    }

    [Theory]
    [InlineData("other.nip", "not in the policy")]
    [InlineData("..\\eco.nip", "not a .nip file name")]
    [InlineData("C:\\eco.nip", "not a .nip file name")]
    [InlineData("eco.exe", "not a .nip file name")]
    public async Task Handle_ProfileNotListedOrOutsideFolder_Refused(string profile, string why)
    {
        // A name the policy lists (written past Parse) is still refused when it is no plain .nip name.
        var policy = profile == "other.nip" ? Policy() : Policy() with { Profiles = ["eco.nip", profile] };
        AssertRefused(await Run("nvidia.applyProfile", new JsonObject { ["file"] = profile }, policy), why);
        Assert.Empty(_importer.Calls);
    }

    [Fact]
    public async Task Handle_InspectorWhereUserWrites_Refused()
    {
        var policy = Policy() with { ProfileInspector = Path.Combine(Path.GetTempPath(), "npi.exe") };
        AssertRefused(await Run("nvidia.applyProfile", new JsonObject { ["file"] = "eco.nip" }, policy), "not under Program Files");
        Assert.Empty(_importer.Calls);
    }

    // ---- service.setStartType, task.enable / task.disable, nvidia.exportProfiles, exports ------------------------------

    [Fact]
    public async Task Handle_SetStartTypeListedService_SetsAndReadsBack()
    {
        var a = await Run("service.setStartType", new JsonObject { ["name"] = "SomeService", ["startType"] = "disabled" });
        Assert.True(a.Result.Ok, a.Result.Error);
        Assert.Equal("disabled", a.Result.Value!.GetValue<string>());
        Assert.Equal(["startType SomeService disabled"], _services.Calls);
    }

    [Fact]
    public async Task Handle_SetStartTypeUnlistedService_Refused()
    {
        AssertRefused(await Run("service.setStartType", new JsonObject { ["name"] = "OtherService", ["startType"] = "disabled" }), "not in the policy");
        Assert.Empty(_services.Calls);
    }

    [Theory]
    [InlineData("boot")]
    [InlineData("system")]
    [InlineData("Disabled ")]
    public async Task Handle_SetStartTypeUnknownType_Refused(string startType)
    {
        AssertRefused(await Run("service.setStartType", new JsonObject { ["name"] = "SomeService", ["startType"] = startType }), "startType");
        Assert.Empty(_services.Calls);
    }

    [Theory]
    [InlineData("task.disable", false)]
    [InlineData("task.enable", true)]
    public async Task Handle_ListedTask_SwitchedAndReadBack(string op, bool enabled)
    {
        var a = await Run(op, new JsonObject { ["name"] = "\\vendor\\sometask" });
        Assert.True(a.Result.Ok, a.Result.Error);
        Assert.Equal(enabled, _tasks.Enabled["\\Vendor\\SomeTask"]);
        Assert.Equal([$"{(enabled ? "enable" : "disable")} \\Vendor\\SomeTask"], _tasks.Calls);   // the policy's spelling
    }

    [Fact]
    public async Task Handle_UnlistedTask_Refused()
    {
        AssertRefused(await Run("task.disable", new JsonObject { ["name"] = "\\Vendor\\OtherTask" }), "not in the policy");
        Assert.Empty(_tasks.Calls);
    }

    [Theory]
    [InlineData("\\Vendor\\..\\SomeTask")]
    [InlineData("\\Vendor\\SomeTask\" /tr calc")]
    [InlineData("\\Vendor\\*")]
    [InlineData("")]
    public async Task Handle_MalformedTaskName_Refused(string name)
    {
        var policy = Policy() with { Tasks = ["\\Vendor\\SomeTask", name] };
        AssertRefused(await Run("task.enable", new JsonObject { ["name"] = name }, policy), "not a task name");
        Assert.Empty(_tasks.Calls);
    }

    [Fact]
    public async Task Handle_ExportProfiles_MovedIntoExportsWithHash()
    {
        var a = await Run("nvidia.exportProfiles", []);
        Assert.True(a.Result.Ok, a.Result.Error);
        Assert.Equal("exports/customized_20261120.nip", a.Result.Value!["file"]!.GetValue<string>());
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("<profiles/>"u8)), a.Result.Value["sha256"]!.GetValue<string>());
    }

    [Fact]
    public async Task Handle_ExportProfilesWithArgument_Refused()
    {
        AssertRefused(await Run("nvidia.exportProfiles", new JsonObject { ["to"] = "C:\\Windows" }), "unexpected argument 'to'");
        Assert.Empty(_importer.Calls);
    }

    [Fact]
    public async Task Handle_ExportProfilesWithoutInspector_Refused()
    {
        AssertRefused(await Run("nvidia.exportProfiles", [], Policy() with { ProfileInspector = "" }), "no NVIDIA Profile Inspector");
        Assert.Empty(_importer.Calls);
    }

    [Fact]
    public async Task Handle_ApplyExportedProfile_ImportsFromExports()
    {
        Directory.CreateDirectory(Path.Combine(_root, "exports"));
        await File.WriteAllTextAsync(Path.Combine(_root, "exports", "before.nip"), "<profiles/>", TestContext.Current.CancellationToken);
        var a = await Run("nvidia.applyProfile", new JsonObject { ["file"] = "exports/before.nip" });
        Assert.True(a.Result.Ok, a.Result.Error);
        Assert.Equal([$"{Inspector} {Path.Combine(_root, "exports", "before.nip")}"], _importer.Calls);
    }

    [Theory]
    [InlineData("exports/../policy.json")]
    [InlineData("exports/sub/x.nip")]
    [InlineData("policy.json")]
    [InlineData("requests/x.nip")]
    public async Task Handle_ApplyProfileOutsideTheTwoFolders_Refused(string file)
    {
        AssertRefused(await Run("nvidia.applyProfile", new JsonObject { ["file"] = file }), "not a .nip file name");
        Assert.Empty(_importer.Calls);
    }

    [Fact]
    public async Task Handle_RequestOwnedByAnotherUser_Refused()
    {
        AssertRefused(await Run("service.stop", new JsonObject { ["name"] = "SomeService" }, owner: Stranger), "not the policy's user");
        Assert.Empty(_services.Calls);
    }

    [Fact]
    public async Task Handle_RequestOwnedByAdministrators_Runs()
    {
        var a = await Run("service.query", new JsonObject { ["name"] = "SomeService" }, owner: Admins);
        Assert.True(a.Result.Ok, a.Result.Error);
    }

    [Fact]
    public async Task Handle_RequestThatIsALink_Refused()
    {
        AssertRefused(await Run("service.stop", new JsonObject { ["name"] = "SomeService" }, plain: false), "a link");
        Assert.Empty(_services.Calls);
    }

    [Fact]
    public async Task Handle_StaleRequest_Refused()
    {
        AssertRefused(await Run("service.stop", new JsonObject { ["name"] = "SomeService" }, issued: T0 - TimeSpan.FromSeconds(31)), "not within 30 s");
        Assert.Empty(_services.Calls);
    }

    [Fact]
    public async Task Handle_RequestFromTheFuture_Refused()
    {
        AssertRefused(await Run("service.stop", new JsonObject { ["name"] = "SomeService" }, issued: T0 + TimeSpan.FromMinutes(5)), "not within 30 s");
        Assert.Empty(_services.Calls);
    }

    [Fact]
    public async Task Handle_IdNotTheFileName_Refused()
    {
        string text = RequestText("service.stop", new JsonObject { ["name"] = "SomeService" }, T0, id: "ffffffffffffffffffffffffffffffff");
        var a = await _core.HandleAsync($"{Id}.json", new SecuredFile(text, User, true, []), Policy(), "", CancellationToken.None);
        AssertRefused(a, "not its file name");
        Assert.Empty(_services.Calls);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1]")]
    [InlineData("""{"v":2,"id":"0123456789abcdef0123456789abcdef","op":"service.stop","args":{"name":"SomeService"},"issued":0}""")]
    [InlineData("""{"v":1,"id":"0123456789abcdef0123456789abcdef","op":"service.stop","args":{"name":"SomeService"},"issued":1795176000000,"sudo":true}""")]
    public async Task Handle_MalformedRequest_Refused(string text)
    {
        var a = await _core.HandleAsync($"{Id}.json", new SecuredFile(text, User, true, []), Policy(), "", CancellationToken.None);
        Assert.False(a.Result.Ok);
        Assert.StartsWith("refused: ", a.Result.Error, StringComparison.Ordinal);
        Assert.Empty(_services.Calls);
    }

    [Fact]
    public async Task Handle_OversizedRequest_Refused()
    {
        string text = RequestText("service.stop", new JsonObject { ["name"] = "SomeService", ["pad"] = new string('x', 5000) }, T0);
        var a = await _core.HandleAsync($"{Id}.json", new SecuredFile(text, User, true, []), Policy(), "", CancellationToken.None);
        AssertRefused(a, "too large");
    }

    [Theory]
    [InlineData("..\\policy.json")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF.json")]
    [InlineData("0123456789abcdef0123456789abcdef.json.tmp")]
    public async Task Handle_FileNameNotARequest_RefusedWithoutAnId(string name)
    {
        var a = await _core.HandleAsync(name, new SecuredFile("{}", User, true, []), Policy(), "", CancellationToken.None);
        Assert.Null(a.Id);
        Assert.False(a.Result.Ok);
    }

    [Fact]
    public async Task Handle_UnreadableRequestFile_Refused()
    {
        var a = await _core.HandleAsync($"{Id}.json", null, Policy(), "", CancellationToken.None);
        AssertRefused(a, "could not be opened");
    }

    [Fact]
    public async Task Handle_NoPolicy_RefusesEverything()
    {
        var policy = BrokerCore.LoadPolicy(null, out string error);
        Assert.Null(policy);
        var a = await _core.HandleAsync($"{Id}.json", new SecuredFile(RequestText("service.query", new JsonObject { ["name"] = "SomeService" }, T0), User, true, []), policy, error, CancellationToken.None);
        AssertRefused(a, "no readable policy file");
        Assert.Empty(_services.Calls);
    }

    [Fact]
    public void LoadPolicy_WritableByTheUser_Denied()
    {
        var file = new SecuredFile(Policy().ToJson(), Admins, true, [User]);
        Assert.Null(BrokerCore.LoadPolicy(file, out string error));
        Assert.Contains("writable by", error, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadPolicy_OwnedByTheUser_Denied()
    {
        Assert.Null(BrokerCore.LoadPolicy(new SecuredFile(Policy().ToJson(), User, true, []), out string error));
        Assert.Contains("not an administrator", error, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadPolicy_ALink_Denied()
    {
        Assert.Null(BrokerCore.LoadPolicy(new SecuredFile(Policy().ToJson(), Admins, false, []), out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"version":1,"userSid":"S-1-5-21-1-2-3-1001","services":["Some Service"]}""")]
    [InlineData("""{"version":1,"userSid":"S-1-5-21-1-2-3-1001","services":[],"allowAll":true}""")]
    [InlineData("""{"version":1,"userSid":"S-1-5-21-1-2-3-1001","profileInspector":"npi.exe"}""")]
    [InlineData("""{"version":1,"userSid":"everyone"}""")]
    public void LoadPolicy_Malformed_Denied(string text)
    {
        Assert.Null(BrokerCore.LoadPolicy(new SecuredFile(text, Admins, true, []), out string error));
        Assert.EndsWith("every request is refused", error, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadPolicy_TrustedAndValid_RoundTrips()
    {
        var policy = BrokerCore.LoadPolicy(new SecuredFile(Policy().ToJson(), Admins, true, []), out string error);
        Assert.NotNull(policy);
        Assert.Equal("", error);
        Assert.Equal(["SomeService"], policy.Services);
        Assert.Equal(Inspector, policy.ProfileInspector);
    }
}
