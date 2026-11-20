using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// <c>windows.phoneLink</c>: ends the Phone Link / cross-device background processes while a mode is entered (a preset
/// of <see cref="ProcessCloseModule"/>: they have no windows, so they are ended after a short grace).
/// </summary>
/// <remarks>
/// Pattern: Adapter. Why a process preset and not the "Mobile devices" setting: that switch lives in the user's registry
/// (<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Mobility</c>, <c>PhoneLinkEnabled</c> / <c>CrossDeviceEnabled</c>),
/// but no public contract says the running cross-device service honours a direct write without the Settings app's own
/// notification, so a Reversible module on it could report a state that does not hold. Irreversible: Windows starts these
/// processes again on its own when a phone or app asks for them; this module starts nothing.
/// </remarks>
internal sealed class PhoneLinkModule(IProcessControl processes, TimeProvider time) : IActionModule
{
    public const string Id = "windows.phoneLink";
    private const int GraceSec = 2;

    /// <summary>The cross-device experience host, the Phone Link app host and the cross-device resume helper (Windows 11).</summary>
    public static readonly IReadOnlyList<string> Processes = ["CrossDeviceService.exe", "PhoneExperienceHost.exe", "CrossDeviceResume.exe"];

    private readonly ProcessCloser _closer = new(processes, time);

    public ActionModuleDescriptor Descriptor { get; } = new(
        Id, 1, "Phone Link background",
        JsonFields.Parse("""{"type":"object","additionalProperties":false,"properties":{}}"""),
        JsonFields.Parse("{}"), [], RestorationClass.Irreversible, NeedsAdmin: false,
        JsonFields.Parse("{}"));

    public Task<ModuleAvailability> AvailableAsync(CancellationToken ct) =>
        Task.FromResult(processes.Count(Processes[0]) is null ? ModuleAvailability.No("the process list cannot be read") : ModuleAvailability.Yes);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>>> DiscoverAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<DiscoveredItem>>>(new Dictionary<string, IReadOnlyList<DiscoveredItem>>(StringComparer.Ordinal));

    public IReadOnlyList<string> Validate(JsonObject settings) => [];

    public IReadOnlyList<ResourceIntent> Expand(JsonObject settings) =>
        [.. Processes.Select(p => new ResourceIntent(ProcessCloser.Key(p), ProcessCloser.CloseTarget(GraceSec, kill: true)))];

    public Task<ResourceRead> ReadAsync(string resourceKey, CancellationToken ct) => _closer.ReadAsync(resourceKey);

    public bool Equivalent(string resourceKey, JsonNode? a, JsonNode? b) => ProcessCloser.Equivalent(a, b);

    /// <summary>Only the preset processes: a key naming any other process is refused.</summary>
    public Task<ApplyResult> ApplyAsync(ResourceIntent intent, CancellationToken ct) =>
        ProcessCloser.ExeOf(intent.Key) is { } exe && Processes.Contains(exe, StringComparer.OrdinalIgnoreCase)
            ? _closer.ApplyAsync(intent, ct)
            : Task.FromResult(ApplyResult.Failed($"not a {Id} key: {intent.Key}"));
}
