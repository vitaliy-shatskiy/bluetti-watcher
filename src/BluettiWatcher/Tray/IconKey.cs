namespace BluettiWatcher.Tray;

/// <summary>The inputs of one icon picture (<see cref="TrayIconRenderer.Key"/>).</summary>
internal sealed record IconKey(string Text, TrayPower Power, bool Stale, int Size);
