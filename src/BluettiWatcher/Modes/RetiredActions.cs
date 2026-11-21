using System.Collections.Frozen;

namespace BluettiWatcher.Modes;

/// <summary>
/// Action types an earlier build had and this one removed. A saved mode that lists one loads without it, and a resource one
/// held in the journal is let go: there is no module left to restore it with.
/// </summary>
internal static class RetiredActions
{
    /// <summary><c>gpu.idleWatcher</c>: it only logged what kept an idle GPU busy, and its polling cost power.</summary>
    public static readonly FrozenSet<string> TypeIds = FrozenSet.Create(StringComparer.Ordinal, "gpu.idleWatcher");

    /// <summary>The settings without retired actions, and how many were taken out.</summary>
    public static (ModesSettings Settings, int Dropped) Drop(ModesSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        int dropped = settings.Modes.Sum(m => m.Actions.Count(a => TypeIds.Contains(a.TypeId)));
        if (dropped == 0) return (settings, 0);
        return (settings with { Modes = [.. settings.Modes.Select(m => m with { Actions = [.. m.Actions.Where(a => !TypeIds.Contains(a.TypeId))] })] }, dropped);
    }
}
