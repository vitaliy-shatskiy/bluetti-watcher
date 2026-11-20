using System.Text.Json;
using BluettiWatcher.Http;
using BluettiWatcher.Modes.Broker;
using BluettiWatcher.Storage;
using BluettiWatcher.Tray;
using static BluettiWatcher.Tests.Fx;

namespace BluettiWatcher.Tests.Modes;

/// <summary>A broker older than the service is never silent: /api/health names it and the tray says so.</summary>
public sealed class BrokerVersionTests
{
    [Theory]
    [InlineData("1.8.0", null, "missing")]
    [InlineData("1.8.0", "1.7.0", "stale")]
    [InlineData("1.8.0", "1.8.0", "current")]
    [InlineData("1.8.0", "1.8.0.7", "current")]
    [InlineData("1.8.0", "1.9.0", "current")]
    public void Check_OwnAndBrokerVersion_NameTheState(string own, string? broker, string state) =>
        Assert.Equal(state, BrokerVersion.Check(Version.Parse(own), broker is null ? null : Version.Parse(broker)).State);

    [Fact]
    public void Check_StaleBroker_TellsTheElevatedStep()
    {
        var s = BrokerVersion.Check(new Version(1, 8, 0), new Version(1, 7, 0));
        Assert.Contains("install.ps1 -Broker -Apply", s.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void FromEvent_BrokerStale_IsAFailureNotice()
    {
        var n = TrayNotices.FromEvent(BrokerVersion.StaleEvent, "the admin broker is 1.7.0, older than the service (1.8.0)", null);
        Assert.NotNull(n);
        Assert.Equal(NotificationKind.Failures, n.Kind);
    }

    [Fact]
    public async Task Health_StaleBroker_IsReported()
    {
        string dir = Directory.CreateTempSubdirectory("bw-broker-health-").FullName;
        try
        {
            var cfg = new StationConfig { DataDir = dir, Port = 18999 };
            var clock = new ManualClock(T0);
            using var store = new StationStore(Path.Combine(dir, "station.db"));
            using var service = new StationService(cfg, new QueueReader(), new FakeActions(), new FakeHibernator(true), new FixedFlag(Flag()), store, clock, _ => { });
            var api = new StationApi(cfg, service, store, clock, T0, broker: () => BrokerVersion.Check(new Version(1, 8, 0), new Version(1, 7, 0)));
            var r = await api.HandleAsync(new HttpRequest("GET", "/api/health", new Dictionary<string, string>(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Host"] = "127.0.0.1:18999" }, []), TestContext.Current.CancellationToken);
            using var doc = JsonDocument.Parse(r.Body);
            var b = doc.RootElement.GetProperty("broker");
            Assert.Equal("stale", b.GetProperty("state").GetString());
            Assert.Equal("1.7.0", b.GetProperty("version").GetString());
            Assert.Contains("-Broker -Apply", b.GetProperty("problem").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, recursive: true);
        }
    }
}
