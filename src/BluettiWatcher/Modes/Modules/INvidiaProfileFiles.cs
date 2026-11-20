namespace BluettiWatcher.Modes.Modules;

/// <summary>What the NVIDIA profile module may look at without the broker: the inspector's presence and the policy folder.</summary>
internal interface INvidiaProfileFiles
{
    /// <summary>Why NVIDIA Profile Inspector cannot be used (not configured, file missing); null = it is there.</summary>
    string? InspectorMissing();

    /// <summary>The <c>.nip</c> file names in the policy folder; null when the folder cannot be read.</summary>
    IReadOnlyList<string>? NipFiles();
}
