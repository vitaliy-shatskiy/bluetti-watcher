namespace BluettiWatcher.Modes.Broker;

/// <summary>The admin broker against this service: <paramref name="State"/> missing, stale or current; its version; what to do (null = nothing).</summary>
internal sealed record BrokerStatus(string State, string? Version, string? Problem);
