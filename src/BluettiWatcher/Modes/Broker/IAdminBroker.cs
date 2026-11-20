using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes.Broker;

/// <summary>
/// Runs one allow-listed admin operation (service start/stop, GPU power limit, driver profile) in the SYSTEM broker task
/// and returns its answer. The broker refuses any operation or target outside its policy.
/// </summary>
internal interface IAdminBroker
{
    Task<BrokerResult> RunAsync(string operation, JsonObject args, CancellationToken ct);
}

/// <summary>The broker's answer: <paramref name="Ok"/> false carries <paramref name="Error"/>; <paramref name="Value"/> is the read-back.</summary>
internal sealed record BrokerResult(bool Ok, string? Error, JsonNode? Value);
