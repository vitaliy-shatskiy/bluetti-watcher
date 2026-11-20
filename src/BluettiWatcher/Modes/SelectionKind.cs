using System.Text.Json.Serialization;

namespace BluettiWatcher.Modes;

/// <summary>Who chooses the mode.</summary>
internal enum SelectionKind
{
    /// <summary>The rules, first match.</summary>
    [JsonStringEnumMemberName("auto")] Auto,
    /// <summary>The person's pick, held until Auto is pressed.</summary>
    [JsonStringEnumMemberName("manual")] Manual,
}
