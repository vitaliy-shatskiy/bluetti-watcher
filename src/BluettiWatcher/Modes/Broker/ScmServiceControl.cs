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
