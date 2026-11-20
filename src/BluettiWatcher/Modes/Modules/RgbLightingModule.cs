using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// <c>rgb.lighting</c>: the RGB lighting off while a mode is active, through OpenRGB's SDK server (<see cref="IOpenRgb"/>).
/// Entering turns the listed devices (none listed = all) off; leaving loads the OpenRGB profile the settings name, or
/// leaves the lighting off when they name none. Key <c>rgb/lighting/off</c> (bool).
/// </summary>
/// <remarks>
/// Pattern: Adapter. Compensatable, not a snapshot: OpenRGB cannot read what some controllers show (a Gigabyte RGB Fusion 2
/// board, whose lighting its own detection resets), so the "on" state is the profile the person saved, not what was lit
/// before. Like <see cref="CommandPairModule"/>, the value it reads is what it last did; a write of <c>true</c> carries the
/// devices and the profile (<c>{ "off": true, "devices": [...], "profile": "name" }</c>) and the profile is kept for the
/// restore, which writes the bare baseline <c>false</c>. After a restart the reconciler recalls the journaled target
/// (<see cref="IRecallsWrites"/>), so leaving the mode still loads the profile. One failed call is one failed write: the
/// module never retries on its own (the reconciler backs off and alerts once).
/// </remarks>
internal sealed class RgbLightingModule(IOpenRgb rgb, Action<string> log) : IActionModule, IRecallsWrites
{
    public const string Id = "rgb.lighting";
    public const string Key = "rgb/lighting/off";

    private readonly Lock _lock = new();
    private JsonObject? _entered;

    public ActionModuleDescriptor Descriptor { get; } = new(
        Id, 1, "RGB lighting off (OpenRGB)",
        JsonFields.Parse("""
            {"type":"object","additionalProperties":false,"required":["devices","profile"],"properties":{
              "devices":{"type":"array","items":{"type":"string"},"maxItems":64},
              "profile":{"type":"string","maxLength":128}}}
            """),
        JsonFields.Parse("""
            {"devices":{"widget":"multiselect","list":"devices","label":"Devices to turn off (none = all; listed while OpenRGB runs)"},
             "profile":{"widget":"select","list":"profiles","label":"OpenRGB profile to load on leave (empty = leave the lighting off)"}}
            """),
        ["devices", "profiles"], RestorationClass.Compensatable, NeedsAdmin: false,
        JsonFields.Parse("""{"devices":[],"profile":""}"""));

    public Task<ModuleAvailability> AvailableAsync(CancellationToken ct) => Task.FromResult(rgb.ExePath is null
        ? ModuleAvailability.No("OpenRGB is not installed (OpenRGB.exe not found; config.json modes.openRgbPath names another place)")
        : ModuleAvailability.Yes);

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>>> DiscoverAsync(CancellationToken ct)
    {
        var lists = new Dictionary<string, IReadOnlyList<DiscoveredItem>>(StringComparer.Ordinal);
        if (rgb.Profiles() is { } profiles)
            lists["profiles"] = [.. profiles.Order(StringComparer.OrdinalIgnoreCase).Select(p => new DiscoveredItem(p, p))];
        if (await rgb.RunningDevicesAsync(ct) is { } devices)
            lists["devices"] = [.. devices.Distinct(StringComparer.Ordinal).Select(d => new DiscoveredItem(d, d))];
        return lists;
    }

    public IReadOnlyList<string> Validate(JsonObject settings)
    {
        var problems = new List<string>();
        if (settings["devices"] is not JsonArray devices || devices.Any(d => JsonFields.Str(d) is null)) problems.Add("devices must be a list of device names");
        else JsonFields.CheckNames(JsonFields.Strings(devices), "device", problems);
        if (JsonFields.Str(settings["profile"]) is not { } profile) problems.Add("profile must be a profile name (empty = none)");
        else if (profile.IndexOfAny(['/', '\\', ':']) >= 0) problems.Add($"profile '{profile}': a profile name, not a path");
        return problems;
    }

    public IReadOnlyList<ResourceIntent> Expand(JsonObject settings) =>
    [
        new ResourceIntent(Key, new JsonObject
        {
            ["off"] = true,
            ["devices"] = new JsonArray([.. JsonFields.Strings(settings["devices"]).Select(d => (JsonNode)d)]),
            ["profile"] = JsonFields.Str(settings["profile"]) ?? "",
        }),
    ];

    public Task<ResourceRead> ReadAsync(string resourceKey, CancellationToken ct)
    {
        if (resourceKey != Key) return Task.FromResult(ResourceRead.Failed($"not a {Id} key: {resourceKey}"));
        lock (_lock) return Task.FromResult(ResourceRead.Of(JsonValue.Create(_entered is not null)));
    }

    public bool Equivalent(string resourceKey, JsonNode? a, JsonNode? b) => OffOf(a) is { } x && OffOf(b) is { } y && x == y;

    public async Task<ApplyResult> ApplyAsync(ResourceIntent intent, CancellationToken ct)
    {
        if (intent.Key != Key) return ApplyResult.Failed($"not a {Id} key: {intent.Key}");
        if (OffOf(intent.Target) is not { } wantOff) return ApplyResult.Failed($"{Id}: the target must say off true or false");
        JsonObject? entered;
        lock (_lock) entered = _entered;
        if ((entered is not null) == wantOff) return ApplyResult.Written(JsonValue.Create(wantOff));

        if (wantOff)
        {
            if (intent.Target is not JsonObject target) return ApplyResult.Failed($"{Id}: turning off needs the devices and the profile");
            var devices = JsonFields.Strings(target["devices"]);
            string? error = await rgb.TurnOffAsync(devices, ct);
            log($"{Id}: off ({(devices.Count == 0 ? "all devices" : string.Join(", ", devices))}) -> {error ?? "done"}");
            if (error is not null) return ApplyResult.Failed(error);
            lock (_lock) _entered = (JsonObject)target.DeepClone();
            return ApplyResult.Written(JsonValue.Create(true));
        }

        // Leaving: the profile given with the target wins, else the one kept when the lighting went off.
        var source = intent.Target as JsonObject ?? entered!; // entered is non-null here: (entered is not null) != wantOff, wantOff is false
        string profile = JsonFields.Str(source["profile"]) ?? "";
        if (profile.Length > 0)
        {
            string? error = await rgb.LoadProfileAsync(profile, ct);
            log($"{Id}: profile '{profile}' -> {error ?? "loaded"}");
            if (error is not null) return ApplyResult.Failed(error);
        }
        else log($"{Id}: no profile chosen, the lighting stays off");
        lock (_lock) _entered = null;
        return ApplyResult.Written(JsonValue.Create(false));
    }

    /// <summary>A journaled "off" target reads as off again after a restart, so leaving still loads its profile.</summary>
    public void Recall(string resourceKey, JsonNode? target)
    {
        if (resourceKey != Key || target is not JsonObject off || OffOf(off) != true) return;
        lock (_lock) _entered = (JsonObject)off.DeepClone();
    }

    private static bool? OffOf(JsonNode? value) => value is JsonObject o ? JsonFields.Bool(o["off"]) : JsonFields.Bool(value);
}
