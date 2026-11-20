using System.Text.Json.Serialization;

namespace BluettiWatcher.Modes;

/// <summary>The power-mode flag word a mode maps to; the flag's readers (the Claude Code hook) know only these.</summary>
internal enum FlagWord
{
    [JsonStringEnumMemberName("normal")] Normal,
    [JsonStringEnumMemberName("eco")] Eco,
    [JsonStringEnumMemberName("emergency")] Emergency,
}
