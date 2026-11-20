using System.Text;
using System.Text.Json;
using BluettiWatcher.Http;
using BluettiWatcher.Modes;
using BluettiWatcher.Modes.Broker;
using BluettiWatcher.Modes.Modules;

namespace BluettiWatcher.Tests.Modes;

/// <summary>
/// An entry the admin broker's policy does not allow (only an elevated --broker-allow adds one) is said where the person
/// acts: the mode save answers with a warning per entry and the elevated command, and the mode's problems name it on entry.
/// </summary>
public sealed class ModesBrokerPolicyTests : IDisposable
{
    private const string Quiet = """
        {"id":"quiet","name":"Quiet","flagWord":"eco","actions":[
          {"typeId":"service.state","enabled":true,"settings":{"services":[{"name":"Spooler","running":false},{"name":"WSearch","running":false}]}},
          {"typeId":"scheduledTask.state","enabled":true,"settings":{"tasks":[{"path":"\\Vendor\\My Task","enabled":false}]}}]}
        """;

    private readonly string _dir = Directory.CreateTempSubdirectory("bw-modes-policy-").FullName;
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly StationConfig _config;
    private readonly ModesEngine _engine;
    private readonly ModesApi _api;
    private readonly List<string> _log = [];
    private BrokerPolicy? _policy = new("S-1-5-21-1-2-3-1001", ["WSearch"], [], [], "");

    public ModesBrokerPolicyTests()
    {
        _config = new StationConfig { DataDir = _dir, PowerMode = new PowerModeConfig { FlagPath = Path.Combine(_dir, "flag", "power-mode.json") } };
        var services = new FakeServiceReader();
        var tasks = new FakeTaskScheduler();
        var broker = new FakeBroker();
        _engine = new ModesEngine(_config, new ModuleRegistry([new ServiceStateModule(services, broker, new JumpClock()), new ScheduledTaskModule(tasks, broker)]),
            new ModesStore(_dir), new ModeJournal(Path.Combine(_dir, ModeJournal.FileName)), new FakeFlagWriter(), _clock, _log.Add)
        {
            ReadPolicy = () => _policy,
            ExePath = @"C:\Apps\BluettiWatcher\BluettiWatcher.exe",
        };
        _engine.Start(_clock.Now.AddDays(-1));
        _api = new ModesApi(_config, _engine);
    }

    public void Dispose()
    {
        _engine.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private HttpRequest Req(string method, string path, string body = "") =>
        new(method, path, new Dictionary<string, string>(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["host"] = $"127.0.0.1:{_config.Port}", ["x-station"] = "1" },
            Encoding.UTF8.GetBytes(body));

    private static JsonElement Json(HttpResponse r) => JsonDocument.Parse(r.Body).RootElement.Clone();

    [Fact]
    public async Task SaveMode_EntriesTheBrokerPolicyDoesNotAllow_Answers200WithAWarningAndTheElevatedCommandEach()
    {
        var r = await _api.HandleAsync(Req("POST", "/api/modes", Quiet), TestContext.Current.CancellationToken);

        Assert.Equal(200, r.Status);
        var warnings = Json(r).GetProperty("warnings").EnumerateArray().ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Equal((0, "service", "Spooler"), (warnings[0].GetProperty("action").GetInt32(), warnings[0].GetProperty("kind").GetString(), warnings[0].GetProperty("name").GetString()));
        Assert.Equal("""& "C:\Apps\BluettiWatcher\BluettiWatcher.exe" --broker-allow service "Spooler" """.TrimEnd(), warnings[0].GetProperty("command").GetString());
        Assert.Equal((1, "task", @"\Vendor\My Task"), (warnings[1].GetProperty("action").GetInt32(), warnings[1].GetProperty("kind").GetString(), warnings[1].GetProperty("name").GetString()));
        Assert.Contains(@"--broker-allow task ""\Vendor\My Task""", warnings[1].GetProperty("command").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveMode_EveryEntryAllowed_HasNoWarnings()
    {
        _policy = new("S-1-5-21-1-2-3-1001", ["spooler", "WSearch"], [@"\Vendor\My Task"], [], "");

        var r = await _api.HandleAsync(Req("POST", "/api/modes", Quiet), TestContext.Current.CancellationToken);

        Assert.Equal(200, r.Status);
        Assert.Empty(Json(r).GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public async Task SelectMode_EntryNotAllowed_TheViewsProblemsNameItWithTheCommand()
    {
        Assert.Equal(200, (await _api.HandleAsync(Req("POST", "/api/modes", Quiet), TestContext.Current.CancellationToken)).Status);

        await _engine.SelectAsync("quiet", TestContext.Current.CancellationToken);

        var problems = Json(await _api.HandleAsync(Req("GET", "/api/modes"), TestContext.Current.CancellationToken)).GetProperty("problems")
            .EnumerateArray().Select(p => p.GetString() ?? "").ToList();
        Assert.Contains(problems, p => p.Contains("service 'Spooler'", StringComparison.Ordinal) && p.Contains("--broker-allow service \"Spooler\"", StringComparison.Ordinal));
        Assert.Contains(_log, l => l.Contains("Spooler", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SelectMode_PolicyThenAllowsIt_TheProblemIsGoneOnTheNextPass()
    {
        Assert.Equal(200, (await _api.HandleAsync(Req("POST", "/api/modes", Quiet), TestContext.Current.CancellationToken)).Status);
        await _engine.SelectAsync("quiet", TestContext.Current.CancellationToken);
        _policy = new("S-1-5-21-1-2-3-1001", ["Spooler", "WSearch"], [@"Vendor\My Task"], [], "");

        await _engine.StepAsync(new SelectorInput(null, null, null, null, _clock.GetLocalNow()), emergency: false, TestContext.Current.CancellationToken);

        Assert.Empty(_engine.Problems);
    }
}
