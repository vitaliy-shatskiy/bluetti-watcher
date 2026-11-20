namespace BluettiWatcher.Modes.Modules;

/// <summary>A running app with a visible window: its executable name (<c>notepad.exe</c>) and the window title.</summary>
internal sealed record RunningApp(string ExeName, string Title);

/// <summary>
/// The user's processes in this session, by executable name (<c>name.exe</c>, case-insensitive). Nothing here starts a
/// process; other sessions' processes are never counted or touched.
/// </summary>
internal interface IProcessControl
{
    /// <summary>Apps with a visible top-level window; null when the process list cannot be read.</summary>
    IReadOnlyList<RunningApp>? ListApps();

    /// <summary>How many processes of <paramref name="exeName"/> run in this session; null when it cannot be read.</summary>
    int? Count(string exeName);

    /// <summary>Asks every visible top-level window of <paramref name="exeName"/> to close (WM_CLOSE); returns how many were asked.</summary>
    int RequestClose(string exeName);

    /// <summary>Ends every process of <paramref name="exeName"/> in this session; returns how many were ended.</summary>
    int Kill(string exeName);
}
