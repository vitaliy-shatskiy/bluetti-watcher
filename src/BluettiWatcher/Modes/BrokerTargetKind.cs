namespace BluettiWatcher.Modes;

/// <summary>The policy lists an action can need an entry in; the lower-case name is <c>--broker-allow</c>'s kind.</summary>
internal enum BrokerTargetKind
{
    Service,
    Task,
    Profile,
}
