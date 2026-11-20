using System.Runtime.InteropServices;

namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// DDC/CI through dxva2: each display monitor's physical monitors, VCP code 0x10 (luminance). A physical monitor is named
/// by the device path of its source's target (the id the refresh-rate module uses); handles live for one call only.
/// </summary>
/// <remarks>Pattern: Adapter. Layouts follow PhysicalMonitorEnumerationAPI.h and winuser.h.</remarks>
internal sealed partial class WindowsDdcMonitors : IDdcMonitors
{
    private const byte Luminance = 0x10;
    // DDC/CI needs a pause after a set before the monitor answers a get with the new value.
    private static readonly TimeSpan SettleAfterWrite = TimeSpan.FromMilliseconds(100);

    public Task<IReadOnlyList<DdcMonitor>?> MonitorsAsync(CancellationToken ct)
    {
        var list = new List<DdcMonitor>();
        bool ok = Visit((id, name, handle) =>
        {
            bool ddc = GetVCPFeatureAndVCPFeatureReply(handle, Luminance, 0, out _, out uint max) != 0 && max > 0;
            list.Add(new DdcMonitor(id, name, ddc, ddc ? "" : "no answer to the brightness query"));
            return false;
        });
        return Task.FromResult<IReadOnlyList<DdcMonitor>?>(ok ? list : null);
    }

    public Task<DdcBrightness> ReadBrightnessAsync(string monitorId, CancellationToken ct)
    {
        DdcBrightness? result = null;
        bool ok = Visit((id, _, handle) =>
        {
            if (!string.Equals(id, monitorId, StringComparison.OrdinalIgnoreCase)) return false;
            result = GetVCPFeatureAndVCPFeatureReply(handle, Luminance, 0, out uint cur, out uint max) != 0
                ? new DdcBrightness((int)cur, (int)max)
                : DdcBrightness.Failed("the monitor did not answer the DDC/CI brightness query");
            return true;
        });
        return Task.FromResult(!ok ? DdcBrightness.Failed("the monitors cannot be listed")
            : result ?? DdcBrightness.Failed($"monitor {monitorId} is not connected or has no DDC/CI"));
    }

    public async Task<string?> WriteBrightnessAsync(string monitorId, int raw, CancellationToken ct)
    {
        string? error = $"monitor {monitorId} is not connected or has no DDC/CI";
        bool ok = Visit((id, _, handle) =>
        {
            if (!string.Equals(id, monitorId, StringComparison.OrdinalIgnoreCase)) return false;
            error = SetVCPFeature(handle, Luminance, (uint)raw) != 0 ? null : "the monitor refused the DDC/CI brightness write";
            return true;
        });
        if (!ok) return "the monitors cannot be listed";
        if (error is null) await Task.Delay(SettleAfterWrite, ct);
        return error;
    }

    /// <summary>
    /// Opens every physical monitor in turn and hands it to <paramref name="visit"/> (true = stop); false when the display
    /// monitors cannot be enumerated.
    /// </summary>
    private static unsafe bool Visit(Func<string, string, nint, bool> visit)
    {
        if (WindowsDisplayConfig.Targets() is not { } targets) return false;
        var hmonitors = new List<nint>();
        var gc = GCHandle.Alloc(hmonitors);
        try
        {
            if (EnumDisplayMonitors(0, 0, (nint)(delegate* unmanaged<nint, nint, nint, nint, int>)&OnMonitor, GCHandle.ToIntPtr(gc)) == 0) return false;
        }
        finally
        {
            gc.Free();
        }
        foreach (var hmon in hmonitors)
        {
            var info = new MonitorInfoEx { Size = (uint)sizeof(MonitorInfoEx) };
            if (GetMonitorInfoW(hmon, &info) == 0) continue;
            string gdi = new(info.Device);
            var paths = targets.Where(t => string.Equals(t.Gdi, gdi, StringComparison.OrdinalIgnoreCase)).ToList();
            if (GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out uint count) == 0 || count == 0) continue;
            var physical = new PhysicalMonitor[count];
            fixed (PhysicalMonitor* p = physical)
            {
                if (GetPhysicalMonitorsFromHMONITOR(hmon, count, p) == 0) continue;
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        // The physical monitors of a cloned source are taken in path order; past the known paths, an index names them.
                        string id = i < paths.Count ? paths[i].DevicePath : $"{gdi}#{i}";
                        string name = i < paths.Count ? paths[i].Name : new string(p[i].Description);
                        if (visit(id, name, p[i].Handle)) return true;
                    }
                }
                finally
                {
                    _ = DestroyPhysicalMonitors(count, p);
                }
            }
        }
        return true;
    }

    [UnmanagedCallersOnly]
    private static int OnMonitor(nint hmonitor, nint hdc, nint rect, nint data)
    {
        if (GCHandle.FromIntPtr(data).Target is List<nint> list) list.Add(hmonitor);
        return 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct MonitorInfoEx
    {
        public uint Size;
        public int MonitorLeft, MonitorTop, MonitorRight, MonitorBottom;
        public int WorkLeft, WorkTop, WorkRight, WorkBottom;
        public uint Flags;
        public fixed char Device[32];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private unsafe struct PhysicalMonitor
    {
        public nint Handle;
        public fixed char Description[128];
    }

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int EnumDisplayMonitors(nint hdc, nint clip, nint callback, nint data);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int GetMonitorInfoW(nint hmonitor, MonitorInfoEx* info);

    [LibraryImport("dxva2.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetNumberOfPhysicalMonitorsFromHMONITOR(nint hmonitor, out uint count);

    [LibraryImport("dxva2.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int GetPhysicalMonitorsFromHMONITOR(nint hmonitor, uint count, PhysicalMonitor* monitors);

    [LibraryImport("dxva2.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int DestroyPhysicalMonitors(uint count, PhysicalMonitor* monitors);

    [LibraryImport("dxva2.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetVCPFeatureAndVCPFeatureReply(nint monitor, byte code, nint codeType, out uint current, out uint maximum);

    [LibraryImport("dxva2.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SetVCPFeature(nint monitor, byte code, uint value);
}
