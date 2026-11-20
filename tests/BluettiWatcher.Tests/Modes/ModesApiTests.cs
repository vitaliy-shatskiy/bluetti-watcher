using System.Text;
using System.Text.Json;
using BluettiWatcher.Http;
using BluettiWatcher.Modes;

namespace BluettiWatcher.Tests.Modes;

public sealed class ModesApiTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bw-modes-api-").FullName;
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeMachine _m = new();
    private readonly StationConfig _config;
    private readonly ModesEngine _engine;
    private readonly ModesApi _api;

    public ModesApiTests()
    {
        _config = new StationConfig { DataDir = _dir, PowerMode = new PowerModeConfig { FlagPath = Path.Combine(_dir, "flag", "power-mode.json") } };
        _engine = new ModesEngine(_config, new ModuleRegistry([new FakeModule(BuiltInModes.PowerPlanType, _m)]), new ModesStore(_dir),
            new ModeJournal(Path.Combine(_dir, ModeJournal.FileName)), new FakeFlagWriter(), _clock, _ => { });
        _engine.Start(_clock.Now.AddDays(-1));
        _api = new ModesApi(_config, _engine);
    }

    public void Dispose()
    {
        _engine.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private HttpRequest Req(string method, string path, string body = "", bool station = true, Dictionary<string, string>? query = null)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["host"] = $"127.0.0.1:{_config.Port}" };
        if (station) headers["x-station"] = "1";
        return new HttpRequest(method, path, query ?? [], headers, Encoding.UTF8.GetBytes(body));
    }

    private static JsonElement Json(HttpResponse r) => JsonDocument.Parse(r.Body).RootElement.Clone();

    [Theory]
    [InlineData("POST", "/api/modes/select")]
    [InlineData("PUT", "/api/modes/eco")]
    [InlineData("DELETE", "/api/modes/eco")]
    [InlineData("PUT", "/api/modes/rules")]
    [InlineData("PUT", "/api/settings/hibernate")]
    public async Task Write_WithoutStationHeader_IsForbidden(string method, string path)
    {
        var r = await _api.HandleAsync(Req(method, path, "{}", station: false), TestContext.Current.CancellationToken);
        Assert.Equal(403, r.Status);
    }

    [Fact]
    public async Task Select_Eco_AnswersTheReconciledView()
    {
        var r = await _api.HandleAsync(Req("POST", "/api/modes/select", """{"mode":"eco"}"""), TestContext.Current.CancellationToken);
        Assert.Equal(200, r.Status);
        var j = Json(r);
        Assert.Equal("eco", j.GetProperty("activeModeId").GetString());
        Assert.Equal("manual", j.GetProperty("settings").GetProperty("selection").GetProperty("kind").GetString());
        Assert.Equal("applied", j.GetProperty("actions")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Hibernate_Put_IsSavedAndReadBack()
    {
        var r = await _api.HandleAsync(Req("PUT", "/api/settings/hibernate", """{"autoHibernate":false}"""), TestContext.Current.CancellationToken);
        Assert.Equal(200, r.Status);
        Assert.False(_engine.AutoHibernate);
        Assert.False(new ModesStore(_dir).Load(() => throw new InvalidOperationException(), _clock.Now).Settings.AutoHibernate);
        Assert.Equal(400, (await _api.HandleAsync(Req("PUT", "/api/settings/hibernate", """{"autoHibernate":"no"}"""), TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Rules_PutBadMode_IsRefused()
    {
        var r = await _api.HandleAsync(Req("PUT", "/api/modes/rules", """[{"condition":{"kind":"socBelow","percent":30},"modeId":"nope"}]"""), TestContext.Current.CancellationToken);
        Assert.Equal(400, r.Status);
        Assert.Contains("rules[0].modeId", Encoding.UTF8.GetString(r.Body), StringComparison.Ordinal);
    }

    private static string ModeBody(string id, string name = "\"Quiet\"") =>
        $$"""{"id":"{{id}}","name":{{name}},"flagWord":"eco","actions":[]}""";

    [Theory]
    [InlineData("auto")]
    [InlineData("journal")]
    [InlineData("rules")]
    [InlineData("select")]
    [InlineData("catalogue")]
    public async Task Mode_CreateWithAReservedId_Is400(string id)
    {
        var r = await _api.HandleAsync(Req("POST", "/api/modes", ModeBody(id)), TestContext.Current.CancellationToken);

        Assert.Equal(400, r.Status);
        Assert.DoesNotContain(_engine.Settings.Modes, m => m.Id == id);
    }

    [Fact]
    public async Task Mode_CreateWithANullName_Is400()
    {
        var r = await _api.HandleAsync(Req("POST", "/api/modes", ModeBody("quiet", "null")), TestContext.Current.CancellationToken);

        Assert.Equal(400, r.Status);
    }

    [Fact]
    public async Task Mode_CreatePastMaxModes_IsRefused()
    {
        for (int i = _engine.Settings.Modes.Count; i < ModesValidation.MaxModes; i++)
            Assert.Equal(200, (await _api.HandleAsync(Req("POST", "/api/modes", ModeBody($"m{i}")), TestContext.Current.CancellationToken)).Status);

        var r = await _api.HandleAsync(Req("POST", "/api/modes", ModeBody("one-more")), TestContext.Current.CancellationToken);

        Assert.Equal(409, r.Status);
        Assert.Equal(ModesValidation.MaxModes, _engine.Settings.Modes.Count);
    }

    [Fact]
    public async Task Mode_CreateThenDelete_RoundTrips()
    {
        string quiet = """{"id":"quiet","name":"Quiet","flagWord":"eco","actions":[{"typeId":"windows.powerPlan","settings":{"key":"plan","value":"saver"}}]}""";
        Assert.Equal(200, (await _api.HandleAsync(Req("POST", "/api/modes", quiet), TestContext.Current.CancellationToken)).Status);
        Assert.Equal(409, (await _api.HandleAsync(Req("POST", "/api/modes", quiet), TestContext.Current.CancellationToken)).Status);
        Assert.Contains(_engine.Settings.Modes, m => m.Id == "quiet" && m.FlagWord == FlagWord.Eco);
        Assert.Equal(200, (await _api.HandleAsync(Req("DELETE", "/api/modes/quiet"), TestContext.Current.CancellationToken)).Status);
        Assert.DoesNotContain(_engine.Settings.Modes, m => m.Id == "quiet");
    }

    [Fact]
    public async Task Catalogue_ListsSchemaAvailabilityAndDiscovery()
    {
        var j = Json(await _api.HandleAsync(Req("GET", "/api/modes/catalogue"), TestContext.Current.CancellationToken));
        var m = j.GetProperty("modules")[0];
        Assert.Equal(BuiltInModes.PowerPlanType, m.GetProperty("descriptor").GetProperty("typeId").GetString());
        Assert.True(m.GetProperty("available").GetBoolean());
        Assert.Equal("a", m.GetProperty("discovery").GetProperty("things")[0].GetProperty("id").GetString());
        Assert.Equal("object", m.GetProperty("descriptor").GetProperty("settingsSchema").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Journal_Since_PagesBySequence()
    {
        await _api.HandleAsync(Req("POST", "/api/modes/select", """{"mode":"eco"}"""), TestContext.Current.CancellationToken);
        var all = Json(await _api.HandleAsync(Req("GET", "/api/modes/journal"), TestContext.Current.CancellationToken)).GetProperty("records");
        Assert.True(all.GetArrayLength() >= 3);   // mode, prepared, applied
        long first = all[0].GetProperty("seq").GetInt64();
        var after = Json(await _api.HandleAsync(Req("GET", "/api/modes/journal", query: new() { ["since"] = first.ToString(System.Globalization.CultureInfo.InvariantCulture) }), TestContext.Current.CancellationToken)).GetProperty("records");
        Assert.Equal(all.GetArrayLength() - 1, after.GetArrayLength());
        Assert.Equal(400, (await _api.HandleAsync(Req("GET", "/api/modes/journal", query: new() { ["since"] = "x" }), TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task LegacyMode_OnV2_SelectsTheBuiltIn()
    {
        var r = await _api.LegacyModeAsync(Req("POST", "/api/mode", """{"mode":"max"}"""), TestContext.Current.CancellationToken);
        Assert.Equal(200, r.Status);
        Assert.Equal("max", _engine.Settings.Selection.ManualModeId);
    }
}
