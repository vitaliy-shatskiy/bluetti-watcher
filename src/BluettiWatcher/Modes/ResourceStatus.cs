namespace BluettiWatcher.Modes;

/// <summary>A resource left by an earlier mode that is not back to its baseline: being retried, let go, or given up.</summary>
internal sealed record ResourceStatus(string Key, string TypeId, ActionStatus Status, string Detail);
