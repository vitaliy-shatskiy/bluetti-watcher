using System.Globalization;
using System.Text.Json;
using BluettiWatcher.Modes;

namespace BluettiWatcher.Http;

/// <summary>
/// The modes routes (UI-agnostic): GET /api/modes, POST /api/modes, PUT and DELETE /api/modes/{id}, PUT /api/modes/rules,
/// POST /api/modes/select { "mode": "auto" | id }, GET /api/modes/catalogue, GET /api/modes/journal?since=,
/// PUT /api/settings/hibernate { "autoHibernate": bool }. Every write is behind <see cref="RequestGuard.WriteRefusal"/>
/// (the caller checked the Host). A change answers with the whole view, reconciled.
/// </summary>
internal sealed class ModesApi(StationConfig config, ModesEngine engine, TimeSpan? changeWait = null)
{
    /// <summary>
    /// The longest a write waits for its transition: well inside the server's request limit, so a slow action answers 202
    /// (switching) instead of a dropped connection. The transition itself runs on to its end.
    /// </summary>
    internal static readonly TimeSpan ChangeWait = MiniHttpServer.RequestLimit / 2;

    private readonly TimeSpan _changeWait = changeWait ?? ChangeWait;

    /// <summary>The longest one module's availability and discovery may take for the catalogue.</summary>
    internal static readonly TimeSpan DiscoverWait = TimeSpan.FromSeconds(3);

    private readonly Lock _catalogueGate = new();
    private Task<HttpResponse>? _catalogue;          // the last finished build, or the first one while it runs
    private Task<HttpResponse>? _catalogueRefresh;   // the one rebuild behind the answers, while it runs

    public static bool Owns(string path) =>
        path == "/api/modes" || path.StartsWith("/api/modes/", StringComparison.Ordinal) || path == "/api/settings/hibernate";

