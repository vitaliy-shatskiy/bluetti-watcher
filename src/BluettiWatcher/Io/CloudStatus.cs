namespace BluettiWatcher.Io;

/// <summary>
/// The cloud's state (design.md §4 Cloud state): <see cref="State"/> off | signed-out | ok | sign-in-needed | error, the
/// reason in English, and when it was seen (null = not from a call). Never carries a token.
/// </summary>
internal sealed record CloudStatus(string State, string Detail, DateTimeOffset? At);
