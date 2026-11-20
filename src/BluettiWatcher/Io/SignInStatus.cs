namespace BluettiWatcher.Io;

/// <summary>The page's cloud sign-in: <see cref="State"/> idle | waiting | signed-in | failed, its last message (English, never a token), when it changed.</summary>
internal sealed record SignInStatus(string State, string Message, DateTimeOffset? At);
