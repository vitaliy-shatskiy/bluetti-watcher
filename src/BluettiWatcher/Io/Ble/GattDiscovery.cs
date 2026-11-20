using System.Globalization;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace BluettiWatcher.Io.Ble;

/// <summary>
/// The GATT lookup and notification subscription of one open, apart from WinRT so its order is tested. Windows' cache
/// first: a normal poll then makes no over-the-air service discovery, the step stalled opens hung in. Over the air (with
/// its tries) only when the cached pass fails, within the same open and its cap. Every stage names its cache mode.
/// </summary>
internal static class GattDiscovery
{
    internal static async Task<GattFound<TChar>> RunAsync<TService, TChar>(
        Func<BluetoothCacheMode, CancellationToken, Task<(GattCommunicationStatus Status, byte? ProtocolError, IReadOnlyList<TService> Services)>> services,
        Func<TService, Guid, BluetoothCacheMode, CancellationToken, Task<TChar?>> characteristic,
        Func<TChar, CancellationToken, Task<string?>> subscribe,
        int attempts, TimeSpan pause, Action<string> stage, CancellationToken ct) where TChar : class
    {
        var cached = await CachedAsync(services, characteristic, subscribe, pause, stage, ct);
        if (cached.Failure is null) return cached;
        var uncached = await PassAsync(BluetoothCacheMode.Uncached, attempts, services, characteristic, pause, stage, ct);
        if (uncached.Failure is null) uncached = await SubscribeAsync(uncached, subscribe, stage, ct);
        return uncached.Failure is null ? uncached : uncached with { Failure = $"{uncached.Failure}; from the cache: {cached.Failure}" };
    }

    /// <summary>
    /// A stale Windows GATT cache can hand back handles the station no longer serves: any failure of the cached pass,
    /// subscription included and a WinRT exception too, falls through to the over-the-air pass.
    /// </summary>
    private static async Task<GattFound<TChar>> CachedAsync<TService, TChar>(
        Func<BluetoothCacheMode, CancellationToken, Task<(GattCommunicationStatus Status, byte? ProtocolError, IReadOnlyList<TService> Services)>> services,
        Func<TService, Guid, BluetoothCacheMode, CancellationToken, Task<TChar?>> characteristic,
        Func<TChar, CancellationToken, Task<string?>> subscribe,
        TimeSpan pause, Action<string> stage, CancellationToken ct) where TChar : class
    {
        string step = "GATT services (cached)";
        void Track(string s) { step = s; stage(s); }
        try
        {
            var found = await PassAsync(BluetoothCacheMode.Cached, 1, services, characteristic, pause, Track, ct);
            return found.Failure is null ? await SubscribeAsync(found, subscribe, Track, ct) : found;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new GattFound<TChar>(null, null, BluetoothCacheMode.Cached, $"{step}: {e.GetType().Name}: {e.Message}");
        }
    }

    private static async Task<GattFound<TChar>> SubscribeAsync<TChar>(GattFound<TChar> found,
        Func<TChar, CancellationToken, Task<string?>> subscribe, Action<string> stage, CancellationToken ct) where TChar : class
    {
        string name = Name(found.Mode);
        stage($"the notification subscription ({name})");
        // A pass without a failure always carries both characteristics.
        return await subscribe(found.Notify!, ct) is { } failed
            ? new GattFound<TChar>(null, null, found.Mode, $"the notification subscription ({name}): {failed}")
            : found;
    }

    private static string Name(BluetoothCacheMode mode) => mode == BluetoothCacheMode.Cached ? "cached" : "uncached";

    private static async Task<GattFound<TChar>> PassAsync<TService, TChar>(BluetoothCacheMode mode, int attempts,
        Func<BluetoothCacheMode, CancellationToken, Task<(GattCommunicationStatus Status, byte? ProtocolError, IReadOnlyList<TService> Services)>> services,
        Func<TService, Guid, BluetoothCacheMode, CancellationToken, Task<TChar?>> characteristic,
        TimeSpan pause, Action<string> stage, CancellationToken ct) where TChar : class
    {
        string name = Name(mode);
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
