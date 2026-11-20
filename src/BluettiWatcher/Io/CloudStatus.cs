namespace BluettiWatcher.Io;

/// <summary>
/// The cloud's state (design.md §4 Cloud state): <see cref="State"/> off | signed-out | ok | sign-in-needed | error, the
/// reason in English (logs and the API; the page words the state itself), when it was seen (null = not from a call) and when the
/// access token runs out (null = no readable token). Never carries a token.
/// </summary>
internal sealed record CloudStatus(string State, string Detail, DateTimeOffset? At, DateTimeOffset? Until = null);
