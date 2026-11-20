using BluettiWatcher.Modes.Modules;

namespace BluettiWatcher.Tests.Modes;

/// <summary>OpenRGB without a server: records each call as <c>off:a,b</c> / <c>profile:name</c>; a set failure is returned by every write.</summary>
internal sealed class FakeOpenRgb : IOpenRgb
{
    public string? ExePath { get; set; } = "C:\\Program Files\\OpenRGB\\OpenRGB.exe";
    public IReadOnlyList<string>? ProfileNames { get; set; } = ["Normal", "Night"];
    public IReadOnlyList<string>? Running { get; set; }
    public string? Failure { get; set; }
    public List<string> Calls { get; } = [];

    public IReadOnlyList<string>? Profiles() => ProfileNames;

    public Task<IReadOnlyList<string>?> RunningDevicesAsync(CancellationToken ct) => Task.FromResult(Running);

    public Task<string?> TurnOffAsync(IReadOnlyList<string> devices, CancellationToken ct)
    {
        Calls.Add("off:" + string.Join(',', devices));
        return Task.FromResult(Failure);
    }

    public Task<string?> LoadProfileAsync(string profile, CancellationToken ct)
    {
        Calls.Add("profile:" + profile);
        return Task.FromResult(Failure);
    }
}
