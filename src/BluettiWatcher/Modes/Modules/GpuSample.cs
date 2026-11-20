namespace BluettiWatcher.Modes.Modules;

/// <summary>One GPU at one moment, as nvidia-smi reports it; <paramref name="PowerW"/> null when the board does not report it.</summary>
internal sealed record GpuSample(int Index, string PState, double ClockMhz, double? PowerW, double UtilizationPct);
