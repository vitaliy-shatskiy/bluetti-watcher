using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BluettiWatcher.Modes.Modules;

/// <summary><see cref="IProcessControl"/> over <see cref="Process"/> and user32 (EnumWindows, PostMessage WM_CLOSE), limited to this session.</summary>
internal sealed partial class WindowsProcessControl : IProcessControl
{
    private const uint WmClose = 0x0010;
    private static readonly int Session = CurrentSession();

    private static int CurrentSession()
    {
        using var me = Process.GetCurrentProcess();
        return me.SessionId;
    }

    public IReadOnlyList<RunningApp>? ListApps()
    {
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch (Win32Exception) { return null; }
        var apps = new List<RunningApp>();
        foreach (var p in all)
        {
            using (p)
            {
                try
                {
                    if (p.SessionId != Session || p.MainWindowHandle == 0) continue;
                    apps.Add(new RunningApp(p.ProcessName + ".exe", p.MainWindowTitle));
                }
                catch (InvalidOperationException) { /* exited while listed: not running, nothing to show */ }
            }
        }
        return apps;
    }

    public int? Count(string exeName)
    {
        try { return Mine(exeName).Count(p => { using (p) return true; }); }
        catch (Win32Exception) { return null; }
    }

    public int RequestClose(string exeName)
    {
        var pids = new HashSet<uint>();
        foreach (var p in Mine(exeName)) using (p) pids.Add((uint)p.Id);
        if (pids.Count == 0) return 0;
        int asked = 0;
        foreach (nint hwnd in TopLevelWindows())
        {
            if (!IsWindowVisible(hwnd)) continue;
            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (pids.Contains(pid) && PostMessage(hwnd, WmClose, 0, 0)) asked++;
        }
        return asked;
    }

    public int Kill(string exeName)
    {
        int ended = 0;
        foreach (var p in Mine(exeName))
        {
            using (p)
            {
                try
                {
                    p.Kill();
                    ended++;
                }
                // Already gone, or protected: the read-back after the kill reports what still runs.
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        return ended;
    }

    /// <summary>This session's processes of an executable name (<c>name.exe</c> or <c>name</c>).</summary>
    private static IEnumerable<Process> Mine(string exeName)
    {
        string bare = exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exeName[..^4] : exeName;
        foreach (var p in Process.GetProcessesByName(bare))
        {
            bool mine;
            try { mine = p.SessionId == Session; }
            catch (InvalidOperationException) { mine = false; }
            if (mine) yield return p;
            else p.Dispose();
        }
    }

    private static unsafe List<nint> TopLevelWindows()
    {
        var list = new List<nint>();
        var handle = GCHandle.Alloc(list);
        try { _ = EnumWindows(&Collect, GCHandle.ToIntPtr(handle)); }
        finally { handle.Free(); }
        return list;
    }

    [UnmanagedCallersOnly]
    private static int Collect(nint hwnd, nint state)
    {
        if (GCHandle.FromIntPtr(state).Target is List<nint> list) list.Add(hwnd);
        return 1;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint state);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
}
