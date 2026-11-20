using System.Text.Json;
using BluettiWatcher.Http;

namespace BluettiWatcher.Io;

/// <summary>
/// The page's own settings, <c>page-settings.json</c> in the data directory (never config.json): GET /api/settings/page and,
/// behind <see cref="RequestGuard.WriteRefusal"/>, PUT with its booleans. Today one: <c>hideSourcePanel</c>. A missing or
/// unreadable file is the defaults (an unreadable one is logged and rewritten only by a change).
/// </summary>
internal sealed class PageSettingsStore
{
    public const string Route = "/api/settings/page";
    public const string FileName = "page-settings.json";
    private const string HideSourcePanel = "hideSourcePanel";

    private readonly string _path;
    private readonly Lock _gate = new();
    private bool _hideSourcePanel;

    public PageSettingsStore(string path, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _path = path;
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (Read(doc.RootElement) is { } hide) _hideSourcePanel = hide;
            else log($"page settings: {path} ignored (not an object of known booleans); the defaults apply");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            log($"page settings: {path} unreadable ({e.GetType().Name}); the defaults apply");
        }
    }

    /// <summary>The body's hideSourcePanel; null when the body is not an object holding only known booleans.</summary>
    private static bool? Read(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return null;
        bool? hide = null;
        foreach (var p in body.EnumerateObject())
        {
            if (p.Name != HideSourcePanel || p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            hide = p.Value.GetBoolean();
        }
        return hide;
    }

    public HttpResponse Handle(HttpRequest r, int port)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.Method == "GET") return Answer();
        if (r.Method != "PUT") return HttpResponse.Text(405, "GET or PUT only");
        if (RequestGuard.WriteRefusal(r, port) is { } why) return HttpResponse.Text(403, "forbidden: " + why);
        bool? hide;
        try
        {
            using var doc = JsonDocument.Parse(r.Body);
            hide = Read(doc.RootElement);
        }
        catch (JsonException) { hide = null; }
        if (hide is not { } value) return HttpResponse.Text(400, $"body must be JSON {{ \"{HideSourcePanel}\": true | false }}");
        lock (_gate)
        {
            try { Save(value); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return HttpResponse.Text(500, $"{FileName} could not be written ({e.GetType().Name})"); }
            _hideSourcePanel = value;
        }
        return Answer();
    }

    private HttpResponse Answer()
    {
        bool hide;
        lock (_gate) hide = _hideSourcePanel;
        return HttpResponse.Json(200, Json(hide));
    }

    private static byte[] Json(bool hide)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteBoolean(HideSourcePanel, hide);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>Temp file beside it, then a replace: a crash never leaves half a file.</summary>
    private void Save(bool hide)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);   // a full path always has a directory
        string tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, Json(hide));
        File.Move(tmp, _path, overwrite: true);
    }
}
