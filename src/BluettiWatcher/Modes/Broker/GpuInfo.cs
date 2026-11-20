namespace BluettiWatcher.Modes.Broker;

/// <summary>One NVIDIA GPU as nvidia-smi reports it: its index, name and power limits in watts (in force, min, max, default).</summary>
internal sealed record GpuInfo(string Id, string Name, double Watts, double Min, double Max, double Default);
