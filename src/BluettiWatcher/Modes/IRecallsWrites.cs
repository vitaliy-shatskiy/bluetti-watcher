using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes;

/// <summary>
/// A module whose read is its own runtime memory of what it ran (it cannot read the machine). At start the reconciler
/// hands it the journaled target of every resource it still owns, so a write made before a restart reads as written and
/// leaving the mode still undoes it: the journal is the truth, not the module's memory.
/// </summary>
internal interface IRecallsWrites
{
    void Recall(string resourceKey, JsonNode? target);
}
