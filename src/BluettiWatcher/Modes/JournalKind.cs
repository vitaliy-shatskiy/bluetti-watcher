using System.Text.Json.Serialization;

namespace BluettiWatcher.Modes;

/// <summary>What a journal line records. Replayed at start: Prepared opens a write, the others close or extend it.</summary>
internal enum JournalKind
{
    /// <summary>A mode became active (history only).</summary>
    [JsonStringEnumMemberName("mode")] ModeEntered,
    /// <summary>About to write: the baseline (<see cref="JournalRecord.Before"/>) and the target. Open until closed.</summary>
    [JsonStringEnumMemberName("prepared")] Prepared,
    /// <summary>Written and read back (<see cref="JournalRecord.Value"/>): owned from here.</summary>
    [JsonStringEnumMemberName("applied")] Applied,
    /// <summary>The person changed it (or a reboot reset runtime state): never restored.</summary>
    [JsonStringEnumMemberName("relinquished")] Relinquished,
    /// <summary>The baseline is back.</summary>
    [JsonStringEnumMemberName("restored")] Restored,
    /// <summary>A restore try failed; <see cref="JournalRecord.Final"/> = the retries are spent and the resource is let go.</summary>
    [JsonStringEnumMemberName("restore-failed")] RestoreFailed,
    /// <summary>An open write found not to have changed anything, or an irreversible one closed: nothing owned.</summary>
    [JsonStringEnumMemberName("dropped")] Dropped,
}
