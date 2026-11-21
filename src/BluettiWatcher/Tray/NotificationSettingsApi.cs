using System.Text.Json;
using BluettiWatcher.Http;

namespace BluettiWatcher.Tray;

/// <summary>
/// GET /api/settings/notifications: <c>enabled</c> (all on or off) and the groups, whether each is on. PUT with a JSON
/// object of booleans (any subset of those names) switches them, behind <see cref="RequestGuard.WriteRefusal"/>, and answers the whole set. POST
/// /api/settings/notifications/test, behind the same guard, shows one notification whatever the switches say.
/// </summary>
internal static class NotificationSettingsApi
{
    public const string Route = "/api/settings/notifications";
    public const string TestRoute = Route + "/test";

    /// <summary><paramref name="send"/> null = this service shows no tray icon.</summary>
    public static HttpResponse HandleTest(HttpRequest r, Action? send, int port)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.Method != "POST") return HttpResponse.Text(405, "POST only");
        if (RequestGuard.WriteRefusal(r, port) is { } why) return HttpResponse.Text(403, "forbidden: " + why);
        if (send is null) return HttpResponse.Text(503, "no tray icon: notifications show only while the service runs in the signed-in session with tray on");
        send();
        return HttpResponse.Json(200, """{"sent":true}"""u8.ToArray());
    }

    public static HttpResponse Handle(HttpRequest r, NotificationSettingsStore store, int port)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(store);
        if (r.Method == "GET") return HttpResponse.Json(200, NotificationSettingsStore.ToJson(store.Current));
        if (r.Method != "PUT") return HttpResponse.Text(405, "GET or PUT only");
        if (RequestGuard.WriteRefusal(r, port) is { } why) return HttpResponse.Text(403, "forbidden: " + why);
        string? error;
        try
        {
            using var doc = JsonDocument.Parse(r.Body);
            error = store.Update(doc.RootElement);
        }
        catch (JsonException) { error = "the body must be a JSON object of booleans"; }
        return error is null ? HttpResponse.Json(200, NotificationSettingsStore.ToJson(store.Current)) : HttpResponse.Text(400, error);
    }
}