    public async Task<HttpResponse> HandleAsync(HttpRequest r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.Method != "GET" && RequestGuard.WriteRefusal(r, config.Port) is { } why) return HttpResponse.Text(403, "forbidden: " + why);
        try
        {
            return (r.Method, r.Path) switch
            {
                ("GET", "/api/modes") => View(200),
                ("GET", "/api/modes/catalogue") => await CatalogueAsync(ct),
                ("GET", "/api/modes/journal") => Journal(r),
                ("POST", "/api/modes") => await SaveAsync(Body(r, ModesJson.Default.Mode), create: true, ct),
                ("PUT", "/api/modes/rules") => await AnswerAsync(engine.PutRulesAsync(Body(r, ModesJson.Default.ListRule), ct), ct),
                ("POST", "/api/modes/select") => await AnswerAsync(engine.SelectAsync(Field(r, "mode"), ct), ct),
                ("PUT", "/api/settings/hibernate") => await AnswerAsync(engine.SetAutoHibernateAsync(Flag(r, "autoHibernate"), ct), ct),
                ("PUT", var p) when ModeId(p) is { } id => await PutAsync(r, id, ct),
                ("DELETE", var p) when ModeId(p) is { } id => await AnswerAsync(engine.DeleteModeAsync(id, ct), ct),
                (_, "/api/modes/catalogue" or "/api/modes/journal") => HttpResponse.Text(405, "GET only"),
                _ => HttpResponse.Text(404, "not found"),
            };
        }
        catch (JsonException e) { return HttpResponse.Text(400, $"body: {e.Message}"); }
    }

    /// <summary>The page's old POST /api/mode { auto | eco | max } on the v2 engine: a select of the built-in mode.</summary>
    public async Task<HttpResponse> LegacyModeAsync(HttpRequest r, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        string mode;
        try { mode = Field(r, "mode"); }
        catch (JsonException) { return HttpResponse.Text(400, "body must be JSON { \"mode\": \"auto\" | \"eco\" | \"max\" }"); }
        if (mode is not ("auto" or BuiltInModes.Eco or BuiltInModes.Max)) return HttpResponse.Text(400, "mode must be auto, eco or max");
        return await AnswerAsync(engine.SelectAsync(mode, ct), ct);
    }

    private async Task<HttpResponse> PutAsync(HttpRequest r, string id, CancellationToken ct)
    {
        var mode = Body(r, ModesJson.Default.Mode);
        if (!string.Equals(mode.Id, id, StringComparison.Ordinal)) return HttpResponse.Text(400, $"body id {mode.Id} is not the path's {id}");
        return await SaveAsync(mode, create: false, ct);
    }

    /// <summary>The id of /api/modes/{id}; null for the fixed sub-routes or an id that cannot be one.</summary>
    private static string? ModeId(string path)
    {
        const string prefix = "/api/modes/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return null;
        string id = path[prefix.Length..];
        return ModesValidation.ReservedIds.Contains(id) || !ModesValidation.ValidId(id) ? null : id;
    }

    /// <summary>
    /// Waits for a change at most <see cref="ChangeWait"/>: past it the answer is 202 with the view as it stands (pending,
    /// switching) and the change runs on in the engine; the page follows it with GET /api/modes.
    /// </summary>
    private async Task<HttpResponse> AnswerAsync(Task<(int Status, string Error)> change, CancellationToken ct, string? savedMode = null)
    {
        (int Status, string Error) res;
        try { res = await change.WaitAsync(_changeWait, ct); }
        catch (TimeoutException) { return View(202, savedMode); }
        return res.Status == 0 ? View(200, savedMode) : HttpResponse.Text(res.Status, res.Error);
    }

    /// <summary>A mode saved answers with <c>warnings</c>: its entries the broker policy does not allow, each with the elevated command.</summary>
    private Task<HttpResponse> SaveAsync(Mode mode, bool create, CancellationToken ct) =>
        AnswerAsync(engine.PutModeAsync(mode, create, ct), ct, mode.Id);

    private HttpResponse View(int status, string? warnFor = null)
    {
        var s = engine.Settings;
        var snap = engine.Snapshot;
        return HttpResponse.Json(status, Write(w =>
        {
            w.WriteStartObject();
            w.WriteString("engine", ModesConfig.V2);
            w.WriteString("activeModeId", snap.ModeId);
            w.WriteString("reason", engine.Reason);
            w.WriteString("switching", engine.Switching);
            w.WriteBoolean("pending", engine.Busy);
            w.WriteBoolean("emergency", engine.Emergency);
            w.WriteStartArray("actions");
            foreach (var a in snap.Actions) JsonSerializer.Serialize(w, a, ModesJson.Default.ActionReport);
            w.WriteEndArray();
            w.WriteStartArray("restores");
            foreach (var x in snap.Restores) JsonSerializer.Serialize(w, x, ModesJson.Default.ResourceStatus);
            w.WriteEndArray();
            w.WriteStartArray("problems");
            foreach (var p in engine.Problems) w.WriteStringValue(p);
            w.WriteEndArray();
            if (warnFor is not null)
            {
                w.WriteStartArray("warnings");
                foreach (var x in engine.PolicyWarnings(warnFor))
                {
                    w.WriteStartObject();
                    w.WriteNumber("action", x.Action);
                    w.WriteString("kind", x.KindName);
                    w.WriteString("name", x.Name);
                    w.WriteString("command", x.Command);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WritePropertyName("settings");
            JsonSerializer.Serialize(w, s, ModesJson.Default.ModesSettings);
            w.WriteEndObject();
        }));
    }

    /// <summary>Builds the catalogue once at start, so the page's first ask finds it ready.</summary>
    public void WarmCatalogue()
    {
        lock (_catalogueGate) _catalogue ??= BuildCatalogueAsync();
    }

    // Discovery asks the machine (plans, GPUs, services, tasks, monitors, OpenRGB) one module after another: seconds of
    // work. An ask gets the last build at once and one rebuild runs behind it, so the next ask sees the machine as it is now.
    private Task<HttpResponse> CatalogueAsync(CancellationToken ct)
    {
        Task<HttpResponse> answer;
        lock (_catalogueGate)
        {
            if (_catalogue is null || _catalogue.IsFaulted || _catalogue.IsCanceled) _catalogue = BuildCatalogueAsync();
            answer = _catalogue;
            if (answer.IsCompletedSuccessfully && _catalogueRefresh is not { IsCompleted: false }) _catalogueRefresh = RefreshCatalogueAsync();
        }
        return answer.WaitAsync(ct);
    }

    private async Task<HttpResponse> RefreshCatalogueAsync()
    {
        var fresh = await BuildCatalogueAsync();
        lock (_catalogueGate) _catalogue = Task.FromResult(fresh);
        return fresh;
    }

    // Shared by every ask that waits on it, so no request's token ends it; each module is bounded by DiscoverWait.
    private async Task<HttpResponse> BuildCatalogueAsync()
    {
        var entries = new List<(IActionModule Module, ModuleAvailability Available, IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>> Lists)>();
        foreach (var m in engine.Registry.All)
        {
            using var bounded = new CancellationTokenSource(DiscoverWait);
            ModuleAvailability available;
            IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>> lists = new Dictionary<string, IReadOnlyList<DiscoveredItem>>();
            try
            {
                available = await m.AvailableAsync(bounded.Token);
                if (available.Available) lists = await m.DiscoverAsync(bounded.Token);
            }
            catch (OperationCanceledException) when (bounded.IsCancellationRequested) { available = ModuleAvailability.No("no answer in time"); }
            catch (Exception e) when (e is not OperationCanceledException) { available = ModuleAvailability.No(e.Message); }
            entries.Add((m, available, lists));
        }
        return HttpResponse.Json(200, Write(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("modules");
            foreach (var (m, available, lists) in entries)
            {
                w.WriteStartObject();
                w.WritePropertyName("descriptor");
                JsonSerializer.Serialize(w, m.Descriptor, ModesJson.Default.ActionModuleDescriptor);
                w.WriteBoolean("available", available.Available);
                w.WriteString("unavailableReason", available.Reason);
                w.WriteStartObject("discovery");
                foreach (var (name, items) in lists)
                {
                    w.WriteStartArray(name);
                    foreach (var item in items) JsonSerializer.Serialize(w, item, ModesJson.Default.DiscoveredItem);
                    w.WriteEndArray();
                }
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }));
    }

    private HttpResponse Journal(HttpRequest r)
    {
        long since = 0;
        if (r.Query.TryGetValue("since", out var text) && !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out since))
            return HttpResponse.Text(400, "since must be a sequence number");
        var records = engine.Journal(since);
        return HttpResponse.Json(200, Write(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("pageSize", ModeJournal.PageSize);
            w.WriteStartArray("records");
            foreach (var rec in records) JsonSerializer.Serialize(w, rec, ModesJson.Default.JournalRecord);
            w.WriteEndArray();
            w.WriteEndObject();
        }));
    }

    private static T Body<T>(HttpRequest r, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        JsonSerializer.Deserialize(r.Body, type) ?? throw new JsonException("body must not be null");

    private static string Field(HttpRequest r, string name)
    {
        using var doc = JsonDocument.Parse(r.Body);
        return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()! : throw new JsonException($"body must be JSON {{ \"{name}\": \"...\" }}");
    }

    private static bool Flag(HttpRequest r, string name)
    {
        using var doc = JsonDocument.Parse(r.Body);
        return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : throw new JsonException($"body must be JSON {{ \"{name}\": true | false }}");
    }

    private static byte[] Write(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms)) body(w);
        return ms.ToArray();
    }
}
