namespace BluettiWatcher.Modes.Broker;

/// <summary>The Task Scheduler, for one task by its full name; answers <c>enabled</c> or <c>disabled</c> as read after the change.</summary>
internal interface ITaskControl
{
    Task<BrokerResult> SetEnabledAsync(string name, bool enabled, CancellationToken ct);
}
