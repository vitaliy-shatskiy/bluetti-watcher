using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes;

/// <summary>
/// A module whose writes go through the admin broker names the targets an action's settings need in the broker's policy,
/// so a mode can say before and on entry which of them the policy does not allow.
/// </summary>
internal interface IBrokerTargets
{
    /// <summary>The policy entries <paramref name="settings"/> needs; a malformed entry is left out (validation says it).</summary>
    IReadOnlyList<BrokerTarget> BrokerTargets(JsonObject settings);
}
