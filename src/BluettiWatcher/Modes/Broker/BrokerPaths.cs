namespace BluettiWatcher.Modes.Broker;

/// <summary>
/// The broker's folder (<c>%ProgramData%\BluettiWatcher\broker</c>): Administrators and SYSTEM write it, the installing user
/// reads it and may only add files to <see cref="Requests"/>. The policy, the profiles and the broker's exe live here.
/// </summary>
internal sealed record BrokerPaths(string Root)
{
    /// <summary>The scheduled task that runs <c>BluettiWatcher.exe --broker</c> as SYSTEM, started on demand.</summary>
    public const string TaskName = "BluettiWatcherBroker";

    public static BrokerPaths Default => new(Path.Combine(Io.KnownFolders.ProgramData, "BluettiWatcher", "broker"));

    public string Policy => Path.Combine(Root, "policy.json");
    public string Requests => Path.Combine(Root, "requests");
    public string Responses => Path.Combine(Root, "responses");
    public string Profiles => Path.Combine(Root, "profiles");
    public string Log => Path.Combine(Root, "broker.log");
}
