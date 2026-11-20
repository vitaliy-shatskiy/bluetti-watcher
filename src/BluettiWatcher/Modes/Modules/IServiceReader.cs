namespace BluettiWatcher.Modes.Modules;

/// <summary>A service's run state as the service manager reports it (SERVICE_STATUS.dwCurrentState).</summary>
internal enum ServiceRunState
{
    Stopped = 1,
    StartPending = 2,
    StopPending = 3,
    Running = 4,
    ContinuePending = 5,
    PausePending = 6,
    Paused = 7,
}

/// <summary>One installed service: its key name, display name, run state and start type (<c>automatic</c>, <c>delayedAutomatic</c>, <c>manual</c>, <c>disabled</c>, <c>boot</c>, <c>system</c>).</summary>
internal sealed record ServiceInfo(string Name, string DisplayName, ServiceRunState State, string StartType);

/// <summary>A query's answer: the service, or why it could not be read.</summary>
internal sealed record ServiceQuery(ServiceInfo? Info, string Error);

/// <summary>
/// Read-only access to the service manager with the user's own rights (query and enumerate need no elevation). Every
/// change goes through the admin broker, never through this port.
/// </summary>
internal interface IServiceReader
{
    ServiceQuery Query(string name);

    /// <summary>Every Win32 service installed; null when the service manager cannot be read.</summary>
    IReadOnlyList<ServiceInfo>? List();
}
