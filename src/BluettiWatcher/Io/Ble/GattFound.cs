using Windows.Devices.Bluetooth;

namespace BluettiWatcher.Io.Ble;

/// <summary>ff01 and ff02 and the cache mode of the lookup that found them; or, both null, why none was found.</summary>
internal sealed record GattFound<TChar>(TChar? Notify, TChar? Write, BluetoothCacheMode Mode, string? Failure) where TChar : class;
