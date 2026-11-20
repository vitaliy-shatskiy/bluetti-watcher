namespace BluettiWatcher.Modes;

/// <summary>The journal as read at start: its records, or why it could not be trusted (then the records are empty).</summary>
internal sealed record JournalLoad(IReadOnlyList<JournalRecord> Records, string? Problem);
