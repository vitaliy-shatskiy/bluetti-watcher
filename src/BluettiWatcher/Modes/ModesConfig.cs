namespace BluettiWatcher.Modes;

/// <summary>Config section <c>modes</c>.</summary>
internal sealed class ModesConfig
{
    public const string Legacy = "legacy";
    public const string V2 = "v2";

    /// <summary>
    /// <c>v2</c> (default): user-defined modes, Auto rules and the ownership-aware reconciler (docs/design.md, Power modes) ·
    /// <c>legacy</c>: the switch's fixed eco and the page's Auto / Eco / Max. Any other value = legacy, named in the log.
    /// </summary>
    public string Engine { get; set; } = V2;

    /// <summary>The named commands a mode's <c>command.pair</c> action may run; only this file defines them.</summary>
    public Dictionary<string, ModeCommandConfig> Commands { get; set => field = value ?? []; } = [];

    /// <summary>
    /// OpenRGB.exe for the <c>rgb.lighting</c> action; empty = the install folders. Only this file sets it: a mode, the
    /// page and the API never name an executable.
    /// </summary>
    public string OpenRgbPath { get; set => field = value ?? ""; } = "";

    public bool V2On => string.Equals(Engine, V2, StringComparison.OrdinalIgnoreCase);
}
