using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using BluettiWatcher.Modes.Broker;

namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// <c>nvidia.profile</c>: imports a <c>.nip</c> file from the policy folder into the driver through NVIDIA Profile Inspector,
/// run by the admin broker. The driver's profile store is one resource, <c>nvidia/profiles</c>; its value is the
/// fingerprint of an export of every customised profile plus the export's file name. Restoring imports that "before"
/// export over the store again.
/// </summary>
/// <remarks>
/// Compensatable: the import replaces the profiles the export holds, but a profile the applied file created that was not
/// customised before stays. Pattern: Adapter.
/// </remarks>
internal sealed class NvidiaProfileModule(INvidiaProfileFiles files, IAdminBroker broker) : IActionModule, IChecksOnTransition
{
    public const string TypeId = "nvidia.profile";
    public const string ResourceKey = "nvidia/profiles";
    /// <summary>Broker operation: import <c>args.file</c> (a name in the policy folder) with replace semantics.</summary>
    public const string ApplyOperation = "nvidia.applyProfile";
    /// <summary>Broker operation: export every customised profile into the policy folder; answers <c>{ file, sha256 }</c>.</summary>
    public const string ExportOperation = "nvidia.exportProfiles";

    // The file each fingerprint came from, for writes this instance made: lets a read name what is applied. Lost on a
    // restart, which only costs one idempotent re-import.
    private readonly ConcurrentDictionary<string, string> _appliedBySha = new(StringComparer.OrdinalIgnoreCase);

    public ActionModuleDescriptor Descriptor { get; } = new(
        TypeId, 1, "NVIDIA driver profile (.nip)",
        (JsonObject)JsonNode.Parse("""
            {
              "type": "object",
              "properties": { "file": { "type": "string" } },
              "required": ["file"],
              "additionalProperties": false
            }
            """)!,
        (JsonObject)JsonNode.Parse("""{ "file": { "widget": "select", "list": "files", "label": "Profile file (.nip)" } }""")!,
        ["files"], RestorationClass.Compensatable, NeedsAdmin: true,
        (JsonObject)JsonNode.Parse("""{ "file": "" }""")!);

    public Task<ModuleAvailability> AvailableAsync(CancellationToken ct) =>
        Task.FromResult(files.InspectorMissing() is { } why ? ModuleAvailability.No(why) : ModuleAvailability.Yes);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>>> DiscoverAsync(CancellationToken ct)
    {
        var lists = new Dictionary<string, IReadOnlyList<DiscoveredItem>>(StringComparer.Ordinal);
        if (files.NipFiles() is { } names) lists["files"] = [.. names.Select(n => new DiscoveredItem(n, n))];
        return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>>>(lists);
    }

    /// <summary>An empty file = the action does nothing (a new action before a file is picked).</summary>
    public IReadOnlyList<string> Validate(JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string file = FileOf(settings);
        if (file.Length == 0) return [];
        return Path.GetFileName(file) != file || file.Contains("..", StringComparison.Ordinal)
            ? [$"\"{file}\" must be a file name in the policy folder, not a path"]
            : !file.EndsWith(".nip", StringComparison.OrdinalIgnoreCase)
                ? [$"\"{file}\" is not a .nip file"]
                : [];
    }

    public IReadOnlyList<ResourceIntent> Expand(JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string file = FileOf(settings);
        return file.Length == 0 ? [] : [new ResourceIntent(ResourceKey, new JsonObject { ["applied"] = file })];
    }

    public async Task<ResourceRead> ReadAsync(string resourceKey, CancellationToken ct)
    {
        if (!string.Equals(resourceKey, ResourceKey, StringComparison.Ordinal)) return ResourceRead.Failed($"not an NVIDIA profile key: {resourceKey}");
        var r = await broker.RunAsync(ExportOperation, [], ct);
        if (!r.Ok) return ResourceRead.Failed("the profile export failed: " + (r.Error ?? "no reason given"));
        if (Text(r.Value, "sha256") is not { Length: > 0 } sha || Text(r.Value, "file") is not { Length: > 0 } export)
            return ResourceRead.Failed("the profile export answered without a file or fingerprint");
        return ResourceRead.Of(new JsonObject
        {
            ["sha256"] = sha,
            ["export"] = export,
            ["applied"] = _appliedBySha.TryGetValue(sha, out var file) ? file : null,
        });
    }

    /// <summary>Two reads compare by fingerprint; a target (a file name only) compares by the file a read names.</summary>
    public bool Equivalent(string resourceKey, JsonNode? a, JsonNode? b)
    {
        if (Text(a, "sha256") is { } x && Text(b, "sha256") is { } y) return string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        return Text(a, "applied") is { } fa && Text(b, "applied") is { } fb && string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A target names the file to apply; a journaled baseline names the export to import back.</summary>
    public async Task<ApplyResult> ApplyAsync(ResourceIntent intent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (!string.Equals(intent.Key, ResourceKey, StringComparison.Ordinal)) return ApplyResult.Failed($"not an NVIDIA profile key: {intent.Key}");
        string? applied = Text(intent.Target, "applied");
        string? file = applied ?? Text(intent.Target, "export");
        if (string.IsNullOrEmpty(file)) return ApplyResult.Failed("the target names no file to import");
        var r = await broker.RunAsync(ApplyOperation, new JsonObject { ["file"] = file }, ct);
        if (!r.Ok) return ApplyResult.Failed($"importing {file} failed: {r.Error ?? "no reason given"}");
        var back = await ReadAsync(ResourceKey, ct);
        if (!back.Ok) return ApplyResult.Failed("imported, but the read-back failed: " + back.Error);
        if (applied is not null && Text(back.Value, "sha256") is { } sha)
        {
            _appliedBySha[sha] = applied;
            back.Value!["applied"] = applied;   // Ok read: Value is the object built in ReadAsync
        }
        return ApplyResult.Written(back.Value);
    }

    private static string FileOf(JsonObject settings) =>
        settings["file"] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>().Trim() : "";

    private static string? Text(JsonNode? node, string field) =>
        node is JsonObject o && o[field] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;
}
