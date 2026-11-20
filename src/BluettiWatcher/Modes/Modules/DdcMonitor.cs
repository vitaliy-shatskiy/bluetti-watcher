namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// One physical monitor seen through DDC/CI: <paramref name="Id"/> is the same device path the refresh-rate module uses;
/// <paramref name="Ddc"/> false = it did not answer the brightness query (<paramref name="Reason"/> says how).
/// </summary>
internal sealed record DdcMonitor(string Id, string Name, bool Ddc, string Reason = "");
