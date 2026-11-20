using System.Globalization;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace BluettiWatcher.Io.Ble;

/// <summary>
/// The GATT lookup of one open, apart from WinRT so its order is tested. Windows' cache first: a normal poll then makes no
/// over-the-air service discovery, the step stalled opens hung in. Over the air (with its tries) only when the cache
/// fails or holds no ff01/ff02, within the same open and its cap. Every stage names the cache mode it uses.
/// </summary>
internal static class GattDiscovery
{
    internal static async Task<GattFound<TChar>> RunAsync<TService, TChar>(
        Func<BluetoothCacheMode, CancellationToken, Task<(GattCommunicationStatus Status, byte? ProtocolError, IReadOnlyList<TService> Services)>> services,
        Func<TService, Guid, BluetoothCacheMode, CancellationToken, Task<TChar?>> characteristic,
        int attempts, TimeSpan pause, Action<string> stage, CancellationToken ct) where TChar : class
    {
        var cached = await PassAsync(BluetoothCacheMode.Cached, 1, services, characteristic, pause, stage, ct);
        if (cached.Failure is null) return cached;
        var uncached = await PassAsync(BluetoothCacheMode.Uncached, attempts, services, characteristic, pause, stage, ct);
        return uncached.Failure is null ? uncached : uncached with { Failure = $"{uncached.Failure}; from the cache: {cached.Failure}" };
    }

    private static async Task<GattFound<TChar>> PassAsync<TService, TChar>(BluetoothCacheMode mode, int attempts,
        Func<BluetoothCacheMode, CancellationToken, Task<(GattCommunicationStatus Status, byte? ProtocolError, IReadOnlyList<TService> Services)>> services,
        Func<TService, Guid, BluetoothCacheMode, CancellationToken, Task<TChar?>> characteristic,
        TimeSpan pause, Action<string> stage, CancellationToken ct) where TChar : class
    {
        string name = mode == BluetoothCacheMode.Cached ? "cached" : "uncached";
        (GattCommunicationStatus Status, byte? ProtocolError, IReadOnlyList<TService> Services)? answer = null;
        for (int i = 1; i <= attempts; i++)
        {
            stage(answer is not { } last ? $"GATT services ({name})"
                : string.Create(CultureInfo.InvariantCulture, $"GATT services ({name}), try {i} (the last answered {last.Status})"));
            answer = await services(mode, ct);
            if (answer.Value.Status == GattCommunicationStatus.Success) break;
            if (i < attempts) await Task.Delay(pause, ct);
        }
        if (answer is not { Status: GattCommunicationStatus.Success } found)
        {
            string why = string.Create(CultureInfo.InvariantCulture,
                $"GATT services ({name}): {answer?.Status} after {attempts} {(attempts == 1 ? "try" : "tries")}{(answer?.ProtocolError is { } pe ? $" (protocol error {pe})" : "")}");
            return new GattFound<TChar>(null, null, mode, why);
        }
        stage($"the ff01/ff02 characteristics ({name})");
        TChar? notify = null, write = null;
        foreach (var s in found.Services)
        {
            notify ??= await characteristic(s, WinRtBleConnector.NotifyUuid, mode, ct);
            write ??= await characteristic(s, WinRtBleConnector.WriteUuid, mode, ct);
        }
        return notify is null || write is null
            ? new GattFound<TChar>(null, null, mode, $"the ff01/ff02 characteristics ({name}): none found")
            : new GattFound<TChar>(notify, write, mode, null);
    }
}
