using System.Globalization;
using System.Threading.Channels;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace BluettiWatcher.Io.Ble;

/// <summary>
/// Opens the station through the Windows Bluetooth LE stack (WinRT GATT). No pairing, no protection level, no encryption:
/// a plain GATT connection, the notification subscription on ff01 (its client configuration descriptor) and read
/// requests on ff02.
/// </summary>
internal sealed class WinRtBleConnector : IBleConnector
{
    /// <summary>The station answers on this characteristic (notifications).</summary>
    internal static readonly Guid NotifyUuid = new("0000ff01-0000-1000-8000-00805f9b34fb");
    /// <summary>Read requests go to this characteristic.</summary>
    internal static readonly Guid WriteUuid = new("0000ff02-0000-1000-8000-00805f9b34fb");
    /// <summary>Windows often reports a sleeping link Unreachable once; a few tries within the poll's budget.</summary>
    private const int ServiceAttempts = 3;
    /// <summary>Well inside readerTimeoutSec (30 s by default): the open after it still has time.</summary>
    internal static readonly TimeSpan ScanCap = TimeSpan.FromSeconds(8);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The link is disposed in finally unless handed over in BleOpen; the reader disposes it (await using).")]
    public async Task<BleOpen> OpenAsync(ulong address, CancellationToken ct)
    {
        var (device, notFound) = await FindAsync(
            async c => await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(c),
            (cap, c) => ScanAsync(address, cap, c), ct);
        if (device is null) return new BleOpen(null, notFound);
        WinRtBleLink? link = null;
        try
        {
            link = new WinRtBleLink(device);
            string? failure = await link.InitAsync(ServiceAttempts, ct);
            if (failure is not null) return new BleOpen(null, failure);
            var open = new BleOpen(link, null);
            link = null;   // handed over: the reader disposes it
            return open;
        }
        finally
        {
            if (link is not null) await link.DisposeAsync();
        }
    }

    /// <summary>
    /// Windows answers null for an unpaired device it has not seen advertise since boot, and nothing else makes it look:
    /// on null, a short scan for the address, then one more open.
    /// </summary>
    internal static async Task<(T? Device, string? NotFound)> FindAsync<T>(Func<CancellationToken, Task<T?>> open,
        Func<TimeSpan, CancellationToken, Task<bool>> scan, CancellationToken ct) where T : class
    {
        if (await open(ct) is { } device) return (device, null);
        bool seen = await scan(ScanCap, ct);
        if (await open(ct) is { } found) return (found, null);
        return (null, seen
            ? string.Create(CultureInfo.InvariantCulture, $"the station was not found at its address (an {ScanCap.TotalSeconds:0} s scan saw it, Windows still did not open it)")
            : string.Create(CultureInfo.InvariantCulture, $"the station was not found at its address (not seen in an {ScanCap.TotalSeconds:0} s scan)"));
    }

    /// <summary>True as soon as the address advertises; false at the cap or when Windows stops the scan (radio off).</summary>
    private static async Task<bool> ScanAsync(ulong address, TimeSpan cap, CancellationToken ct)
    {
        var seen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
        void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            if (args.BluetoothAddress == address) seen.TrySetResult(true);
        }
        void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args) => seen.TrySetResult(false);
        watcher.Received += OnReceived;
        watcher.Stopped += OnStopped;
        try
        {
            watcher.Start();
            return await seen.Task.WaitAsync(cap, ct);
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            watcher.Received -= OnReceived;
            watcher.Stopped -= OnStopped;
            watcher.Stop();
        }
    }
}

internal sealed class WinRtBleLink(BluetoothLEDevice device) : IBleLink
{
    private readonly Channel<byte[]> _notes = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<GattDeviceService> _services = [];
    private GattCharacteristic? _notify;
    private GattCharacteristic? _write;

    public ChannelReader<byte[]> Notifications => _notes.Reader;

    /// <summary>Null when the link is ready; otherwise why not.</summary>
    internal async Task<string?> InitAsync(int attempts, CancellationToken ct)
    {
        device.ConnectionStatusChanged += OnConnectionStatusChanged;
        GattDeviceServicesResult? services = null;
        for (int i = 1; i <= attempts; i++)
        {
            services = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(ct);
            if (services.Status == GattCommunicationStatus.Success) break;
            if (i < attempts) await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        if (services is null || services.Status != GattCommunicationStatus.Success)
            return string.Create(CultureInfo.InvariantCulture, $"GATT {services?.Status} after {attempts} tries{(services?.ProtocolError is { } pe ? $" (protocol error {pe})" : "")}");
        _services.AddRange(services.Services);
        foreach (var s in _services)
        {
            _notify ??= await Find(s, WinRtBleConnector.NotifyUuid, ct);
            _write ??= await Find(s, WinRtBleConnector.WriteUuid, ct);
        }
        if (_notify is null || _write is null) return "the station has no ff01/ff02 characteristic";
        _notify.ValueChanged += OnValueChanged;
        var sub = await _notify.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(ct);
        return sub == GattCommunicationStatus.Success ? null : $"notification subscription: {sub}";
    }

    private static async Task<GattCharacteristic?> Find(GattDeviceService service, Guid uuid, CancellationToken ct)
    {
        var r = await service.GetCharacteristicsForUuidAsync(uuid, BluetoothCacheMode.Uncached).AsTask(ct);
        return r.Status == GattCommunicationStatus.Success && r.Characteristics.Count > 0 ? r.Characteristics[0] : null;
    }

    public async Task WriteAsync(ReadRequest request, CancellationToken ct)
    {
        if (_write is null) throw new InvalidOperationException("the link is not open");
        using var writer = new DataWriter();
        writer.WriteBytes(request.Frame.ToArray());
        var option = _write.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write) ? GattWriteOption.WriteWithResponse : GattWriteOption.WriteWithoutResponse;
        var result = await _write.WriteValueWithResultAsync(writer.DetachBuffer(), option).AsTask(ct);
        if (result.Status != GattCommunicationStatus.Success)
            throw new IOException(string.Create(CultureInfo.InvariantCulture, $"write of the read request: {result.Status}{(result.ProtocolError is { } pe ? $" (protocol error {pe})" : "")}"));
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var bytes = new byte[args.CharacteristicValue.Length];
        using (var reader = DataReader.FromBuffer(args.CharacteristicValue)) reader.ReadBytes(bytes);
        _notes.Writer.TryWrite(bytes);
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected) _notes.Writer.TryComplete(new IOException("the station disconnected"));
    }

    /// <summary>
    /// Closes without writing anything: the subscription is not turned off (a station that is not bonded forgets it with
    /// the connection), so the only writes stay the read request and the subscription itself.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        if (_notify is not null) _notify.ValueChanged -= OnValueChanged;
        _notes.Writer.TryComplete();
        foreach (var s in _services) s.Dispose();
        device.Dispose();
        return ValueTask.CompletedTask;
    }
}
