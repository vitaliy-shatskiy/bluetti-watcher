using System.Collections.Frozen;
using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes;

/// <summary>
/// The action modules this build knows, registered explicitly at start (NativeAOT: no assembly scanning). A duplicate type
/// id or a module whose default settings fail its own schema is a programming error and stops the start.
/// </summary>
internal sealed class ModuleRegistry
{
    private readonly FrozenDictionary<string, IActionModule> _byType;

    public ModuleRegistry(IEnumerable<IActionModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);
        var list = modules.ToList();
        var byType = new Dictionary<string, IActionModule>(StringComparer.Ordinal);
        foreach (var m in list)
        {
            var d = m.Descriptor;
            if (!byType.TryAdd(d.TypeId, m)) throw new ArgumentException($"module type {d.TypeId} registered twice", nameof(modules));
            var problems = Validate(m, d.DefaultSettings);
            if (problems.Count > 0) throw new ArgumentException($"module {d.TypeId}: default settings invalid: {string.Join("; ", problems)}", nameof(modules));
        }
        _byType = byType.ToFrozenDictionary(StringComparer.Ordinal);
        All = list;
    }

    /// <summary>In registration order (the catalogue's order).</summary>
    public IReadOnlyList<IActionModule> All { get; }

    public bool TryGet(string typeId, out IActionModule module) => _byType.TryGetValue(typeId, out module!);

    /// <summary>Schema problems, then the module's own; empty = valid. An unknown type id is a problem.</summary>
    public IReadOnlyList<string> Validate(string typeId, JsonObject settings) =>
        TryGet(typeId, out var m) ? Validate(m, settings) : [$"unknown action type {typeId}"];

    private static List<string> Validate(IActionModule m, JsonObject settings)
    {
        var problems = SettingsSchema.Check(m.Descriptor.SettingsSchema, settings).ToList();
        if (problems.Count == 0) problems.AddRange(m.Validate(settings));
        return problems;
    }
}
