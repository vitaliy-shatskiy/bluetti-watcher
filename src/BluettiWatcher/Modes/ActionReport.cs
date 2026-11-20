namespace BluettiWatcher.Modes;

/// <summary>The status of one action of the active mode (<paramref name="Index"/> into its actions).</summary>
internal sealed record ActionReport(int Index, string TypeId, ActionStatus Status, string Detail);
