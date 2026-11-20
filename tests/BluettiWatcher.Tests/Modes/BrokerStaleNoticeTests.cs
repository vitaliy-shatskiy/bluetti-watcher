using BluettiWatcher.Modes.Broker;

namespace BluettiWatcher.Tests.Modes;

/// <summary>
/// An upgrade starts the new service seconds before the elevated install step refreshes the broker: a broker older than
/// the service is told only when it is still older after a grace, and once per service version.
/// </summary>
public sealed class BrokerStaleNoticeTests : IDisposable
{
    private static readonly BrokerStatus Stale = new("stale", "1.10.0", "the admin broker is 1.10.0, older than the service (1.10.1): run install.ps1 -Broker -Apply in an elevated shell");
    private static readonly BrokerStatus Current = new("current", "1.10.1", null);

    private readonly string _dir = Directory.CreateTempSubdirectory("bw-broker-notice-").FullName;
    private readonly List<(string Kind, string Detail)> _events = [];
    private readonly List<string> _log = [];
    private readonly List<TimeSpan> _waits = [];

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary><paramref name="afterGrace"/> is what the broker is once the grace has passed.</summary>
    private Task RunAsync(BrokerStatus atStart, BrokerStatus afterGrace, string version = "1.10.1")
    {
        var now = atStart;
        var notice = new BrokerStaleNotice(() => now, version, Path.Combine(_dir, "broker-stale-noticed"),
            (d, _) => { _waits.Add(d); now = afterGrace; return Task.CompletedTask; },
            (kind, detail) => _events.Add((kind, detail)), _log.Add);
        return notice.RunAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RunAsync_BrokerUpdatedWithinTheGrace_NoEvent()
    {
        await RunAsync(Stale, Current);
        Assert.Empty(_events);
        Assert.Equal([BrokerStaleNotice.Grace], _waits);
    }

    [Fact]
    public async Task RunAsync_StillStaleAfterTheGrace_OneEvent()
    {
        await RunAsync(Stale, Stale);
        var e = Assert.Single(_events);
        Assert.Equal(BrokerVersion.StaleEvent, e.Kind);
        Assert.Equal(Stale.Problem, e.Detail);
        Assert.Contains(_log, l => l.StartsWith("WARNING - the admin broker is 1.10.0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_SameVersionToldBefore_NoSecondEvent()
    {
        await RunAsync(Stale, Stale);
        await RunAsync(Stale, Stale);
        Assert.Single(_events);
    }

    [Fact]
    public async Task RunAsync_NewServiceVersion_ToldAgain()
    {
        await RunAsync(Stale, Stale, "1.10.1");
        await RunAsync(Stale, Stale, "1.10.2");
        Assert.Equal(2, _events.Count);
    }

    [Fact]
    public async Task RunAsync_CurrentAtStart_NoWaitNoEvent()
    {
        await RunAsync(Current, Current);
        Assert.Empty(_waits);
        Assert.Empty(_events);
    }
}
