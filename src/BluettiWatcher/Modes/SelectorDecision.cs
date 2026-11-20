namespace BluettiWatcher.Modes;

/// <summary>The selector's answer: the mode to be active, the selection to save, and why (for the API and the log).</summary>
internal sealed record SelectorDecision(string ModeId, Selection Selection, string Reason);
