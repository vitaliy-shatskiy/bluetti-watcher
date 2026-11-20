namespace BluettiWatcher.Modes.Broker;

/// <summary>The Service Control Manager, for one service by its key name. Each call answers with the state read after it.</summary>
internal interface IServiceControl
{
    /// <summary><c>running</c>, <c>stopped</c>, <c>start-pending</c>, ... as the value; an error when the service cannot be read.</summary>
    Task<BrokerResult> QueryAsync(string name, CancellationToken ct);

    Task<BrokerResult> StartAsync(string name, CancellationToken ct);

    Task<BrokerResult> StopAsync(string name, CancellationToken ct);

    /// <summary><paramref name="startType"/>: automatic, delayedAutomatic, manual or disabled; answers the start type read back.</summary>
    Task<BrokerResult> SetStartTypeAsync(string name, string startType, CancellationToken ct);
}
