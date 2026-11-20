namespace BluettiWatcher.Modes;

/// <summary>One entry of the broker's policy an action needs: <paramref name="Kind"/> is <c>--broker-allow</c>'s kind.</summary>
internal sealed record BrokerTarget(BrokerTargetKind Kind, string Name);
