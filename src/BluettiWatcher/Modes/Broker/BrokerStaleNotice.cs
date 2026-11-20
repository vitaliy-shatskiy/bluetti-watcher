using System.Globalization;

namespace BluettiWatcher.Modes.Broker;

/// <summary>
/// Tells a broker older than the service: the event <see cref="BrokerVersion.StaleEvent"/> (a tray notice) and a log line.
/// An upgrade starts the new service seconds before the elevated install step refreshes the broker, so the state is
/// checked again after <see cref="Grace"/> and told only when it still holds; once per service version (the marker file
/// keeps the version told), as /api/health names the state all along.
/// </summary>
internal sealed class BrokerStaleNotice(Func<BrokerStatus> state, string version, string markerPath,
    Func<TimeSpan, CancellationToken, Task> wait, Action<string, string> addEvent, Action<string> log)
{
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    public async Task RunAsync(CancellationToken ct)
    {
        if (state() is not { State: "stale" }) return;
        log(string.Create(CultureInfo.InvariantCulture, $"broker: older than the service; told in {Grace.TotalMinutes:0} min unless install.ps1 -Broker -Apply (elevated) updates it first"));
        await wait(Grace, ct);
        if (state() is not { State: "stale", Problem: { } problem })
        {
            log("broker: updated within the grace");
            return;
        }
        log($"WARNING - {problem}");
        if (await ToldAsync(ct) == version) return;
        addEvent(BrokerVersion.StaleEvent, problem);
        // The event is out: the marker is written even when the service stops now, or the notice repeats at the next start.
        try { await File.WriteAllTextAsync(markerPath, version, CancellationToken.None); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"broker: could not write {markerPath} ({e.Message}); the notice comes again at the next start");
        }
    }

    private async Task<string?> ToldAsync(CancellationToken ct)
    {
        try { return File.Exists(markerPath) ? (await File.ReadAllTextAsync(markerPath, ct)).Trim() : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"broker: could not read {markerPath} ({e.Message})");
            return null;
        }
    }
}
