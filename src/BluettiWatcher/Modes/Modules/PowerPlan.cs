namespace BluettiWatcher.Modes.Modules;

/// <summary>A Windows power plan (scheme) on this machine: its GUID and friendly name.</summary>
internal sealed record PowerPlan(Guid Id, string Name);
