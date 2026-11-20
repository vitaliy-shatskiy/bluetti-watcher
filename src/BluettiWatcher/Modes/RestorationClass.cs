using System.Text.Json.Serialization;

namespace BluettiWatcher.Modes;

/// <summary>How leaving a mode undoes a module's writes, and what a restart after a reboot does with them.</summary>
internal enum RestorationClass
{
    /// <summary>The baseline is snapshotted and restored only while the resource still holds what the mode wrote.</summary>
    [JsonStringEnumMemberName("reversible")] Reversible,
    /// <summary>A pause or hold whose token is released on leaving; runtime state, not resurrected after a reboot.</summary>
    [JsonStringEnumMemberName("token")] Token,
    /// <summary>Undone approximately (documented per module).</summary>
    [JsonStringEnumMemberName("compensatable")] Compensatable,
    /// <summary>Never undone (closing an app).</summary>
    [JsonStringEnumMemberName("irreversible")] Irreversible,
}
