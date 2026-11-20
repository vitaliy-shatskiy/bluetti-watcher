namespace BluettiWatcher.Modes.Modules;

/// <summary>A process holding a GPU: <paramref name="Type"/> is nvidia-smi's C (compute), G (graphics) or C+G.</summary>
internal sealed record GpuProcess(int Gpu, int Pid, string Type, string Name);
