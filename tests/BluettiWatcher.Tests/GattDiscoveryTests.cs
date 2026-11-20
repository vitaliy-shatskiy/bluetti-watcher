using BluettiWatcher.Io.Ble;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace BluettiWatcher.Tests;

/// <summary>
/// One open looks the station's services and ff01/ff02 up in Windows' cache first (a normal poll then makes no
/// over-the-air discovery, the step a stalled open hung in) and over the air only when the cached pass fails (no
/// ff01/ff02, or the subscription on them), within the same open.
/// </summary>
public class GattDiscoveryTests
{
    private const string Station = "station";

    /// <summary>A scripted station: per cache mode, the services' status and whether ff01/ff02 are found.</summary>
    private sealed class Gatt(Dictionary<BluetoothCacheMode, (GattCommunicationStatus Status, bool Characteristics)> answers,
        params BluetoothCacheMode[] subscriptionFails)
    {
        public List<string> Calls { get; } = [];
        public List<string> Stages { get; } = [];

        private Task<(GattCommunicationStatus Status, byte? ProtocolError, IReadOnlyList<string> Services)> ServicesAsync(BluetoothCacheMode mode, CancellationToken ct)
        {
            Calls.Add($"services {mode}");
            var status = answers[mode].Status;
            return Task.FromResult<(GattCommunicationStatus, byte?, IReadOnlyList<string>)>((status, null, status == GattCommunicationStatus.Success ? [Station] : []));
        }

        private Task<string?> CharacteristicAsync(string service, Guid uuid, BluetoothCacheMode mode, CancellationToken ct)
        {
            Calls.Add($"characteristic {mode}");
            return Task.FromResult(answers[mode].Characteristics ? $"{service}/{uuid:N}/{mode}" : null);
        }

        private Task<string?> SubscribeAsync(string notify, CancellationToken ct)
        {
            var mode = notify.EndsWith("/Uncached", StringComparison.Ordinal) ? BluetoothCacheMode.Uncached : BluetoothCacheMode.Cached;
            Calls.Add($"subscribe {mode}");
            return Task.FromResult(subscriptionFails.Contains(mode) ? "Unreachable" : null);
        }

        public Task<GattFound<string>> RunAsync() =>
            GattDiscovery.RunAsync<string, string>(ServicesAsync, CharacteristicAsync, SubscribeAsync, 2, TimeSpan.Zero, Stages.Add, CancellationToken.None);
    }

    [Fact]
    public async Task RunAsync_CacheHoldsTheCharacteristics_NeverLooksOverTheAir()
    {
        var gatt = new Gatt(new() { [BluetoothCacheMode.Cached] = (GattCommunicationStatus.Success, true), [BluetoothCacheMode.Uncached] = (GattCommunicationStatus.Success, true) });
        var found = await gatt.RunAsync();
        Assert.Null(found.Failure);
        Assert.Equal(BluetoothCacheMode.Cached, found.Mode);
        Assert.EndsWith("/Cached", found.Notify, StringComparison.Ordinal);
        Assert.EndsWith("/Cached", found.Write, StringComparison.Ordinal);
        Assert.DoesNotContain(gatt.Calls, c => c.EndsWith("Uncached", StringComparison.Ordinal));
        Assert.Equal(["GATT services (cached)", "the ff01/ff02 characteristics (cached)", "the notification subscription (cached)"], gatt.Stages);
    }

    [Theory]
    [InlineData(GattCommunicationStatus.Success)]
    [InlineData(GattCommunicationStatus.Unreachable)]
    public async Task RunAsync_CacheMisses_LooksOverTheAirInTheSameOpen(GattCommunicationStatus cachedStatus)
    {
        var gatt = new Gatt(new() { [BluetoothCacheMode.Cached] = (cachedStatus, false), [BluetoothCacheMode.Uncached] = (GattCommunicationStatus.Success, true) });
        var found = await gatt.RunAsync();
        Assert.Null(found.Failure);
        Assert.Equal(BluetoothCacheMode.Uncached, found.Mode);
        Assert.EndsWith("/Uncached", found.Notify, StringComparison.Ordinal);
        Assert.EndsWith("/Uncached", found.Write, StringComparison.Ordinal);
        Assert.Contains("services Uncached", gatt.Calls);
        Assert.Contains("GATT services (uncached)", gatt.Stages);
        Assert.Equal("the notification subscription (uncached)", gatt.Stages[^1]);
    }

