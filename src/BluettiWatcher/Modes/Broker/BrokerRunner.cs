using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes.Broker;

/// <summary>
/// One run of the SYSTEM broker task (<c>BluettiWatcher.exe --broker</c>): the policy read once, then every pending
/// request taken, judged, answered and logged, until the folder is empty (bounded rounds), then exit. A file whose name
/// is not a request id (a client's half-written <c>.tmp</c>) is left alone.
/// </summary>
internal sealed class BrokerRunner(IBrokerInbox inbox, BrokerCore core, Action<string> log)
{
    private const int MaxRounds = 20;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var policy = BrokerCore.LoadPolicy(inbox.ReadPolicy(), out string policyError);
        if (policy is null) log($"policy: {policyError}");
        int handled = 0;
        for (int round = 0; round < MaxRounds; round++)
        {
            var names = inbox.PendingRequests().Where(n => BrokerRequest.IdOf(n) is not null).ToList();
            if (names.Count == 0) break;
            foreach (var name in names)
            {
                var answer = await HandleOneAsync(name, policy, policyError, ct);
                log($"{name} {answer.Operation}: {(answer.Result.Ok ? "ok" : answer.Result.Error)}");
                if (answer.Id is { } id) inbox.WriteResponse(id, ResponseJson(id, answer.Result));
                handled++;
            }
        }
        log($"broker run done: {handled} request(s)");
        return 0;
    }

    private async Task<BrokerAnswer> HandleOneAsync(string name, BrokerPolicy? policy, string policyError, CancellationToken ct)
    {
        try { return await core.HandleAsync(name, inbox.TakeRequest(name), policy, policyError, ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // One request's failure is answered and logged; the others still run.
            return new BrokerAnswer(BrokerRequest.IdOf(name), "", new BrokerResult(false, $"broker error: {e.GetType().Name}: {e.Message}", null));
        }
    }

    internal static string ResponseJson(string id, BrokerResult r) => new JsonObject
    {
        ["id"] = id,
        ["ok"] = r.Ok,
        ["error"] = r.Error,
        ["value"] = r.Value?.DeepClone(),
    }.ToJsonString();
}
