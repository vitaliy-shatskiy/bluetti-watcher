using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes;

/// <summary>
/// A write's outcome: on success the value read back after it (what the reconciler records as written); on failure the
/// error. A write that went through but whose read-back failed is a failure: nothing is owned on a guess.
/// </summary>
internal sealed record ApplyResult
{
    private ApplyResult(bool ok, JsonNode? readBack, string error)
    {
        Ok = ok;
        ReadBack = readBack;
        Error = error;
    }

    public bool Ok { get; }
    public JsonNode? ReadBack { get; }
    public string Error { get; }

    public static ApplyResult Written(JsonNode? readBack) => new(true, readBack, "");

    public static ApplyResult Failed(string error) => new(false, null, string.IsNullOrEmpty(error) ? "write failed" : error);
}