    [Fact]
    public async Task RunAsync_SubscriptionFailsAfterCachedLookup_LooksUpOverTheAirOnceInTheSameOpen()
    {
        var gatt = new Gatt(new() { [BluetoothCacheMode.Cached] = (GattCommunicationStatus.Success, true), [BluetoothCacheMode.Uncached] = (GattCommunicationStatus.Success, true) },
            BluetoothCacheMode.Cached);
        var found = await gatt.RunAsync();
        Assert.Null(found.Failure);
        Assert.Equal(BluetoothCacheMode.Uncached, found.Mode);
        Assert.EndsWith("/Uncached", found.Notify, StringComparison.Ordinal);
        Assert.EndsWith("/Uncached", found.Write, StringComparison.Ordinal);
        Assert.Equal(["subscribe Cached", "services Uncached"], gatt.Calls.Where(c => c is "subscribe Cached" or "services Uncached"));
        Assert.Equal(1, gatt.Calls.Count(c => c == "subscribe Uncached"));
        Assert.Contains("the notification subscription (cached)", gatt.Stages);
        Assert.Equal("the notification subscription (uncached)", gatt.Stages[^1]);
    }

    [Fact]
    public async Task RunAsync_SubscriptionFailsBothWays_NamesTheCacheModeOfEach()
    {
        var gatt = new Gatt(new() { [BluetoothCacheMode.Cached] = (GattCommunicationStatus.Success, true), [BluetoothCacheMode.Uncached] = (GattCommunicationStatus.Success, true) },
            BluetoothCacheMode.Cached, BluetoothCacheMode.Uncached);
        var found = await gatt.RunAsync();
        Assert.Null(found.Notify);
        Assert.Null(found.Write);
        Assert.Equal("the notification subscription (uncached): Unreachable; from the cache: the notification subscription (cached): Unreachable", found.Failure);
        Assert.Equal(1, gatt.Calls.Count(c => c == "subscribe Uncached"));
    }

    [Fact]
    public async Task RunAsync_CachedSubscriptionThrows_LooksUpOverTheAir()
    {
        List<string> stages = [];
        var found = await GattDiscovery.RunAsync<string, string>(
            (mode, _) => Task.FromResult<(GattCommunicationStatus, byte?, IReadOnlyList<string>)>((GattCommunicationStatus.Success, null, [Station])),
            (s, uuid, mode, _) => Task.FromResult<string?>($"{s}/{uuid:N}/{mode}"),
            (notify, _) => notify.EndsWith("/Cached", StringComparison.Ordinal) ? throw new InvalidOperationException("stale handle") : Task.FromResult<string?>(null),
            2, TimeSpan.Zero, stages.Add, CancellationToken.None);
        Assert.Null(found.Failure);
        Assert.Equal(BluetoothCacheMode.Uncached, found.Mode);
        Assert.Equal("the notification subscription (uncached)", stages[^1]);
    }

    [Fact]
    public async Task RunAsync_BothSubscriptionsThrowOrFail_NamesTheCachedStepThatThrew()
    {
        var found = await GattDiscovery.RunAsync<string, string>(
            (mode, _) => Task.FromResult<(GattCommunicationStatus, byte?, IReadOnlyList<string>)>((GattCommunicationStatus.Success, null, [Station])),
            (s, uuid, mode, _) => Task.FromResult<string?>($"{s}/{uuid:N}/{mode}"),
            (notify, _) => notify.EndsWith("/Cached", StringComparison.Ordinal) ? throw new InvalidOperationException("stale handle") : Task.FromResult<string?>("Unreachable"),
            2, TimeSpan.Zero, _ => { }, CancellationToken.None);
        Assert.Equal("the notification subscription (uncached): Unreachable; from the cache: the notification subscription (cached): InvalidOperationException: stale handle", found.Failure);
    }

    [Fact]
    public async Task RunAsync_BothLookupsFail_NamesTheStepOfEach()
    {
        var gatt = new Gatt(new() { [BluetoothCacheMode.Cached] = (GattCommunicationStatus.Success, false), [BluetoothCacheMode.Uncached] = (GattCommunicationStatus.Unreachable, false) });
        var found = await gatt.RunAsync();
        Assert.Null(found.Notify);
        Assert.Null(found.Write);
        Assert.Equal("GATT services (uncached): Unreachable after 2 tries; from the cache: the ff01/ff02 characteristics (cached): none found", found.Failure);
        Assert.Equal("GATT services (uncached), try 2 (the last answered Unreachable)", gatt.Stages[^1]);
    }
}
