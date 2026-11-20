namespace BluettiWatcher.Modes.Broker;

/// <summary>nvidia-smi: reading the GPUs needs no elevation; setting a power limit does (the broker calls it).</summary>
internal interface INvidiaSmi
{
    Task<GpuQuery> QueryAsync(CancellationToken ct);

    /// <summary>Sets the power limit of GPU <paramref name="gpu"/> (its index); null = done, else the error.</summary>
    Task<string?> SetPowerLimitAsync(string gpu, double watts, CancellationToken ct);
}
