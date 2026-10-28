using System.Text.Json;

namespace BluettiWatcher.PowerMode;

/// <summary>
/// The page's view of <c>returnPlan</c> in power-mode.config.json beside the flag (power-mode/README.md, Configuration):
/// read as the switch reads it, set to one of the page's three choices. The switch only reads this file, so a write needs
/// no flag lock: a temp file in the same folder moved over it.
/// </summary>
internal static class ReturnPlanFile
{
    public const string FileName = "power-mode.config.json";
    /// <summary>A plan GUID set by hand: shown, never written by the page.</summary>
    public const string Custom = "custom";

    /// <summary>The choices the page may set.</summary>
    public static bool IsChoice(string? value) => value is "high" or "balanced" or "previous";

    public static string PathOf(string flagFile) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(flagFile)) ?? "", FileName);

    /// <summary>high | balanced | previous | custom (another plan GUID), as <see cref="PowerModeSwitch.ReadConfig"/> resolves it.</summary>
    public static string Read(string flagFile) => PowerModeSwitch.ReadConfig(flagFile).ReturnPlan switch
    {
        PowerModeSwitch.HighPlan => "high",
        PowerModeSwitch.BalancedPlan => "balanced",
        "previous" => "previous",
        _ => Custom,
    };

    /// <summary>
    /// Sets <c>returnPlan</c>, every other key kept as it is · (0, "") = written · else (HTTP status, why): 409 the folder is
    /// missing or the file is not a JSON object (nothing written), 500 the write failed (the old file stays).
    /// </summary>
    public static (int Status, string Error) Write(string flagFile, string choice)
    {
        if (!IsChoice(choice)) throw new ArgumentOutOfRangeException(nameof(choice), choice, "not high, balanced or previous");
        string path = PathOf(flagFile);
        string dir = Path.GetDirectoryName(path) ?? "";
        if (!Directory.Exists(dir)) return (409, $"the power-mode folder {dir} does not exist yet (the first station reading makes it)");
        byte[] text;
        try { text = Compose(File.Exists(path) ? PowerModeSwitch.ReadText(path) : "{}", choice); }
        catch (JsonException) { return (409, $"{FileName} is not a JSON object: fix it by hand"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (500, $"{FileName} cannot be read: {e.Message}"); }
        string tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(tmp, text);
            File.Move(tmp, path, overwrite: true);
            return (0, "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (500, $"{FileName} could not be written: {e.Message}"); }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine(e.Message); }
        }
    }

    /// <summary>The file with returnPlan set: in place of the first returnPlan key (the switch reads keys without case), else last. Throws JsonException when not an object.</summary>
    internal static byte[] Compose(string current, string choice)
    {
        using var doc = JsonDocument.Parse(current.Length == 0 ? "{}" : current);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("not an object");
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            bool written = false;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(p.Name, "returnPlan", StringComparison.OrdinalIgnoreCase)) { p.WriteTo(w); continue; }
                if (written) continue;
                w.WriteString("returnPlan", choice);
                written = true;
            }
            if (!written) w.WriteString("returnPlan", choice);
            w.WriteEndObject();
        }
        ms.Write("\n"u8);
        return ms.ToArray();
    }
}
