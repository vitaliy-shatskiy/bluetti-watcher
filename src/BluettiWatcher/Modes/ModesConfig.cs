namespace BluettiWatcher.Modes;

/// <summary>Config section <c>modes</c>.</summary>
internal sealed class ModesConfig
{
    public const string Legacy = "legacy";
    public const string V2 = "v2";

    /// <summary>
    /// <c>legacy</c> (default): the switch's fixed eco and the page's Auto / Eco / Max · <c>v2</c>: user-defined modes, Auto
    /// rules and the ownership-aware reconciler (docs/design.md, Power modes). Any other value = legacy, named in the log.
    /// </summary>
    public string Engine { get; set; } = Legacy;

    public bool V2On => string.Equals(Engine, V2, StringComparison.OrdinalIgnoreCase);
}
