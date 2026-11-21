using System.Text.Json;

namespace BluettiWatcher.Tray;

/// <summary>
/// <c>notifications.json</c> in the data directory: read at start, written (temp file moved over it) on each change. A
/// missing file is all groups on; an unreadable one is logged and also all on, and is rewritten only by a change.
/// </summary>
internal sealed class NotificationSettingsStore
{
    private readonly string _path;
    private readonly Action<string> _log;
    private readonly Lock _gate = new();
    private NotificationSettings _current;

    public NotificationSettingsStore(string path, Action<string> log)
    {
        _path = path;
        _log = log;
        _current = Load();
    }

    public NotificationSettings Current
    {
        get { lock (_gate) return _current; }
    }

    private NotificationSettings Load()
    {
        if (!File.Exists(_path)) return new NotificationSettings();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(_path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
            if (TryApply(new NotificationSettings(), doc.RootElement, out var loaded, out string error)) return loaded;
            _log($"notifications: {_path} ignored ({error}); every notification is on");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            _log($"notifications: {_path} unreadable ({e.GetType().Name}: {e.Message}); every notification is on");
        }
        return new NotificationSettings();
    }

    /// <summary>
    /// Applies the groups named in <paramref name="body"/> (each a boolean; the rest keep their value) and saves.
    /// All or nothing: an unknown name or a non-boolean value changes nothing. Returns null on success, else why not.
    /// </summary>
    public string? Update(JsonElement body)
    {
        lock (_gate)
        {
            if (!TryApply(_current, body, out var next, out string error)) return error;
            Save(next);
            _current = next;
            return null;
        }
    }

    internal static bool TryApply(NotificationSettings start, JsonElement body, out NotificationSettings result, out string error)
    {
        result = start;
        error = "";
        if (body.ValueKind != JsonValueKind.Object) { error = "the body must be a JSON object of booleans"; return false; }
        var next = start;
        foreach (var p in body.EnumerateObject())
        {
            if (string.Equals(p.Name, NotificationSettings.EnabledKey, StringComparison.Ordinal))
            {
                if (p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { error = $"\"{p.Name}\" must be true or false"; return false; }
                next = next with { Enabled = p.Value.GetBoolean() };
                continue;
            }
            var key = NotificationSettings.Keys.FirstOrDefault(k => string.Equals(k.Name, p.Name, StringComparison.Ordinal));
            if (key.Name is null) { error = $"unknown notification \"{p.Name}\" (known: {string.Join(", ", NotificationSettings.Keys.Select(k => k.Name))})"; return false; }
            if (p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { error = $"\"{p.Name}\" must be true or false"; return false; }
            next = next.With(key.Kind, p.Value.GetBoolean());
        }
        result = next;
        return true;
    }

    public static byte[] ToJson(NotificationSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteBoolean(NotificationSettings.EnabledKey, s.Enabled);
            foreach (var (name, kind) in NotificationSettings.Keys) w.WriteBoolean(name, s.IsOn(kind));
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    private void Save(NotificationSettings s)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);   // a full path: the data directory's file
        string tmp = _path + ".tmp";
        File.WriteAllBytes(tmp, ToJson(s));
        File.Move(tmp, _path, overwrite: true);
    }
}
