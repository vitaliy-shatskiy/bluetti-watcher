using System.Runtime.InteropServices;

namespace BluettiWatcher.Io;

/// <summary>The Windows memory calls made after each poll; each returns 0 when done, else the Win32 error code.</summary>
internal interface IMemoryCalls
{
    /// <summary>HeapCompact on the process heap: its free blocks are coalesced and decommitted (private bytes go down).</summary>
    int HeapCompact();

    /// <summary>HeapSetInformation(HeapOptimizeResources) on every heap: the low-fragmentation heaps' caches are freed.</summary>
    int HeapOptimizeResources();

    /// <summary>SetProcessWorkingSetSizeEx(-1, -1): every page leaves the working set; commit is unchanged.</summary>
    int TrimWorkingSet();
}

/// <summary>
/// What the process gives back to Windows after each poll, after the garbage collection: the native heaps' free memory,
/// then (unless <c>trimWorkingSet</c> is false) its working set. A failing call never stops the service: it is logged
/// once and made again at the next poll.
/// </summary>
internal sealed class ProcessMemory(StationConfig config, IMemoryCalls calls, Action<string> log)
{
    private bool _compactLogged, _optimizeLogged, _trimLogged;

    public void AfterPoll()
    {
        Check(calls.HeapCompact(), "HeapCompact", ref _compactLogged);
        Check(calls.HeapOptimizeResources(), "HeapOptimizeResources", ref _optimizeLogged);
        if (config.TrimWorkingSet) Check(calls.TrimWorkingSet(), "the working set trim", ref _trimLogged);
    }

    private void Check(int error, string what, ref bool logged)
    {
        if (error == 0 || logged) return;
        logged = true;
        log($"memory: {what} failed (Win32 error {error}); not logged again");
    }
}

/// <summary>The kernel32 calls behind <see cref="IMemoryCalls"/>.</summary>
internal sealed partial class Win32MemoryCalls : IMemoryCalls
{
    private const int HeapOptimizeResourcesClass = 3;   // HEAP_INFORMATION_CLASS.HeapOptimizeResources
    private const uint HeapOptimizeResourcesCurrentVersion = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct HeapOptimizeResourcesInformation
    {
        public uint Version;
        public uint Flags;
    }

    public int HeapCompact()
    {
        // 0 with no error = no free block at all, not a failure.
        return NativeHeapCompact(GetProcessHeap(), 0) != 0 ? 0 : Marshal.GetLastPInvokeError();
    }

    public int HeapOptimizeResources()
    {
        var info = new HeapOptimizeResourcesInformation { Version = HeapOptimizeResourcesCurrentVersion, Flags = 0 };
        // A null heap handle = every heap of the process.
        return HeapSetInformation(0, HeapOptimizeResourcesClass, ref info, (nuint)Marshal.SizeOf<HeapOptimizeResourcesInformation>())
            ? 0 : Marshal.GetLastPInvokeError();
    }

    public int TrimWorkingSet() => SetProcessWorkingSetSizeEx(GetCurrentProcess(), -1, -1, 0) ? 0 : Marshal.GetLastPInvokeError();

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetProcessHeap();

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", EntryPoint = "HeapCompact", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nuint NativeHeapCompact(nint heap, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HeapSetInformation(nint heap, int informationClass, ref HeapOptimizeResourcesInformation information, nuint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSizeEx(nint process, nint minimum, nint maximum, uint flags);
}
