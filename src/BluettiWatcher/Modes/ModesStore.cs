using System.Globalization;
using System.Text.Json;

namespace BluettiWatcher.Modes;

/// <summary>
/// <c>modes.json</c> in the data directory: read at start, replaced atomically (a temp file in the same folder moved over
/// it) on every change. Absent = made from the migration. A file this build cannot parse is moved aside and replaced; one
/// written by a newer build is left alone (never half-read, never overwritten) and the service runs on the defaults in
/// memory until it is fixed.
/// </summary>
internal sealed class ModesStore(string dataDir)
{
    public const string FileName = "modes.json";

    public string Path => System.IO.Path.Combine(dataDir, FileName);

    /// <summary>Whether <see cref="Save"/> may write (false after a newer or unreadable file was found).</summary>
    public bool Writable { get; private set; } = true;

    /// <summary>The stored settings (with the built-ins restored if missing), or <paramref name="initial"/> when there are none; the problem, if any.</summary>
    public (ModesSettings Settings, string? Problem) Load(Func<ModesSettings> initial, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(initial);
        if (!File.Exists(Path))
        {
            var fresh = initial();
            Save(fresh);
            return (fresh, null);
        }
        ModesSettings? read;
        string? problem = null;
        try { read = JsonSerializer.Deserialize(File.ReadAllText(Path), ModesJson.Default.ModesSettings); }
        catch (JsonException e) { read = null; problem = e.Message; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Writable = false;
            return (initial(), $"{FileName} cannot be read ({e.Message}): defaults in use, the file is left as it is");
        }
        if (read is null)
        {
            string aside = $"{Path}.bad-{now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}";
            File.Move(Path, aside, overwrite: true);
            var fresh = initial();
            Save(fresh);
            return (fresh, $"{FileName} unreadable ({problem ?? "empty"}): moved to {System.IO.Path.GetFileName(aside)}, defaults written");
        }
        if (read.Version > ModesSettings.CurrentVersion)
        {
            Writable = false;
            return (initial(), $"{FileName} is version {read.Version.ToString(CultureInfo.InvariantCulture)}, newer than this build reads ({ModesSettings.CurrentVersion.ToString(CultureInfo.InvariantCulture)}): defaults in use, the file is left as it is");
        }
        return (Repair(read), null);
    }

    /// <summary>The built-ins are always there (editable, never deletable); the fallback is a mode that exists.</summary>
    internal static ModesSettings Repair(ModesSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var modes = s.Modes.ToList();
        foreach (var id in new[] { BuiltInModes.Normal, BuiltInModes.Eco, BuiltInModes.Max })
        {
            int at = modes.FindIndex(m => m.Id == id);
            if (at < 0) modes.Add(BuiltInModes.Fresh(id));
            else if (!modes[at].BuiltIn) modes[at] = modes[at] with { BuiltIn = true };
        }
        string fallback = modes.Any(m => m.Id == s.FallbackModeId) ? s.FallbackModeId : BuiltInModes.Normal;
        return s with { Version = ModesSettings.CurrentVersion, Modes = modes, FallbackModeId = fallback };
    }

    public void Save(ModesSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Writable) throw new InvalidOperationException($"{FileName} was written by a newer build or cannot be read: not overwritten");
        Directory.CreateDirectory(dataDir);
        string tmp = $"{Path}.{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(settings, ModesJson.Default.ModesSettings));
            File.Move(tmp, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
