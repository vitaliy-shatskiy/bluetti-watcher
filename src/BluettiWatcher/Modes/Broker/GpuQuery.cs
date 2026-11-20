namespace BluettiWatcher.Modes.Broker;

/// <summary>The GPUs read, or why they could not be (<see cref="Gpus"/> null then; an empty list is a real "no GPU").</summary>
internal sealed record GpuQuery(IReadOnlyList<GpuInfo>? Gpus, string Error)
{
    public static GpuQuery Of(IReadOnlyList<GpuInfo> gpus) => new(gpus, "");

    public static GpuQuery Failed(string error) => new(null, string.IsNullOrEmpty(error) ? "nvidia-smi gave no answer" : error);
}
