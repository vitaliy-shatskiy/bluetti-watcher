using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BluettiWatcher.Io;

namespace BluettiWatcher.Modes.Broker;

/// <summary>
/// The service's side of the broker: writes a request file (a <c>.tmp</c> renamed into place, so the broker never reads
/// half of it), starts the on-demand SYSTEM task, and waits for the answer, bounded by <paramref name="wait"/> (10 s).
/// </summary>
internal sealed class BrokerClient(BrokerPaths paths, IProcessRunner runner, TimeProvider time, TimeSpan? wait = null) : IAdminBroker
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);
    private readonly TimeSpan _wait = wait ?? TimeSpan.FromSeconds(10);

    private static string SchTasks => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");

    private static BrokerResult Failed(string why) => new(false, why, null);

    public async Task<BrokerResult> RunAsync(string operation, JsonObject args, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(operation);
        ArgumentNullException.ThrowIfNull(args);
        var request = new BrokerRequest(BrokerRequest.NewId(), operation, args, time.GetUtcNow());
        string file = Path.Combine(paths.Requests, request.Id + ".json");
        try
        {
            await File.WriteAllTextAsync(file + ".tmp", request.ToJson(), ct);
            File.Move(file + ".tmp", file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(file + ".tmp");
            return Failed($"the broker's request folder {paths.Requests} cannot be written ({e.Message}): install.ps1 -Broker registers the broker");
        }
        var run = await runner.RunAsync(SchTasks, ["/run", "/tn", BrokerPaths.TaskName], TimeSpan.FromSeconds(10), ct);
        if (run.TimedOut || run.ExitCode != 0)
        {
            TryDelete(file);
            return Failed($"the broker task {BrokerPaths.TaskName} could not be started: {ProcessText.Last(run)}");
        }
        string answer = Path.Combine(paths.Responses, request.Id + ".json");
        long started = time.GetTimestamp();
        while (true)
        {
            if (File.Exists(answer) && ReadAnswer(answer, request.Id) is { } result) return result;
            if (time.GetElapsedTime(started) >= _wait) break;
            await Task.Delay(Poll, time, ct);
        }
        return Failed($"the broker did not answer within {_wait.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s");
    }

    /// <summary>The answer file, or null while it cannot be read yet.</summary>
    internal static BrokerResult? ReadAnswer(string path, string id)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _ = e; return null; }
        try
        {
            if (JsonNode.Parse(text) is not JsonObject o || o["id"]?.GetValue<string>() != id || o["ok"] is not JsonValue okv || !okv.TryGetValue(out bool ok))
                return Failed("the broker's answer is malformed");
            string? error = o["error"] is JsonValue ev && ev.TryGetValue(out string? s) ? s : null;
            return new BrokerResult(ok, ok ? null : error ?? "the broker refused", o["value"]?.DeepClone());
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return Failed($"the broker's answer is malformed: {e.Message}"); }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _ = e; /* the broker removes stale requests unread */ }
    }
}
