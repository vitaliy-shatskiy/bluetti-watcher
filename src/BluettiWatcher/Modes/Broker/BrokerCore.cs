using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BluettiWatcher.Modes.Broker;

/// <summary>
/// The broker's decisions, default deny: a request runs only when the policy is trusted and valid, the request file is a
/// plain file owned by the policy's user (or an administrator), fresh, well-formed, names a known operation with exactly
/// its arguments, and the target is allow-listed (a service, a profile) or in range (a GPU limit inside the card's
/// min..max). Nothing from a request ever reaches a shell or a command line as text: services go to the SCM by name,
/// numbers are re-formatted, profiles are file names joined to the profiles folder.
/// </summary>
/// <remarks>Pattern: Table-driven Strategy (one entry per operation).</remarks>
internal sealed partial class BrokerCore
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxSkew = TimeSpan.FromSeconds(5);

    /// <summary>Administrators, SYSTEM, TrustedInstaller: the accounts that may own and write the policy.</summary>
    internal static readonly HashSet<string> AdminSids = new(
        ["S-1-5-32-544", "S-1-5-18", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"], StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("^[0-9]{1,2}$", RegexOptions.CultureInvariant, BrokerPolicy.MatchTimeoutMs)]
    internal static partial Regex GpuIdPattern();

    private sealed record Operation(string[] Args, Func<JsonObject, BrokerPolicy, CancellationToken, Task<BrokerResult>> Run);

    private readonly BrokerPaths _paths;
    private readonly IServiceControl _services;
    private readonly ITaskControl _tasks;
    private readonly INvidiaSmi _gpu;
    private readonly IProfileImporter _importer;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Operation> _operations;

    public BrokerCore(BrokerPaths paths, IServiceControl services, ITaskControl tasks, INvidiaSmi gpu, IProfileImporter importer, TimeProvider time)
    {
        _paths = paths;
        _services = services;
        _tasks = tasks;
        _gpu = gpu;
        _importer = importer;
        _time = time;
        _operations = new(StringComparer.Ordinal)
        {
            ["service.query"] = new(["name"], (a, p, ct) => ServiceAsync(a, p, _services.QueryAsync, ct)),
            ["service.start"] = new(["name"], (a, p, ct) => ServiceAsync(a, p, _services.StartAsync, ct)),
            ["service.stop"] = new(["name"], (a, p, ct) => ServiceAsync(a, p, _services.StopAsync, ct)),
            ["service.setStartType"] = new(["name", "startType"], SetStartTypeAsync),
            ["task.enable"] = new(["name"], (a, p, ct) => TaskAsync(a, p, true, ct)),
            ["task.disable"] = new(["name"], (a, p, ct) => TaskAsync(a, p, false, ct)),
            ["gpu.query"] = new([], (_, _, ct) => GpuQueryAsync(ct)),
            ["gpu.setPowerLimit"] = new(["gpu", "watts"], SetPowerLimitAsync),
            ["nvidia.applyProfile"] = new(["file"], ApplyProfileAsync),
            ["nvidia.exportProfiles"] = new([], ExportProfilesAsync),
        };
    }

    public IReadOnlyCollection<string> Operations => _operations.Keys;

    private static BrokerResult Refused(string why) => new(false, $"refused: {why}", null);

    /// <summary>The policy when its file is trusted (owned and writable only by administrators, a plain file) and valid · null = deny all.</summary>
    public static BrokerPolicy? LoadPolicy(SecuredFile? file, out string error)
    {
        if (file is null) { error = "no readable policy file: every request is refused"; return null; }
        if (!file.Plain) { error = "the policy file is a link or has several names: every request is refused"; return null; }
        if (!AdminSids.Contains(file.OwnerSid)) { error = $"the policy file is owned by {file.OwnerSid}, not an administrator: every request is refused"; return null; }
        if (file.OtherWriters.Count > 0) { error = $"the policy file is writable by {string.Join(", ", file.OtherWriters)}: every request is refused"; return null; }
        var policy = BrokerPolicy.Parse(file.Text, out error);
        if (policy is null) error += ": every request is refused";
        return policy;
    }

    /// <summary>One request file judged and, when allowed, run. Never throws for a bad request.</summary>
    public async Task<BrokerAnswer> HandleAsync(string fileName, SecuredFile? file, BrokerPolicy? policy, string policyError, CancellationToken ct)
    {
        string? id = BrokerRequest.IdOf(fileName);
        if (id is null) return new(null, "", Refused($"'{fileName}' is not a request file name"));
        if (policy is null) return new(id, "", Refused(policyError));
        if (RefuseFile(file, policy) is { } bad) return new(id, "", Refused(bad));
        var request = BrokerRequest.Parse(id, file!.Text, out string parseError);   // RefuseFile refused a null file
        if (request is null) return new(id, "", Refused(parseError));
        var now = _time.GetUtcNow();
        if (request.Issued < now - MaxAge || request.Issued > now + MaxSkew)
            return new(id, request.Operation, Refused($"the request was issued at {request.Issued:O}, not within {MaxAge.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s of now"));
        if (!_operations.TryGetValue(request.Operation, out var op)) return new(id, request.Operation, Refused($"unknown operation '{request.Operation}'"));
        if (RefuseArgs(request.Args, op.Args) is { } argError) return new(id, request.Operation, Refused(argError));
        return new(id, request.Operation, await op.Run(request.Args, policy, ct));
    }

    private static string? RefuseFile(SecuredFile? file, BrokerPolicy policy)
    {
        if (file is null) return "the request file could not be opened";
        if (!file.Plain) return "the request file is a link or has several names";
        if (!string.Equals(file.OwnerSid, policy.UserSid, StringComparison.OrdinalIgnoreCase) && !AdminSids.Contains(file.OwnerSid))
            return $"the request file is owned by {file.OwnerSid}, not the policy's user";
        return null;
    }

    private static string? RefuseArgs(JsonObject args, string[] expected)
    {
        foreach (var (key, _) in args)
            if (!expected.Contains(key, StringComparer.Ordinal)) return $"unexpected argument '{key}'";
        foreach (var key in expected)
            if (!args.ContainsKey(key)) return $"missing argument '{key}'";
        return null;
    }

    private static string? Text(JsonObject args, string key) => args[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    /// <summary>The policy's own spelling of the requested service (what reaches the SCM, never the request's), or the refusal.</summary>
    private static (string? Name, BrokerResult? Refusal) ListedService(JsonObject args, BrokerPolicy policy)
    {
        string? name = Text(args, "name");
        if (name is null || !BrokerPolicy.Matches(BrokerPolicy.ServiceNamePattern(), name)) return (null, Refused("the service name is not a service key name"));
        if (!policy.AllowsService(name)) return (null, Refused($"the service '{name}' is not in the policy"));
        return (policy.Services.First(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase)), null);
    }

    private static async Task<BrokerResult> ServiceAsync(JsonObject args, BrokerPolicy policy, Func<string, CancellationToken, Task<BrokerResult>> call, CancellationToken ct)
    {
        var (name, refusal) = ListedService(args, policy);
        return refusal ?? await call(name!, ct);   // ListedService gives a name or a refusal
    }

    private async Task<BrokerResult> SetStartTypeAsync(JsonObject args, BrokerPolicy policy, CancellationToken ct)
    {
        string? type = Text(args, "startType");
        if (type is null || !ScmServiceControl.StartTypes.ContainsKey(type)) return Refused("startType is not automatic, delayedAutomatic, manual or disabled");
        var (name, refusal) = ListedService(args, policy);
        return refusal ?? await _services.SetStartTypeAsync(name!, type, ct);   // ListedService gives a name or a refusal
    }

    private async Task<BrokerResult> TaskAsync(JsonObject args, BrokerPolicy policy, bool enable, CancellationToken ct)
    {
        string? name = Text(args, "name");
        if (name is null || !BrokerPolicy.Matches(BrokerPolicy.TaskNamePattern(), name)) return Refused("the task name is not a task name");
        // The policy's own spelling goes to the Task Scheduler.
        return policy.ListedTask(name) is { } listed ? await _tasks.SetEnabledAsync(listed, enable, ct) : Refused($"the task '{name}' is not in the policy");
    }

    private async Task<BrokerResult> GpuQueryAsync(CancellationToken ct)
    {
        var q = await _gpu.QueryAsync(ct);
        if (q.Gpus is null) return new(false, q.Error, null);
        return new(true, null, new JsonArray([.. q.Gpus.Select(g => (JsonNode?)GpuJson(g))]));
    }

    internal static JsonObject GpuJson(GpuInfo g) => new()
    {
        ["id"] = g.Id,
        ["name"] = g.Name,
        ["watts"] = g.Watts,
        ["min"] = g.Min,
        ["max"] = g.Max,
        ["default"] = g.Default,
    };

    private async Task<BrokerResult> SetPowerLimitAsync(JsonObject args, BrokerPolicy policy, CancellationToken ct)
    {
        string? gpu = Text(args, "gpu");
        if (gpu is null || !BrokerPolicy.Matches(GpuIdPattern(), gpu)) return Refused("gpu is not a GPU index");
        if (args["watts"] is not JsonValue wv || !wv.TryGetValue(out double watts) || !double.IsFinite(watts)) return Refused("watts is not a number");
        var q = await _gpu.QueryAsync(ct);
        if (q.Gpus is null) return Refused($"the GPU limits could not be read ({q.Error})");
        var card = q.Gpus.FirstOrDefault(g => g.Id == gpu);
        if (card is null) return Refused($"there is no GPU {gpu} with a power limit");
        if (watts < card.Min || watts > card.Max)
            return Refused($"{watts.ToString(CultureInfo.InvariantCulture)} W is outside GPU {gpu}'s {card.Min.ToString(CultureInfo.InvariantCulture)}..{card.Max.ToString(CultureInfo.InvariantCulture)} W");
        if (await _gpu.SetPowerLimitAsync(card.Id, watts, ct) is { } setError) return new(false, setError, null);
        var back = await _gpu.QueryAsync(ct);
        var now = back.Gpus?.FirstOrDefault(g => g.Id == gpu);
        return now is null ? new(false, $"the limit was set but could not be read back ({back.Error})", null) : new(true, null, GpuJson(now));
    }

    private const string ExportsPrefix = "exports/";

    private BrokerResult? RefuseInspector(BrokerPolicy policy)
    {
        if (policy.ProfileInspector.Length == 0) return Refused("the policy names no NVIDIA Profile Inspector");
        if (!AdminOnlyLocation(policy.ProfileInspector, _paths))
            return Refused($"NVIDIA Profile Inspector at {policy.ProfileInspector} is not under Program Files or the broker folder (SYSTEM never runs an exe the user can replace)");
        return null;
    }

    private async Task<BrokerResult> ExportProfilesAsync(JsonObject args, BrokerPolicy policy, CancellationToken ct)
    {
        if (RefuseInspector(policy) is { } bad) return bad;
        string folder = Path.GetFullPath(_paths.Exports);
        var export = await _importer.ExportAsync(policy.ProfileInspector, folder, ct);
        if (export.File.Length == 0) return new(false, export.Error, null);
        string file = Path.GetFullPath(export.File);
        if (!string.Equals(Path.GetDirectoryName(file), folder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return new(false, $"the export landed outside {folder}", null);
        await using var stream = File.OpenRead(file);
        string hash = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct));
        return new(true, null, new JsonObject { ["file"] = ExportsPrefix + Path.GetFileName(file), ["sha256"] = hash });
    }

    /// <summary>
    /// A path under Program Files (both) or the broker's own folder (itself under Program Files): places only
    /// administrators write by default. Never the exchange folder, where the user adds request files.
    /// </summary>
    internal static bool AdminOnlyLocation(string path, BrokerPaths paths)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        string full = Path.GetFullPath(path);
        if (Under(full, paths.Exchange)) return false;
        string[] roots = [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), paths.Root];
        return roots.Any(r => Under(full, r));
    }

    private static bool Under(string full, string root) =>
        root.Length > 0 && full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private async Task<BrokerResult> ApplyProfileAsync(JsonObject args, BrokerPolicy policy, CancellationToken ct)
    {
        // Two shapes only: a listed profile name (profiles\), or exports/<name> (a file this broker exported).
        string? name = Text(args, "file");
        bool exported = name is not null && name.StartsWith(ExportsPrefix, StringComparison.Ordinal);
        string? bare = exported ? name![ExportsPrefix.Length..] : name;
        if (bare is null || !BrokerPolicy.Matches(BrokerPolicy.ProfileNamePattern(), bare)) return Refused("the file is not a .nip file name in profiles or exports");
        if (!exported && !policy.AllowsProfile(bare)) return Refused($"the profile '{bare}' is not in the policy");
        if (RefuseInspector(policy) is { } bad) return bad;
        string folder = Path.GetFullPath(exported ? _paths.Exports : _paths.Profiles);
        string file = Path.GetFullPath(Path.Combine(folder, bare));
        if (!string.Equals(Path.GetDirectoryName(file), folder.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return Refused($"the profile '{name}' is outside the profiles folder");
        if (!File.Exists(file)) return new(false, $"the profile file {file} is missing", null);
        return await _importer.ImportAsync(policy.ProfileInspector, file, ct) is { } error ? new(false, error, null) : new(true, null, name);
    }
}
