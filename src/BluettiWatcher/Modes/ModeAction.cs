using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes;

/// <summary>One action of a mode: a module by its type id and the settings it runs with.</summary>
internal sealed record ModeAction
{
    /// <summary>The module's <see cref="ActionModuleDescriptor.TypeId"/>.</summary>
    public required string TypeId { get; init; }
    public bool Enabled { get; init; } = true;
    /// <summary>While the mode is active a resource the person changed is re-applied instead of let go.</summary>
    public bool Enforce { get; init; }
    /// <summary>Validated against the module's schema and <see cref="IActionModule.Validate"/> before it is saved.</summary>
    public JsonObject Settings { get; init => field = value ?? []; } = [];
}
