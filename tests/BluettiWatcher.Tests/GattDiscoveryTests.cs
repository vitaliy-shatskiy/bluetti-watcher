using BluettiWatcher.Io.Ble;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace BluettiWatcher.Tests;

/// <summary>
/// One open looks the station's services and ff01/ff02 up in Windows' cache first (a normal poll then makes no
/// over-the-air discovery, the step a stalled open hung in) and over the air only when the cache fails or holds no
/// ff01/ff02, within the same open.
/// </summary>
public class GattDiscoveryTests
{
    private const string Station = "station";

    /// <summary>A scripted station: per cache mode, the services' status and whether ff01/ff02 are found.</summary>
    private sealed class Gatt(Dictionary<BluetoothCacheMode, (GattCommunicationStatus Status, bool Characteristics)> answers)
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

        public Task<GattFound<string>> RunAsync() =>
            GattDiscovery.RunAsync<string, string>(ServicesAsync, CharacteristicAsync, 2, TimeSpan.Zero, Stages.Add, CancellationToken.None);
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
        Assert.Equal(["GATT services (cached)", "the ff01/ff02 characteristics (cached)"], gatt.Stages);
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
        Assert.Equal("the ff01/ff02 characteristics (uncached)", gatt.Stages[^1]);
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
