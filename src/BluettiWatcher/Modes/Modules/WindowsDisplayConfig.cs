using System.Runtime.InteropServices;

namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// The Windows display configuration: QueryDisplayConfig names each active monitor by its device path, and
/// EnumDisplaySettingsEx / ChangeDisplaySettingsEx read and set the refresh rate on its <c>\\.\DISPLAYn</c> source.
/// </summary>
/// <remarks>Pattern: Adapter. Struct layouts follow wingdi.h (DEVMODEW, DISPLAYCONFIG_*).</remarks>
internal sealed unsafe partial class WindowsDisplayConfig : IDisplayConfig
{
    private const uint QdcOnlyActivePaths = 2;
    private const int ErrorSuccess = 0, ErrorInsufficientBuffer = 122;
    private const uint GetSourceName = 1, GetTargetName = 2;
    private const int EnumCurrentSettings = -1;
    private const uint DmPelsWidth = 0x80000, DmPelsHeight = 0x100000, DmDisplayFrequency = 0x400000;
    private const uint CdsUpdateRegistry = 1;

    public IReadOnlyList<DisplayMonitor>? Monitors()
    {
        if (Targets() is not { } targets) return null;
        var list = new List<DisplayMonitor>();
        // A cloned source drives several monitors at one mode: the first target names it.
        foreach (var t in targets.GroupBy(t => t.Gdi, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            if (!TryCurrent(t.Gdi, out var cur)) continue;
            list.Add(new DisplayMonitor(t.DevicePath, t.Name, (int)cur.PelsWidth, (int)cur.PelsHeight, (int)cur.DisplayFrequency, Rates(t.Gdi, cur)));
        }
        return list;
    }

    public string? SetRefreshRate(string monitorId, int hz)
    {
        if (Targets() is not { } targets) return "the display configuration cannot be read";
        var found = targets.Where(t => string.Equals(t.DevicePath, monitorId, StringComparison.OrdinalIgnoreCase)).Take(1).ToList();
        if (found.Count == 0) return $"monitor {monitorId} is not active";
        var target = found[0];
        if (!TryCurrent(target.Gdi, out var mode)) return $"the current mode of {target.Name} cannot be read";
        mode.DisplayFrequency = (uint)hz;
        mode.Fields = DmPelsWidth | DmPelsHeight | DmDisplayFrequency;
        int r = ChangeDisplaySettingsExW(target.Gdi, &mode, 0, CdsUpdateRegistry, 0);
        return r == 0 ? null : $"ChangeDisplaySettingsEx refused {hz} Hz on {target.Name} (code {r})";
    }

    /// <summary>Each active path's source GDI name with its monitor's device path and friendly name; null when unreadable.</summary>
    internal static IReadOnlyList<(string Gdi, string DevicePath, string Name)>? Targets()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint pathCount, out uint modeCount) != ErrorSuccess) return null;
            var paths = new PathInfo[pathCount];
            var modes = new ModeInfo[modeCount];
            int r;
            fixed (PathInfo* p = paths)
            fixed (ModeInfo* m = modes)
                r = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, p, ref modeCount, m, 0);
            if (r == ErrorInsufficientBuffer) continue;   // the topology changed between the two calls
            if (r != ErrorSuccess) return null;
            var list = new List<(string, string, string)>();
            foreach (var path in paths.AsSpan(0, (int)pathCount))
            {
                var source = new SourceDeviceName { Header = new() { Type = GetSourceName, Size = (uint)sizeof(SourceDeviceName), AdapterId = path.Source.AdapterId, Id = path.Source.Id } };
                var target = new TargetDeviceName { Header = new() { Type = GetTargetName, Size = (uint)sizeof(TargetDeviceName), AdapterId = path.Target.AdapterId, Id = path.Target.Id } };
                if (DisplayConfigGetDeviceInfo(&source.Header) != ErrorSuccess || DisplayConfigGetDeviceInfo(&target.Header) != ErrorSuccess) continue;
                string devicePath = new(target.DevicePath);
                if (devicePath.Length == 0) continue;
                string name = new(target.FriendlyName);
                list.Add((new string(source.GdiName), devicePath, name.Length > 0 ? name : "Monitor"));
            }
            return list;
        }
        return null;
    }

    private static bool TryCurrent(string gdi, out DevMode mode)
    {
        mode = new DevMode { Size = (ushort)sizeof(DevMode) };
        fixed (DevMode* p = &mode) return EnumDisplaySettingsExW(gdi, EnumCurrentSettings, p, 0) != 0;
    }

    private static int[] Rates(string gdi, DevMode current)
    {
        var rates = new SortedSet<int>();
        var mode = new DevMode { Size = (ushort)sizeof(DevMode) };
        for (int i = 0; EnumDisplaySettingsExW(gdi, i, &mode, 0) != 0; i++)
        {
            // 0 and 1 mean "the hardware default", not a rate.
            if (mode.PelsWidth == current.PelsWidth && mode.PelsHeight == current.PelsHeight && mode.BitsPerPel == current.BitsPerPel && mode.DisplayFrequency > 1)
                rates.Add((int)mode.DisplayFrequency);
            mode = new DevMode { Size = (ushort)sizeof(DevMode) };
        }
        rates.Add((int)current.DisplayFrequency);
        return [.. rates];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public uint RefreshNumerator;
        public uint RefreshDenominator;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo
    {
        public PathSourceInfo Source;
        public PathTargetInfo Target;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ModeInfo
    {
        public uint InfoType;
        public uint Id;
        public Luid AdapterId;
        public fixed byte Union[48];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SourceDeviceName
    {
        public DeviceInfoHeader Header;
        public fixed char GdiName[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TargetDeviceName
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        public fixed char FriendlyName[64];
        public fixed char DevicePath[128];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevMode
    {
        public fixed char DeviceName[32];
        public ushort SpecVersion;
        public ushort DriverVersion;
        public ushort Size;
        public ushort DriverExtra;
        public uint Fields;
        public int PositionX;
        public int PositionY;
        public uint DisplayOrientation;
        public uint DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        public fixed char FormName[32];
        public ushort LogPixels;
        public uint BitsPerPel;
        public uint PelsWidth;
        public uint PelsHeight;
        public uint DisplayFlags;
        public uint DisplayFrequency;
        public uint IcmMethod;
        public uint IcmIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;
    }

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int QueryDisplayConfig(uint flags, ref uint pathCount, PathInfo* paths, ref uint modeCount, ModeInfo* modes, nint topologyId);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DisplayConfigGetDeviceInfo(DeviceInfoHeader* request);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int EnumDisplaySettingsExW(string deviceName, int modeNum, DevMode* mode, uint flags);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int ChangeDisplaySettingsExW(string deviceName, DevMode* mode, nint hwnd, uint flags, nint lParam);
}
