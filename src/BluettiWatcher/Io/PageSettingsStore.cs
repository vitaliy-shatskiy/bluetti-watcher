using System.Text.Json;
using BluettiWatcher.Http;

namespace BluettiWatcher.Io;

/// <summary>
/// The page's own settings, <c>page-settings.json</c> in the data directory (never config.json): GET /api/settings/page and,
/// behind <see cref="RequestGuard.WriteRefusal"/>, PUT with any of its fields; the others are kept. <c>hideSourcePanel</c>
/// (bool) and <c>lang</c> ("en" or "uk": the page's language, which the tray follows through <see cref="Lang"/>). A missing
/// or unreadable file is the defaults (an unreadable one is logged and rewritten only by a change).
/// </summary>
internal sealed class PageSettingsStore
{
    public const string Route = "/api/settings/page";
    public const string FileName = "page-settings.json";
    private const string HideSourcePanel = "hideSourcePanel";
    private const string LangKey = "lang";

    private readonly string _path;
    private readonly Lock _gate = new();
    private bool _hideSourcePanel;
    private string _lang = "en";

    public PageSettingsStore(string path, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _path = path;
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (Read(doc.RootElement) is { } read)
            {
                _hideSourcePanel = read.Hide ?? false;
                _lang = read.Lang ?? "en";
            }
            else log($"page settings: {path} ignored (not an object of known fields); the defaults apply");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            log($"page settings: {path} unreadable ({e.GetType().Name}); the defaults apply");
        }
    }

    /// <summary>The page's language, "en" or "uk"; "en" until a page sent one.</summary>
    public string Lang
    {
        get { lock (_gate) return _lang; }
    }

    /// <summary>The fields the body names; null when it is not an object of known keys with valid values.</summary>
    private static (bool? Hide, string? Lang)? Read(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return null;
        bool? hide = null;
        string? lang = null;
        foreach (var p in body.EnumerateObject())
        {
            switch (p.Name)
            {
                case HideSourcePanel when p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    hide = p.Value.GetBoolean();
                    break;
                case LangKey when p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is "en" or "uk":
                    lang = p.Value.GetString();
                    break;
                default:
                    return null;
            }
        }
        return (hide, lang);
    }

    public HttpResponse Handle(HttpRequest r, int port)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.Method == "GET") return Answer();
        if (r.Method != "PUT") return HttpResponse.Text(405, "GET or PUT only");
        if (RequestGuard.WriteRefusal(r, port) is { } why) return HttpResponse.Text(403, "forbidden: " + why);
        (bool? Hide, string? Lang)? read;
        try
        {
            using var doc = JsonDocument.Parse(r.Body);
            read = Read(doc.RootElement);
        }
        catch (JsonException) { read = null; }
        if (read is not { } put || (put.Hide is null && put.Lang is null))
            return HttpResponse.Text(400, $"body must be a JSON object with \"{HideSourcePanel}\": true | false and/or \"{LangKey}\": \"en\" | \"uk\"");
        lock (_gate)
        {
            bool hide = put.Hide ?? _hideSourcePanel;
            string lang = put.Lang ?? _lang;
            try { Save(hide, lang); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return HttpResponse.Text(500, $"{FileName} could not be written ({e.GetType().Name})"); }
            _hideSourcePanel = hide;
            _lang = lang;
        }
        return Answer();
    }

    private HttpResponse Answer()
    {
        bool hide;
        string lang;
        lock (_gate) (hide, lang) = (_hideSourcePanel, _lang);
        return HttpResponse.Json(200, Json(hide, lang));
    }

    private static byte[] Json(bool hide, string lang)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteBoolean(HideSourcePanel, hide);
            w.WriteString(LangKey, lang);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>Temp file beside it, then a replace: a crash never leaves half a file.</summary>
    private void Save(bool hide, string lang)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);   // a full path always has a directory
        string tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, Json(hide, lang));
        File.Move(tmp, _path, overwrite: true);
    }
}
