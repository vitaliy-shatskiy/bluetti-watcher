namespace BluettiWatcher.Modes.Modules;

/// <summary>Read-only nvidia-smi queries: the only way the GPU idle watcher reaches the GPU.</summary>
internal interface IGpuSampler
{
    /// <summary>P-state, graphics clock, power draw and utilisation per GPU; null when nvidia-smi did not answer.</summary>
    Task<IReadOnlyList<GpuSample>?> SampleAsync(CancellationToken ct);

    /// <summary>The processes holding a GPU (compute and graphics); null when nvidia-smi did not answer.</summary>
    Task<IReadOnlyList<GpuProcess>?> ProcessesAsync(CancellationToken ct);
}
