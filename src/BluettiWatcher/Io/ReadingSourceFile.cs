using System.Text.Json;

namespace BluettiWatcher.Io;

/// <summary>
/// The page's reading-source choice, <c>reading-source.json</c> in the data directory: <c>{"readingSource":"cloud"}</c>.
/// It wins over the config key from the moment it is saved (no restart); deleting the file returns to the key.
/// </summary>
internal static class ReadingSourceFile
{
    public const string FileName = "reading-source.json";

    public static string PathOf(string dataDir) => Path.Combine(dataDir, FileName);

    /// <summary>auto | bluetooth | cloud, as the API and the files spell them.</summary>
    public static string Name(ReadingSource s) => s switch
    {
        ReadingSource.Bluetooth => "bluetooth",
        ReadingSource.Cloud => "cloud",
        _ => "auto",
    };

    public static ReadingSource? Parse(string? name) => name switch
    {
        "auto" => ReadingSource.Auto,
        "bluetooth" => ReadingSource.Bluetooth,
        "cloud" => ReadingSource.Cloud,
        _ => null,
    };

    /// <summary>The saved choice; null when there is none or the file is unreadable (the reason goes to <paramref name="log"/>).</summary>
    public static ReadingSource? Read(string dataDir, Action<string> log)
    {
        string path = PathOf(dataDir);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            string? name = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("readingSource", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (Parse(name) is { } s) return s;
            log($"CONFIG - {FileName} holds no valid readingSource (auto, bluetooth or cloud): the config key applies");
            return null;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            log($"CONFIG - {FileName} is unreadable ({e.GetType().Name}): the config key applies");
            return null;
        }
    }

    /// <summary>Temp file beside it, then a replace: a crash never leaves half a file. Throws IOException / UnauthorizedAccessException.</summary>
    public static void Write(string dataDir, ReadingSource s)
    {
        Directory.CreateDirectory(dataDir);
        string path = PathOf(dataDir), tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using (var w = new Utf8JsonWriter(fs))
            {
                w.WriteStartObject();
                w.WriteString("readingSource", Name(s));
                w.WriteEndObject();
            }
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
