namespace BluettiWatcher.Storage;

/// <summary>
/// SQLite is Windows' own <c>winsqlite3.dll</c> (System32, on every Windows 10 and 11): no SQLite library ships beside the
/// exe, and its version follows Windows Update. Set once, before the first connection opens.
/// </summary>
internal static class SqliteSetup
{
    static SqliteSetup() => SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());

    /// <summary>Makes sure the provider is set (the static constructor runs once, thread-safe).</summary>
    public static void Init()
    {
        // The work is the static constructor's.
    }
}
