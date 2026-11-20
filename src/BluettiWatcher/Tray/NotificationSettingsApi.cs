using System.Text.Json;
using BluettiWatcher.Http;

namespace BluettiWatcher.Tray;

/// <summary>
/// GET /api/settings/notifications: the groups and whether each is on. PUT with a JSON object of booleans (any subset of
/// the groups) switches them, behind <see cref="RequestGuard.WriteRefusal"/>, and answers the whole set.
/// </summary>
internal static class NotificationSettingsApi
{
    public const string Route = "/api/settings/notifications";

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
