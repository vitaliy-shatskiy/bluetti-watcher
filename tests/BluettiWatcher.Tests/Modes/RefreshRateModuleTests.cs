using System.Text.Json.Nodes;
using BluettiWatcher.Modes;
using BluettiWatcher.Modes.Modules;

namespace BluettiWatcher.Tests.Modes;

/// <summary>display.refreshRate against a fake display configuration: apply, read back, restore, refuse.</summary>
public sealed class RefreshRateModuleTests
{
    private const string Id = @"\\?\DISPLAY#ABC1234#5&1a2b3c&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Key => MonitorTargets.Key(Id, "refresh");

    [Fact]
    public async Task A_supported_rate_is_written_read_back_and_the_baseline_restores()
    {
        var display = new FakeDisplay();
        var module = new RefreshRateModule(display);
        var intent = Assert.Single(module.Expand(Settings(60)));
        Assert.Equal(Key, intent.Key);

        var baseline = await module.ReadAsync(Key, Ct);
        var applied = await module.ApplyAsync(intent, Ct);
        Assert.True(applied.Ok, applied.Error);
        Assert.Equal(60, applied.ReadBack!.GetValue<int>());
        Assert.Equal(60, display.Hz);

        var restored = await module.ApplyAsync(new ResourceIntent(Key, baseline.Value), Ct);
        Assert.True(restored.Ok, restored.Error);
        Assert.Equal(144, display.Hz);
        Assert.Equal([60, 144], display.Writes);
    }

    [Fact]
    public async Task A_rate_the_monitor_does_not_offer_is_refused_without_a_write()
    {
        var display = new FakeDisplay();
        var r = await new RefreshRateModule(display).ApplyAsync(new ResourceIntent(Key, 75), Ct);
        Assert.False(r.Ok);
        Assert.Contains("does not offer 75 Hz", r.Error, StringComparison.Ordinal);
        Assert.Empty(display.Writes);
    }

    [Fact]
    public async Task Applying_the_rate_it_already_has_writes_nothing()
    {
        var display = new FakeDisplay();
        var r = await new RefreshRateModule(display).ApplyAsync(new ResourceIntent(Key, 144), Ct);
        Assert.True(r.Ok);
        Assert.Empty(display.Writes);
    }

    [Fact]
    public async Task The_read_back_is_what_the_monitor_holds_not_the_target()
    {
        // The driver accepts the call but keeps the old rate: the module reports the truth for the reconciler to compare.
        var display = new FakeDisplay { IgnoreWrites = true };
        var module = new RefreshRateModule(display);
        var r = await module.ApplyAsync(new ResourceIntent(Key, 60), Ct);
        Assert.True(r.Ok);
        Assert.Equal(144, r.ReadBack!.GetValue<int>());
        Assert.False(module.Equivalent(Key, JsonValue.Create(60), r.ReadBack));
    }

    [Fact]
    public async Task A_failed_write_is_a_failure()
    {
        var display = new FakeDisplay { WriteError = "ChangeDisplaySettingsEx refused" };
        var r = await new RefreshRateModule(display).ApplyAsync(new ResourceIntent(Key, 60), Ct);
        Assert.False(r.Ok);
        Assert.Equal("ChangeDisplaySettingsEx refused", r.Error);
    }

    [Fact]
    public async Task A_disconnected_monitor_or_an_unreadable_configuration_is_a_failed_read_never_a_value()
    {
        var display = new FakeDisplay();
        var module = new RefreshRateModule(display);
        Assert.False((await module.ReadAsync(MonitorTargets.Key("other", "refresh"), Ct)).Ok);
        display.Unreadable = true;
        Assert.False((await module.ReadAsync(Key, Ct)).Ok);
        Assert.False((await module.ApplyAsync(new ResourceIntent(Key, 60), Ct)).Ok);
        Assert.Empty(display.Writes);
        Assert.False((await module.AvailableAsync(Ct)).Available);
        Assert.False((await module.DiscoverAsync(Ct)).ContainsKey("monitors"));
    }

    [Fact]
    public async Task Discovery_lists_each_monitor_by_its_stable_id_with_its_rates()
    {
        var lists = await new RefreshRateModule(new FakeDisplay()).DiscoverAsync(Ct);
        var item = Assert.Single(lists["monitors"]);
        Assert.Equal(Id, item.Id);
        Assert.Contains("60, 120, 144", item.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_refuses_a_monitor_listed_twice_and_an_id_with_a_slash()
    {
        var module = new RefreshRateModule(new FakeDisplay());
        var twice = JsonNode.Parse($$"""{ "monitors": [ { "monitor": "a", "hz": 60 }, { "monitor": "A", "hz": 144 } ] }""")!.AsObject();
        var slash = JsonNode.Parse($$"""{ "monitors": [ { "monitor": "a/b", "hz": 60 } ] }""")!.AsObject();
        Assert.Single(module.Validate(twice));
        Assert.Single(module.Validate(slash));
        Assert.Empty(module.Validate(Settings(60)));
    }

    private static JsonObject Settings(int hz) =>
        new() { ["monitors"] = new JsonArray(new JsonObject { ["monitor"] = Id, ["hz"] = hz }) };

    private sealed class FakeDisplay : IDisplayConfig
    {
        public int Hz { get; private set; } = 144;
        public bool Unreadable { get; set; }
        public bool IgnoreWrites { get; set; }
        public string? WriteError { get; init; }
        public List<int> Writes { get; } = [];

        public IReadOnlyList<DisplayMonitor>? Monitors() =>
            Unreadable ? null : [new DisplayMonitor(Id, "Test monitor", 2560, 1440, Hz, [60, 120, 144])];

        public string? SetRefreshRate(string monitorId, int hz)
        {
            if (WriteError is not null) return WriteError;
            Writes.Add(hz);
            if (!IgnoreWrites) Hz = hz;
            return null;
        }
    }
}
