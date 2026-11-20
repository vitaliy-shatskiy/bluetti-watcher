using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes;

/// <summary>
/// One kind of action a mode can take (a power plan, a GPU limit, a service): its descriptor and the adapter that reads
/// and writes the machine. The reconciler owns ownership, journaling and restore; a module only reads, compares and
/// writes single resources, so every module gets the same undo rules.
/// </summary>
/// <remarks>Pattern: Adapter (one per capability), registered by hand at start (no reflection: NativeAOT).</remarks>
internal interface IActionModule
{
    /// <summary>What the catalogue shows: type id, settings schema, UI hints, restoration class.</summary>
    ActionModuleDescriptor Descriptor { get; }

    /// <summary>Whether the module can run on this machine now (a missing tool, an absent device); never throws.</summary>
    Task<ModuleAvailability> AvailableAsync(CancellationToken ct);

    /// <summary>
    /// The live lists the settings choose from, keyed by the names in <see cref="ActionModuleDescriptor.DiscoveryLists"/>
    /// (plans, services, monitors, GPUs, apps). A list that cannot be read is absent, never empty-as-a-guess.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>>> DiscoverAsync(CancellationToken ct);

    /// <summary>
    /// Problems with <paramref name="settings"/> beyond the JSON Schema (a value the schema cannot express); empty = valid.
    /// Runs before anything is saved; never touches the machine.
    /// </summary>
    IReadOnlyList<string> Validate(JsonObject settings);

    /// <summary>
    /// The resources the settings want, one per field (<c>windows.activePlan</c>, <c>gpu/0/powerLimit</c>,
    /// <c>service/{name}/running</c>), each with its target value. Pure: no machine access. Keys are unique.
    /// </summary>
    IReadOnlyList<ResourceIntent> Expand(JsonObject settings);

    /// <summary>The current value of one resource; a failure is <see cref="ResourceRead.Failed"/>, never a guessed value.</summary>
    Task<ResourceRead> ReadAsync(string resourceKey, CancellationToken ct);

    /// <summary>
    /// Whether two values of a resource mean the same machine state (a GUID's case, a rounded wattage). Both values are
    /// real reads or targets: the reconciler never calls this with a failed read.
    /// </summary>
    bool Equivalent(string resourceKey, JsonNode? a, JsonNode? b);

    /// <summary>
    /// Writes <paramref name="intent"/>'s target and reads it back. The read-back is what the reconciler records as written
    /// and later compares for ownership. Restoring writes the journaled baseline through this same call; a token module
    /// (pause, hold) encodes the token in the value, and writing the baseline releases it.
    /// </summary>
    Task<ApplyResult> ApplyAsync(ResourceIntent intent, CancellationToken ct);
}
