namespace BluettiWatcher.Modes;

/// <summary>One entry of a discovery list: the value a setting stores (<paramref name="Id"/>, a plan GUID) and its label.</summary>
internal sealed record DiscoveredItem(string Id, string Label);
