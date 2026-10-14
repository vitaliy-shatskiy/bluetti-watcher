namespace BluettiWatcher.Storage;

/// <summary>
/// SQLite is Windows' own <c>winsqlite3.dll</c> (System32, on every Windows 10 and 11): no SQLite library ships beside the
/// exe, and its version follows Windows Update. Set once, before the first connection opens.
/// </summary>
internal static class SqliteSetup
{
    /// <summary>3.24.0: the store's upserts (<c>INSERT ... ON CONFLICT ... DO UPDATE</c>) need it.</summary>
    public const int MinVersionNumber = 3_024_000;
    public const string MinVersion = "3.24.0";

    /// <summary>The service's exit code when Windows' SQLite is missing or older than <see cref="MinVersion"/>.</summary>
    public const int ExitTooOld = 3;

    static SqliteSetup() => SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_winsqlite3());

    /// <summary>Makes sure the provider is set (the static constructor runs once, thread-safe).</summary>
    public static void Init()
    {
        // The work is the static constructor's.
    }

    /// <summary>The start check on Windows' SQLite: null when it can run the store, else <see cref="ExitTooOld"/> after one log line.</summary>
    public static int? StartGate(Action<string> log) =>
        // Init inside the version source: a missing winsqlite3.dll fails the provider's static constructor
        // (TypeInitializationException), which the gate turns into its refusal line.
        StartGate(() => { Init(); return SQLitePCL.raw.sqlite3_libversion_number(); }, () => SQLitePCL.raw.sqlite3_libversion().utf8_to_string(), log);

    /// <summary>The check behind <see cref="StartGate(Action{string})"/>, on any version source.</summary>
    internal static int? StartGate(Func<int> versionNumber, Func<string> version, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(versionNumber);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(log);
        string? problem;
        try
        {
            int number = versionNumber();
            problem = number >= MinVersionNumber ? null
                : $"SQLite {version()} in Windows (winsqlite3.dll) is older than {MinVersion}, which the database needs: update Windows";
        }
        catch (Exception e) when (e is TypeInitializationException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            var cause = e is TypeInitializationException { InnerException: { } inner } ? inner : e;
            problem = $"Windows' SQLite (winsqlite3.dll) could not be loaded ({cause.GetType().Name}: {cause.Message}); SQLite {MinVersion} or later is needed";
        }
        if (problem is null) return null;
        log($"DID NOT RUN - {problem}");
        return ExitTooOld;
    }
}
