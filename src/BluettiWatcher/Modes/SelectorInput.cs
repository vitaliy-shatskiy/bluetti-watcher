namespace BluettiWatcher.Modes;

/// <summary>
/// What the Auto rules see at one poll. A null field is unknown: a condition on it does not match (never a guess).
/// </summary>
/// <param name="MainsOn">Mains confirmed on (true) or off (false).</param>
/// <param name="MainsFor">How long mains has been in that state.</param>
/// <param name="Soc">The last accepted battery percent.</param>
/// <param name="MinutesToCut">Minutes until the next scheduled outage starts (0 while one is on).</param>
/// <param name="LocalNow">The PC's local time, for time windows.</param>
internal sealed record SelectorInput(bool? MainsOn, TimeSpan? MainsFor, double? Soc, double? MinutesToCut, DateTimeOffset LocalNow);
