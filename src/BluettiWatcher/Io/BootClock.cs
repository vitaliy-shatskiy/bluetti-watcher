using System.Runtime.InteropServices;

namespace BluettiWatcher.Io;

/// <summary>
/// When Windows last started: the wall clock minus the time since boot, sleep and hibernation included. Never
/// <c>Environment.TickCount64</c>: from .NET 11 it is the awake time on Windows too, so a boot time built on it lands
/// after the real one by every hour the machine slept.
/// </summary>
internal static partial class BootClock
{
    /// <summary>The boot time on <paramref name="time"/>'s wall clock, given the time since boot (sleep included).</summary>
    public static DateTimeOffset BootAt(TimeProvider time, Func<TimeSpan> sinceBoot)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(sinceBoot);
        return time.GetUtcNow() - sinceBoot();
    }

    /// <summary>The time since Windows started, sleep and hibernation included (Win32 GetTickCount64).</summary>
    public static TimeSpan SinceBoot() => TimeSpan.FromMilliseconds(checked((long)GetTickCount64()));

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial ulong GetTickCount64();
}
