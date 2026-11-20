namespace BluettiWatcher.Decision;

/// <summary>One event row the alarms step asks for: its kind (also the config's <c>actions</c> key) and its text.</summary>
internal sealed record AlarmEvent(string Kind, string Detail);
