using System.Text.Json.Serialization;

namespace BluettiWatcher.Modes;

/// <summary>Where one action of the active mode stands; the API reports it per action.</summary>
internal enum ActionStatus
{
    /// <summary>Not reconciled yet, or its last read failed.</summary>
    [JsonStringEnumMemberName("unknown")] Unknown,
    /// <summary>The machine already held the targets: nothing written, nothing to undo.</summary>
    [JsonStringEnumMemberName("satisfied")] Satisfied,
    /// <summary>Written and read back; owned until left or changed by the person.</summary>
    [JsonStringEnumMemberName("applied")] Applied,
    /// <summary>A read or write failed; tried again on the next poll.</summary>
    [JsonStringEnumMemberName("failed")] Failed,
    /// <summary>The module is not available here, or not registered.</summary>
    [JsonStringEnumMemberName("unsupported")] Unsupported,
    /// <summary>The person changed a resource the mode wrote: the mode let go and will not restore it.</summary>
    [JsonStringEnumMemberName("relinquished")] Relinquished,
    /// <summary>Restoring the baseline failed (bounded retries spent).</summary>
    [JsonStringEnumMemberName("restore-failed")] RestoreFailed,
}
