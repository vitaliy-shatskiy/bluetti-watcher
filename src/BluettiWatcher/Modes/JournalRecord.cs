using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes;

/// <summary>One line of the mode journal (<c>modes-journal.jsonl</c>).</summary>
internal sealed record JournalRecord
{
    /// <summary>Monotonic within the file; <c>/api/modes/journal?since=</c> pages by it.</summary>
    public long Seq { get; init; }
    public DateTimeOffset At { get; init; }
    public JournalKind Kind { get; init; }
    public string ModeId { get; init => field = value ?? ""; } = "";
    public string TypeId { get; init => field = value ?? ""; } = "";
    public string Key { get; init => field = value ?? ""; } = "";
    /// <summary>Prepared: the baseline restored on leaving.</summary>
    public JsonNode? Before { get; init; }
    /// <summary>Prepared: the target · Applied: the value read back after the write.</summary>
    public JsonNode? Value { get; init; }
    /// <summary>RestoreFailed: the retries are spent.</summary>
    public bool Final { get; init; }
    public string Detail { get; init => field = value ?? ""; } = "";
}
