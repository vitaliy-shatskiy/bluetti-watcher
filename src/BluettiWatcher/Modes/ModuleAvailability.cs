namespace BluettiWatcher.Modes;

/// <summary>Whether a module can run here; <paramref name="Reason"/> says why not (shown as "unavailable", never a silent fallback).</summary>
internal sealed record ModuleAvailability(bool Available, string Reason = "")
{
    public static readonly ModuleAvailability Yes = new(true);

    public static ModuleAvailability No(string reason) => new(false, reason);
}
