using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes;

/// <summary>
/// A resource's current value, or why it could not be read. A failed read carries no value: the reconciler never
/// compares it, never skips a write on it and never claims ownership from it.
/// </summary>
internal sealed record ResourceRead
{
    private ResourceRead(bool ok, JsonNode? value, string error)
    {
        Ok = ok;
        Value = value;
        Error = error;
    }

    public bool Ok { get; }
    /// <summary>The value read; null is a real value (absent, not set) only when <see cref="Ok"/>.</summary>
    public JsonNode? Value { get; }
    public string Error { get; }

    public static ResourceRead Of(JsonNode? value) => new(true, value, "");

    public static ResourceRead Failed(string error) => new(false, null, string.IsNullOrEmpty(error) ? "read failed" : error);
}
