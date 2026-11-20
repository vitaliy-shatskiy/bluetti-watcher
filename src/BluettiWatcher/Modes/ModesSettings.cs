namespace BluettiWatcher.Modes;

/// <summary>
/// Everything the modes feature stores (<c>modes.json</c> in the data directory). The file is storage, not the interface:
/// every value is changed through the API.
/// </summary>
internal sealed record ModesSettings
{
    /// <summary>The shape this build writes; a newer file is refused (never half-read), an older one migrated.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public IReadOnlyList<Mode> Modes { get; init => field = value ?? []; } = [];
    /// <summary>Auto's rules in order; the first match wins.</summary>
    public IReadOnlyList<Rule> Rules { get; init => field = value ?? []; } = [];
    /// <summary>The mode when nothing has chosen one yet, and the safe state after an unreadable journal.</summary>
    public string FallbackModeId { get; init => field = value ?? "normal"; } = "normal";
    public Selection Selection { get; init => field = value ?? Selection.Auto; } = Selection.Auto;
    /// <summary>Hibernate at the battery's hibernate level; false = never (as the <c>no-hibernate</c> file).</summary>
    public bool AutoHibernate { get; init; } = true;
}
