namespace BluettiWatcher.Modes;

/// <summary>
/// One entry of a discovery list: the value a setting stores (<paramref name="Id"/>, a plan GUID), its label, and the
/// numbers a page words itself (<paramref name="Numbers"/>, a GPU's <c>min</c>, <c>max</c> and <c>default</c> watts;
/// null when the item has none).
/// </summary>
internal sealed record DiscoveredItem(string Id, string Label, IReadOnlyDictionary<string, double>? Numbers = null);
