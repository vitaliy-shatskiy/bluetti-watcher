using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes;

/// <summary>One field of the machine a mode wants: its key (<c>gpu/0/powerLimit</c>) and the target value.</summary>
internal sealed record ResourceIntent(string Key, JsonNode? Target);
