using System.Security.Principal;
using System.Text.Json.Nodes;
using BluettiWatcher.Io;
using BluettiWatcher.Modes.Broker;

namespace BluettiWatcher.Tests.Modes;

/// <summary>A broker folder in a temp directory whose files all count as plain and owned by <see cref="Owner"/>.</summary>
internal sealed class DirectoryInbox(BrokerPaths paths, string owner) : IBrokerInbox
{
    public string Owner { get; } = owner;

    public SecuredFile? ReadPolicy() => File.Exists(paths.Policy) ? new SecuredFile(File.ReadAllText(paths.Policy), "S-1-5-32-544", true, []) : null;

    public IReadOnlyList<string> PendingRequests() => [.. Directory.EnumerateFiles(paths.Requests).Select(f => Path.GetFileName(f))];

    public SecuredFile? TakeRequest(string fileName)
    {
        string f = Path.Combine(paths.Requests, fileName);
        string text = File.ReadAllText(f);
        File.Delete(f);
        return new SecuredFile(text, Owner, true, []);
    }

    public void WriteResponse(string id, string json) => File.WriteAllText(Path.Combine(paths.Responses, id + ".json"), json);
}

/// <summary>schtasks as the tests see it: <c>/run</c> runs the given action (the broker task's run) and answers its exit code.</summary>
internal sealed class TaskStarter(Func<Task> onRun, int exit = 0) : IProcessRunner
{
    public List<string> Calls { get; } = [];

    public async Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, IReadOnlyDictionary<string, string>? env = null)
    {
        Calls.Add($"{Path.GetFileName(exe)} {string.Join(' ', args)}");
        if (exit == 0) await onRun();
        return new ProcessResult(exit, exit == 0 ? ["SUCCESS"] : ["ERROR: The system cannot find the file specified."], false);
    }
}

/// <summary>Client to broker run to answer, through a temp folder: the request file, the task start, the response.</summary>
public sealed class BrokerClientTests : IDisposable
{
    private const string User = "S-1-5-21-1000000001-1000000002-1000000003-1001";
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "bw-broker-client-" + Guid.NewGuid().ToString("N"));
    private readonly BrokerPaths _paths = new(Dir, Dir);
    private readonly FakeServiceControl _services = new();
    private readonly List<string> _log = [];

    public BrokerClientTests()
    {
        Directory.CreateDirectory(_paths.Requests);
        Directory.CreateDirectory(_paths.Responses);
        File.WriteAllText(_paths.Policy, new BrokerPolicy(User, ["SomeService"], [], [], "").ToJson());
        _services.State["SomeService"] = "running";
    }

    public void Dispose() => Directory.Delete(_paths.Root, recursive: true);

    private BrokerRunner Runner(string owner = User) =>
        new(new DirectoryInbox(_paths, owner), new BrokerCore(_paths, _services, new FakeTaskControl(), new FakeNvidiaSmi(), new FakeProfileImporter(), TimeProvider.System), _log.Add);

    [Fact]
    public async Task Run_AllowedStop_AnsweredWithReadBack()
    {
        var starter = new TaskStarter(() => Runner().RunAsync(CancellationToken.None));
        var r = await new BrokerClient(_paths, starter, TimeProvider.System).RunAsync("service.stop", new JsonObject { ["name"] = "SomeService" }, CancellationToken.None);
        Assert.True(r.Ok, r.Error);
        Assert.Equal("stopped", r.Value!.GetValue<string>());
        Assert.Equal(["schtasks.exe /run /tn BluettiWatcherBroker"], starter.Calls);
        Assert.Empty(Directory.EnumerateFiles(_paths.Requests));
        Assert.Contains(_log, l => l.Contains("service.stop: ok", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_RefusedRequest_RefusalReachesTheCaller()
    {
        var starter = new TaskStarter(() => Runner().RunAsync(CancellationToken.None));
        var r = await new BrokerClient(_paths, starter, TimeProvider.System).RunAsync("service.stop", new JsonObject { ["name"] = "OtherService" }, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("not in the policy", r.Error, StringComparison.Ordinal);
        Assert.Empty(_services.Calls);
    }

    [Fact]
    public async Task Run_PolicyMissing_EveryRequestRefused()
    {
        File.Delete(_paths.Policy);
        var starter = new TaskStarter(() => Runner().RunAsync(CancellationToken.None));
        var r = await new BrokerClient(_paths, starter, TimeProvider.System).RunAsync("service.query", new JsonObject { ["name"] = "SomeService" }, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("no readable policy file", r.Error, StringComparison.Ordinal);
        Assert.Empty(_services.Calls);
    }

    [Fact]
    public async Task Run_TaskNotRegistered_FailsAndRemovesRequest()
    {
        var starter = new TaskStarter(() => Task.CompletedTask, exit: 1);
        var r = await new BrokerClient(_paths, starter, TimeProvider.System).RunAsync("gpu.query", [], CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("could not be started", r.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(_paths.Requests));
    }

    [Fact]
    public async Task Run_NoAnswer_FailsAfterTheBoundedWait()
    {
        var starter = new TaskStarter(() => Task.CompletedTask);
        var r = await new BrokerClient(_paths, starter, TimeProvider.System, TimeSpan.FromMilliseconds(200)).RunAsync("gpu.query", [], CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("did not answer within 0.2 s", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runner_HalfWrittenTmp_LeftAlone()
    {
        await File.WriteAllTextAsync(Path.Combine(_paths.Requests, "0123456789abcdef0123456789abcdef.json.tmp"), "{", TestContext.Current.CancellationToken);
        await Runner().RunAsync(CancellationToken.None);
        Assert.Single(Directory.EnumerateFiles(_paths.Requests));
        Assert.Empty(Directory.EnumerateFiles(_paths.Responses));
    }

    [WindowsFact]
    public void WindowsInbox_TakeRequest_ReportsOwnerAndDeletes()
    {
        string name = "0123456789abcdef0123456789abcdef.json";
        File.WriteAllText(Path.Combine(_paths.Requests, name), "{}");
        var file = new WindowsBrokerInbox(_paths, TimeProvider.System).TakeRequest(name);
        Assert.NotNull(file);
        using var me = WindowsIdentity.GetCurrent();
        // An elevated test run owns new files as Administrators; otherwise as the user.
        Assert.Contains(file.OwnerSid, new[] { me.User!.Value, "S-1-5-32-544" });
        Assert.True(file.Plain);
        Assert.Equal("{}", file.Text);
        Assert.False(File.Exists(Path.Combine(_paths.Requests, name)));
    }

    [WindowsFact]
    public void WindowsInbox_PolicyWritableByUser_ReportsTheWriter()
    {
        var policy = new WindowsBrokerInbox(_paths, TimeProvider.System).ReadPolicy();
        Assert.NotNull(policy);
        // A temp folder's file is writable by its creator: the broker would refuse such a policy.
        Assert.Null(BrokerCore.LoadPolicy(policy, out string error));
        Assert.Contains("every request is refused", error, StringComparison.Ordinal);
    }
}
