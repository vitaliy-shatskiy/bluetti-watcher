namespace BluettiWatcher.Modes.Broker;

/// <summary>
/// The broker's two folders. <see cref="Root"/> (<c>%ProgramFiles%\BluettiWatcher\broker</c>) holds what SYSTEM trusts:
/// the exe it runs (<c>bin\</c>), the policy, the profiles and exports, the log; only administrators write there.
/// <see cref="Exchange"/> (<c>%ProgramData%\BluettiWatcherBroker</c>) is where the user's service talks to it: the user
/// may only add files to <see cref="Requests"/> and read <see cref="Responses"/>. Neither is ever inside the folder the
/// power-mode flag lives in, which the user may change.
/// </summary>
internal sealed record BrokerPaths(string Root, string Exchange)
{
    /// <summary>The scheduled task that runs <c>BluettiWatcher.exe --broker</c> as SYSTEM, started on demand.</summary>
    public const string TaskName = "BluettiWatcherBroker";

    public static BrokerPaths Default => new(
        Path.Combine(Io.KnownFolders.ProgramFiles, "BluettiWatcher", "broker"),
        Path.Combine(Io.KnownFolders.ProgramData, "BluettiWatcherBroker"));

    public string Policy => Path.Combine(Root, "policy.json");
    public string Requests => Path.Combine(Exchange, "requests");
    public string Responses => Path.Combine(Exchange, "responses");
    public string Profiles => Path.Combine(Root, "profiles");
    public string Exports => Path.Combine(Root, "exports");
    public string Log => Path.Combine(Root, "broker.log");
}
