using System.Globalization;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BluettiWatcher.PowerMode;

/// <summary>Ends a power-mode command with an exit code from any depth (the script's <c>exit</c>).</summary>
internal sealed class PowerModeExit : Exception
{
    public PowerModeExit() : this(2) { }

    public PowerModeExit(string message) : base(message) => Code = 2;

    public PowerModeExit(string message, Exception innerException) : base(message, innerException) => Code = 2;

    public PowerModeExit(int code) : base($"exit {code.ToString(CultureInfo.InvariantCulture)}") => Code = code;

    public int Code { get; }
}

/// <summary>
/// The power-mode switch, in the exe: what power-mode.ps1, power-mode-end.ps1 and power-mode-flag.ps1 did, with the same
/// verbs, flags, exit codes, output lines, flag-file bytes, mutex and scheduled tasks. power-mode/README.md is the user's guide.
/// One instance per command: the environment it reads (the POWER_MODE_* names and test seams) and where its lines go.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed partial class PowerModeSwitch : IDisposable
{
    internal const string BalancedPlan = "381b4222-f694-41f0-9685-ff5bb260df2e";
    internal const string HighPlan = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    internal const string SaverPlan = "a1841308-3541-4fab-bc81-f71556f20b4a";
    internal const string BestPerformanceOverlay = "ded574b5-45a0-4f42-8737-46345c09c238";
    internal const string SystemSid = "S-1-5-18";
    internal const string AdministratorsSid = "S-1-5-32-544";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly Dictionary<string, string> _env;
    /// <summary>Where the lines go (the writer is the caller's).</summary>
    private readonly Action<string> _write;

    /// <param name="env">The environment the command sees (names compared without case, as Windows does).</param>
    /// <param name="output">Where the command's lines go.</param>
    /// <param name="exePath">The exe the end task runs (this process's own when not given).</param>
    public PowerModeSwitch(IEnumerable<KeyValuePair<string, string>> env, TextWriter output, string? exePath = null)
    {
        _env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in env) _env[k] = v;
        _write = output.WriteLine;
        ExePath = exePath ?? Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "BluettiWatcher.exe");
    }

    /// <summary>The process environment as a list, for the constructor.</summary>
    public static List<KeyValuePair<string, string>> ProcessEnvironment()
    {
        var list = new List<KeyValuePair<string, string>>();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            list.Add(new((string)e.Key, (string?)e.Value ?? ""));
        return list;
    }

    public string ExePath { get; }
    public string MutexName { get; private set; } = "Local\\BluettiWatcherPowerMode";
    public string TaskPrefix { get; private set; } = "BluettiWatcherEco";
    public IReadOnlyList<string> ExtraReaders { get; private set; } = [];
    public string EndTask => TaskPrefix + "End";
    public string GpuCapTask => TaskPrefix + "GpuCap";
    public string GpuRestoreTask => TaskPrefix + "GpuRestore";

    /// <summary>An environment variable as PowerShell's <c>$env:X</c> reads it: "" when not set.</summary>
    private string Env(string name) => _env.TryGetValue(name, out var v) ? v : "";

    private void SetEnv(string name, string value)
    {
        if (value.Length == 0) _env.Remove(name);
        else _env[name] = value;
    }

    private void Say(string line) => _write(line);

    private void SayAll(IEnumerable<string> lines)
    {
        foreach (var l in lines) _write(l);
    }

    private static string Num(double v) => v.ToString(Inv);
    private static string Num(int v) => v.ToString(Inv);
    internal static string Watts(double w) => w.ToString("0.##", Inv);
    internal static string Iso(DateTimeOffset at) => at.ToString("yyyy-MM-dd'T'HH:mm:sszzz", Inv);

    // ---- the live names (power-mode-flag.ps1, top) -----------------------------------------------------------------

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,99}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();

    [GeneratedRegex(@"^(Local|Global)\\[A-Za-z0-9_.-]{1,100}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MutexPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_. -]{0,63}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReaderPattern();

    /// <summary>Reads and checks POWER_MODE_MUTEX, _TASK_PREFIX and _EXTRA_READERS; a name that is not plain throws.</summary>
    private void LoadNames()
    {
        MutexName = Env("POWER_MODE_MUTEX") is { Length: > 0 } m ? m : "Local\\BluettiWatcherPowerMode";
        TaskPrefix = Env("POWER_MODE_TASK_PREFIX") is { Length: > 0 } p ? p : "BluettiWatcherEco";
        if (!PrefixPattern().IsMatch(TaskPrefix))
            throw new InvalidOperationException($"POWER_MODE_TASK_PREFIX '{TaskPrefix}' is not 1-100 letters, digits, '_', '.' or '-', the first a letter, digit or '_'");
        if (!MutexPattern().IsMatch(MutexName))
            throw new InvalidOperationException($"POWER_MODE_MUTEX '{MutexName}' is not Local\\ or Global\\ plus 1-100 letters, digits, '_', '.' or '-'");
        ExtraReaders = [.. Env("POWER_MODE_EXTRA_READERS").Split(',').Select(r => r.Trim()).Where(r => r.Length > 0)];
        foreach (var reader in ExtraReaders)
        {
            if (!ReaderPattern().IsMatch(reader))
                throw new InvalidOperationException($"POWER_MODE_EXTRA_READERS '{reader}' is not 1-64 letters, digits, spaces, '_', '.' or '-', the first a letter, digit or '_'");
        }
    }

    /// <summary>The configured flag (POWER_MODE_FILE, else %ProgramData%\BluettiWatcher\power-mode.json), full path.</summary>
    public string DefaultFlagPath => Env("POWER_MODE_FILE") is { Length: > 0 } f
        ? Path.GetFullPath(f)
        : Path.GetFullPath(Path.Combine(Io.KnownFolders.ProgramData, "BluettiWatcher", "power-mode.json"));

    private string FlagPathOf(string path) => path.Length == 0 ? DefaultFlagPath : Path.GetFullPath(path);

    private bool IsDefaultFlag(string path) => string.Equals(Path.GetFullPath(path), DefaultFlagPath, StringComparison.OrdinalIgnoreCase);

    private List<string> ExtraReaderSids()
    {
        var sids = new List<string>();
        foreach (var name in ExtraReaders)
        {
            try { sids.Add(AccountSid(name)); }
            // As the scripts' bare catch: whatever the lookup throws (an unmapped name, a domain controller out of reach), the
            // reader is left out - one less account trusted, never a failed command.
            catch (Exception e) when (e is not OutOfMemoryException) { Skip(e); }
        }
        return sids;
    }

    /// <summary>The SID of an account name. POWER_MODE_TEST_FAULT=translate (with POWER_MODE_TEST=1) is the test seam: the lookup fails as one out of reach of its domain controller does.</summary>
    private string AccountSid(string name)
    {
        if (Env("POWER_MODE_TEST") == "1" && Env("POWER_MODE_TEST_FAULT") == "translate") throw new System.ComponentModel.Win32Exception(1722);
        return ((SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier))).Value;
    }

    /// <summary>What the scripts dropped on purpose (a reader name that does not resolve, a zone id that is not there).</summary>
    private static void Skip(Exception e) => System.Diagnostics.Debug.WriteLine(e.Message);

    /// <summary>The user the flag folder belongs to: the one running this. POWER_MODE_ME_SID (with POWER_MODE_TEST=1) is the test seam: someone else.</summary>
    private string MeSid() => Env("POWER_MODE_TEST") == "1" && Env("POWER_MODE_ME_SID") is { Length: > 0 } seam ? seam : WindowsIdentity.GetCurrent().User?.Value ?? "";

    // ---- time ---------------------------------------------------------------------------------------------------------

    /// <summary>The zone times are shown in: the PC's own; POWER_MODE_TZ (test seam) = zone ids to try, comma-separated.</summary>
    private TimeZoneInfo Zone()
    {
        string tz = Env("POWER_MODE_TZ");
        if (tz.Length == 0) return TimeZoneInfo.Local;
        foreach (var id in tz.Split(','))
        {
            if (FindZone(id) is { } z) return z;
        }
        throw new InvalidOperationException($"no time zone {tz} on this machine");
    }

    private static TimeZoneInfo? FindZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            Skip(e);
            // An IANA id (PowerShell 7 took them through ICU): the exe runs in invariant globalization, so Windows' own ICU
            // names the Windows zone.
            if (IanaToWindows(id) is not { } windowsId) return null;
            try { return TimeZoneInfo.FindSystemTimeZoneById(windowsId); }
            catch (Exception again) when (again is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException) { Skip(again); return null; }
        }
    }

    [System.Runtime.InteropServices.LibraryImport("icu.dll", EntryPoint = "ucal_getWindowsTimeZoneID", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static partial int UcalGetWindowsTimeZoneId(string id, int length, [System.Runtime.InteropServices.Out] char[] windowsId, int capacity, ref int status);

    /// <summary>The Windows zone id of an IANA id through System32 icu.dll (Windows 10 1903 and later) · null = none.</summary>
    private static string? IanaToWindows(string id)
    {
        if (id.Length == 0 || !id.Contains('/', StringComparison.Ordinal)) return null;
        try
        {
            var buffer = new char[128];
            int status = 0;
            int n = UcalGetWindowsTimeZoneId(id, id.Length, buffer, buffer.Length, ref status);
            return status <= 0 && n > 0 && n <= buffer.Length ? new string(buffer, 0, n) : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { Skip(e); return null; }
    }

    private string FormatTime(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Zone()).ToString("dd.MM.yyyy HH:mm", Inv);

    // ---- JSON as PowerShell saw it ---------------------------------------------------------------------------------------

    /// <summary>
    /// The top-level object of a flag or config file: keys without case (a PowerShell hashtable, the last duplicate wins),
    /// strings as text, numbers as double, true/false as bool, the rest null · null = not a JSON object.
    /// </summary>
    internal static Dictionary<string, object?>? ParseObject(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? (Dictionary<string, object?>?)Convert(doc.RootElement) : null;
        }
        catch (Exception e) when (e is JsonException or FormatException or ArgumentException or InvalidOperationException) { Skip(e); return null; }
    }

    private static object? Convert(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Object => el.EnumerateObject().Aggregate(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase), (h, p) => { h[p.Name] = Convert(p.Value); return h; }),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static object? Get(Dictionary<string, object?> h, string key) => h.TryGetValue(key, out var v) ? v : null;

    /// <summary>A file's text, read so that a writer is never blocked (the writers replace it by a move); a BOM is dropped.</summary>
    internal static string ReadText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, new System.Text.UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
