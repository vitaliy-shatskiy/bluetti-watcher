using System.Runtime.InteropServices;

namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// <see cref="IServiceReader"/> over the service manager API (advapi32) with query-only access rights, which every
/// interactive user holds: nothing here can start, stop or reconfigure a service.
/// </summary>
internal sealed partial class WindowsServiceReader : IServiceReader
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerEnumerateService = 0x0004;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceWin32 = 0x30;
    private const uint ServiceStateAll = 0x3;
    private const int ScEnumProcessInfo = 0;
    private const uint ServiceConfigDelayedAutoStartInfo = 3;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorMoreData = 234;
    private const int ErrorServiceDoesNotExist = 1060;

    public ServiceQuery Query(string name)
    {
        nint scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == 0) return new(null, $"the service manager cannot be opened (error {Marshal.GetLastPInvokeError()})");
        try
        {
            nint svc = OpenService(scm, name, ServiceQueryConfig | ServiceQueryStatus);
            if (svc == 0)
            {
                int e = Marshal.GetLastPInvokeError();
                return new(null, e == ErrorServiceDoesNotExist ? $"service '{name}' is not installed" : $"service '{name}' cannot be opened (error {e})");
            }
            try
            {
                int state = CurrentState(svc);
                if (state < 0) return new(null, $"service '{name}': status not readable (error {Marshal.GetLastPInvokeError()})");
                string? startType = StartType(svc);
                if (startType is null) return new(null, $"service '{name}': configuration not readable (error {Marshal.GetLastPInvokeError()})");
                return new(new ServiceInfo(name, name, (ServiceRunState)state, startType), "");
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    public IReadOnlyList<ServiceInfo>? List()
    {
        nint scm = OpenSCManager(null, null, ScManagerConnect | ScManagerEnumerateService);
        if (scm == 0) return null;
        try { return Enumerate(scm); }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>ENUM_SERVICE_STATUS_PROCESSW entries: two string pointers, then SERVICE_STATUS_PROCESS (dwCurrentState is its second field).</summary>
    private static List<ServiceInfo>? Enumerate(nint scm)
    {
        var result = new List<ServiceInfo>();
        uint resume = 0;
        int entrySize = Align(2 * IntPtr.Size + 9 * sizeof(uint), IntPtr.Size);
        while (true)
        {
            _ = EnumServicesStatusEx(scm, ScEnumProcessInfo, ServiceWin32, ServiceStateAll, 0, 0, out uint needed, out _, ref resume, 0);
            int first = Marshal.GetLastPInvokeError();
            if (first != ErrorMoreData) return first == 0 ? result : null;
            nint buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                bool ok = EnumServicesStatusEx(scm, ScEnumProcessInfo, ServiceWin32, ServiceStateAll, buffer, needed, out _, out uint returned, ref resume, 0);
                int error = ok ? 0 : Marshal.GetLastPInvokeError();
                if (!ok && error != ErrorMoreData) return null;
                for (int i = 0; i < returned; i++)
                {
                    nint entry = buffer + i * entrySize;
                    string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(entry)) ?? "";
                    string display = Marshal.PtrToStringUni(Marshal.ReadIntPtr(entry, IntPtr.Size)) ?? name;
                    var state = (ServiceRunState)Marshal.ReadInt32(entry, 2 * IntPtr.Size + sizeof(uint));
                    result.Add(new ServiceInfo(name, display, state, ""));
                }
                if (ok) return result;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    private static int Align(int size, int to) => (size + to - 1) / to * to;

    /// <summary>
    /// QUERY_SERVICE_CONFIGW.dwStartType (offset 4) and the delayed-start flag; null when unreadable. The one start-type
    /// reader: the broker's <c>service.setStartType</c> reads its result back through it too.
    /// </summary>
    internal static string? StartType(nint svc)
    {
        int raw = ReadConfig(svc, delayed: false);
        if (raw < 0) return null;
        bool delayed = false;
        if (raw == 2)
        {
            int flag = ReadConfig(svc, delayed: true);
            if (flag < 0) return null;
            delayed = flag == 1;
        }
        return StartTypeName(raw, delayed);
    }

    /// <summary>The name of a SERVICE_*_START value, as the modes and the broker's requests spell it.</summary>
    internal static string StartTypeName(int start, bool delayed) => start switch
    {
        0 => "boot",
        1 => "system",
        2 => delayed ? "delayedAutomatic" : "automatic",
        3 => "manual",
        4 => "disabled",
        _ => "unknown",
    };

    private static int ReadConfig(nint svc, bool delayed)
    {
        uint needed;
        _ = delayed ? QueryServiceConfig2(svc, ServiceConfigDelayedAutoStartInfo, 0, 0, out needed) : QueryServiceConfig(svc, 0, 0, out needed);
        if (Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer) return -1;
        nint buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            bool ok = delayed ? QueryServiceConfig2(svc, ServiceConfigDelayedAutoStartInfo, buffer, needed, out _) : QueryServiceConfig(svc, buffer, needed, out _);
            if (!ok) return -1;
            return delayed ? Marshal.ReadInt32(buffer) : Marshal.ReadInt32(buffer, sizeof(uint));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>SERVICE_STATUS (seven DWORDs).dwCurrentState, its second field; -1 when unreadable.</summary>
    private static unsafe int CurrentState(nint svc)
    {
        uint* status = stackalloc uint[7];
        return QueryServiceStatus(svc, status) ? (int)status[1] : -1;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint OpenService(nint scManager, string serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryServiceStatus(nint service, uint* status);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceConfig(nint service, nint buffer, uint bufferSize, out uint bytesNeeded);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfig2W", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceConfig2(nint service, uint infoLevel, nint buffer, uint bufferSize, out uint bytesNeeded);

    [LibraryImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumServicesStatusEx(nint scManager, int infoLevel, uint serviceType, uint serviceState, nint services,
        uint bufferSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, nint groupName);
}
