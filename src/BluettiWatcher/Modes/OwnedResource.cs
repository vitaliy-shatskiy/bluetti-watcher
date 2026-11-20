using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes;

/// <summary>
/// A resource the modes wrote and still answer for. <see cref="Applied"/> false = a write was prepared and its outcome is
/// not known yet (a crash or a failed write between Prepared and Applied): resolved by reading before anything else.
/// </summary>
internal sealed record OwnedResource
{
    public required string Key { get; init; }
    public required string TypeId { get; init; }
    public required string ModeId { get; init; }
    /// <summary>The value before the first write of this ownership; what leaving restores.</summary>
    public JsonNode? Before { get; init; }
    public JsonNode? Target { get; init; }
    /// <summary>The value read back after the write; the resource is ours only while it still reads this.</summary>
    public JsonNode? Written { get; init; }
    public bool Applied { get; init; }
    /// <summary>The value an earlier write of this ownership left, when a new target was being written over it.</summary>
    public JsonNode? PriorWritten { get; init; }
    public bool HadPrior { get; init; }
    public int RestoreTries { get; init; }
    public DateTimeOffset At { get; init; }
}
