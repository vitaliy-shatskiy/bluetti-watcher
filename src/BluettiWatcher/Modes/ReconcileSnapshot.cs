namespace BluettiWatcher.Modes;

/// <summary>What the reconciler last did, for the API: the active mode, its actions' statuses and the open restores.</summary>
internal sealed record ReconcileSnapshot(string? ModeId, IReadOnlyList<ActionReport> Actions, IReadOnlyList<ResourceStatus> Restores)
{
    public static readonly ReconcileSnapshot Empty = new(null, [], []);
}
