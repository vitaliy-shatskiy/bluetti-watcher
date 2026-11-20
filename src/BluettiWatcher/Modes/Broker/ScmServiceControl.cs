using System.Globalization;
using System.Runtime.InteropServices;

namespace BluettiWatcher.Modes.Broker;

/// <summary>
/// Services through the Service Control Manager API (advapi32): a service is opened by its key name, never through a
/// command line. Start and stop wait up to <see cref="Settle"/> for the service to get there and answer the state read then.
/// </summary>
internal sealed partial class ScmServiceControl(TimeProvider time) : IServiceControl
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004, ServiceStart = 0x0010, ServiceStop = 0x0020;
    private const uint ControlStop = 1;
    private const int ErrorServiceAlreadyRunning = 1056, ErrorServiceNotActive = 1062;
    private const uint Stopped = 1, Running = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint Type, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    private static string StateName(uint state) => state switch
    {
        1 => "stopped",
        2 => "start-pending",
        3 => "stop-pending",
        4 => "running",
        5 => "continue-pending",
        6 => "pause-pending",
        7 => "paused",
        _ => $"state {state.ToString(CultureInfo.InvariantCulture)}",
    };

    private static BrokerResult WinError(string what) =>
        new(false, $"{what}: Windows error {Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture)}", null);

    public Task<BrokerResult> QueryAsync(string name, CancellationToken ct) => RunAsync(name, null, ct);

    public Task<BrokerResult> StartAsync(string name, CancellationToken ct) => RunAsync(name, true, ct);

    public Task<BrokerResult> StopAsync(string name, CancellationToken ct) => RunAsync(name, false, ct);

    /// <summary>start: true, stop: false, query: null.</summary>
    private async Task<BrokerResult> RunAsync(string name, bool? start, CancellationToken ct)
    {
        nint scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == 0) return WinError("the Service Control Manager could not be opened");
        try
        {
            uint access = ServiceQueryStatus | (start switch { true => ServiceStart, false => ServiceStop, null => 0u });
            nint svc = OpenServiceW(scm, name, access);
            if (svc == 0) return WinError($"the service {name} could not be opened");
            try
            {
                if (start == true && !StartServiceW(svc, 0, 0) && Marshal.GetLastPInvokeError() != ErrorServiceAlreadyRunning)
                    return WinError($"the service {name} could not be started");
                if (start == false && !ControlService(svc, ControlStop, out _) && Marshal.GetLastPInvokeError() != ErrorServiceNotActive)
                    return WinError($"the service {name} could not be stopped");
                uint want = start switch { true => Running, false => Stopped, null => 0u };
                long deadline = time.GetTimestamp();
                ServiceStatus status;
                while (true)
                {
                    if (!QueryServiceStatus(svc, out status)) return WinError($"the service {name} could not be read");
                    if (want == 0 || status.CurrentState == want || time.GetElapsedTime(deadline) > Settle) break;
                    await Task.Delay(TimeSpan.FromMilliseconds(200), time, ct);
                }
                return new(true, null, StateName(status.CurrentState));
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>The start types a request may name, with their SERVICE_*_START value and the delayed flag.</summary>
    internal static readonly IReadOnlyDictionary<string, (uint Start, bool Delayed)> StartTypes = new Dictionary<string, (uint, bool)>(StringComparer.Ordinal)
    {
        ["automatic"] = (2, false),
        ["delayedAutomatic"] = (2, true),
        ["manual"] = (3, false),
        ["disabled"] = (4, false),
    };

    private const uint ServiceQueryConfig = 0x0001, ServiceChangeConfig = 0x0002, NoChange = 0xFFFFFFFF, DelayedAutoStartInfo = 3;

    public Task<BrokerResult> SetStartTypeAsync(string name, string startType, CancellationToken ct)
    {
        if (!StartTypes.TryGetValue(startType, out var want)) return Task.FromResult(new BrokerResult(false, $"unknown start type {startType}", null));
        nint scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == 0) return Task.FromResult(WinError("the Service Control Manager could not be opened"));
        try
        {
            nint svc = OpenServiceW(scm, name, ServiceQueryConfig | ServiceChangeConfig);
            if (svc == 0) return Task.FromResult(WinError($"the service {name} could not be opened"));
            try
            {
                if (!ChangeServiceConfigW(svc, NoChange, want.Start, NoChange, null, null, 0, null, null, null, null))
                    return Task.FromResult(WinError($"the start type of {name} could not be set"));
                int delayed = want.Delayed ? 1 : 0;
                if (want.Start == 2 && !ChangeServiceConfig2W(svc, DelayedAutoStartInfo, ref delayed))
                    return Task.FromResult(WinError($"the delayed start of {name} could not be set"));
                return Task.FromResult(ReadStartType(svc, name));
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    private static BrokerResult ReadStartType(nint svc, string name) =>
        Modules.WindowsServiceReader.StartType(svc) is { } type ? new(true, null, type) : WinError($"the start type of {name} could not be read");

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfigW(nint service, uint type, uint start, uint errorControl, string? binary, string? group, nint tag, string? dependencies, string? account, string? password, string? displayName);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeServiceConfig2W(nint service, uint level, ref int info);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint OpenSCManagerW(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint OpenServiceW(nint scm, string name, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool StartServiceW(nint service, uint argc, nint argv);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ControlService(nint service, uint control, out ServiceStatus status);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatus(nint service, out ServiceStatus status);

    [LibraryImport("advapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(nint handle);
}
