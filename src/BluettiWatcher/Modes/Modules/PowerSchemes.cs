using System.Globalization;
using System.Runtime.InteropServices;

namespace BluettiWatcher.Modes.Modules;

/// <summary>The power plans through powrprof.dll (PowerEnumerate, PowerGetActiveScheme, Power*ValueIndex): no powercfg text to parse.</summary>
internal sealed unsafe partial class PowerSchemes : IPowerSchemes
{
    private const uint AccessScheme = 16;
    private const uint ErrorNoMoreItems = 259;
    private const uint ErrorMoreData = 234;

    public IReadOnlyList<PowerPlan>? ListPlans()
    {
        var plans = new List<PowerPlan>();
        for (uint i = 0; i < 1000; i++)
        {
            Guid g;
            uint size = (uint)sizeof(Guid);
            uint r = PowerEnumerate(0, null, null, AccessScheme, i, (byte*)&g, ref size);
            if (r == ErrorNoMoreItems) return plans;
            if (r != 0) return null;
            plans.Add(new PowerPlan(g, FriendlyName(g) ?? g.ToString()));
        }
        return null;
    }

    private static string? FriendlyName(Guid plan)
    {
        uint size = 0;
        uint r = PowerReadFriendlyName(0, &plan, null, null, null, ref size);
        if ((r != 0 && r != ErrorMoreData) || size < 2 || size > 4096) return null;
        var buffer = new byte[size];
        fixed (byte* b = buffer)
        {
            if (PowerReadFriendlyName(0, &plan, null, null, b, ref size) != 0) return null;
            return new string((char*)b).Trim();
        }
    }

    public Guid? ActivePlan()
    {
        if (PowerGetActiveScheme(0, out nint p) != 0 || p == 0) return null;
        try { return *(Guid*)p; }
        finally { LocalFree(p); }
    }

    public string? SetActivePlan(Guid plan) => Error(PowerSetActiveScheme(0, &plan), "PowerSetActiveScheme");

    public uint? ReadValue(Guid plan, PowerSetting setting, PowerSource source)
    {
        Guid sub = setting.Subgroup, set = setting.Setting;
        uint value;
        uint r = source == PowerSource.Ac ? PowerReadACValueIndex(0, &plan, &sub, &set, &value) : PowerReadDCValueIndex(0, &plan, &sub, &set, &value);
        return r == 0 ? value : null;
    }

    public string? WriteValue(Guid plan, PowerSetting setting, PowerSource source, uint value)
    {
        Guid sub = setting.Subgroup, set = setting.Setting;
        uint r = source == PowerSource.Ac ? PowerWriteACValueIndex(0, &plan, &sub, &set, value) : PowerWriteDCValueIndex(0, &plan, &sub, &set, value);
        if (r != 0) return Error(r, "PowerWriteValueIndex");
        // A written value of the active plan takes effect only when the plan is set active again.
        return ActivePlan() == plan ? Error(PowerSetActiveScheme(0, &plan), "PowerSetActiveScheme") : null;
    }

    private static string? Error(uint code, string call) =>
        code == 0 ? null : $"{call} failed: Windows error {code.ToString(CultureInfo.InvariantCulture)}";

    [LibraryImport("powrprof.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PowerEnumerate(nint root, Guid* scheme, Guid* subgroup, uint access, uint index, byte* buffer, ref uint size);

    [LibraryImport("powrprof.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PowerReadFriendlyName(nint root, Guid* scheme, Guid* subgroup, Guid* setting, byte* buffer, ref uint size);

    [LibraryImport("powrprof.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PowerGetActiveScheme(nint userRoot, out nint activeScheme);

    [LibraryImport("powrprof.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PowerSetActiveScheme(nint userRoot, Guid* scheme);

    [LibraryImport("powrprof.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PowerReadACValueIndex(nint root, Guid* scheme, Guid* subgroup, Guid* setting, uint* value);

    [LibraryImport("powrprof.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PowerReadDCValueIndex(nint root, Guid* scheme, Guid* subgroup, Guid* setting, uint* value);

    [LibraryImport("powrprof.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PowerWriteACValueIndex(nint root, Guid* scheme, Guid* subgroup, Guid* setting, uint value);

    [LibraryImport("powrprof.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint PowerWriteDCValueIndex(nint root, Guid* scheme, Guid* subgroup, Guid* setting, uint value);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint LocalFree(nint memory);
}
