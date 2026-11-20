using System.Text.Json.Nodes;
using BluettiWatcher.Modes;
using BluettiWatcher.Modes.Modules;

namespace BluettiWatcher.Tests.Modes;

/// <summary>display.brightness against fake DDC/CI monitors: percent of the monitor's maximum, read back, restore.</summary>
public sealed class BrightnessModuleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string KeyA = MonitorTargets.Key("mon-a", "brightness");
    private static readonly string KeyNoDdc = MonitorTargets.Key("mon-tv", "brightness");

    [Fact]
    public async Task A_percent_is_written_read_back_and_the_baseline_restores()
    {
        var ddc = new FakeDdc();
        var module = new BrightnessModule(ddc);
        var intent = Assert.Single(module.Expand(JsonNode.Parse("""{ "monitors": [ { "monitor": "mon-a", "percent": 30 } ] }""")!.AsObject()));

        var baseline = await module.ReadAsync(intent.Key, Ct);
        Assert.Equal(80, baseline.Value!.GetValue<int>());
        var applied = await module.ApplyAsync(intent, Ct);
        Assert.True(applied.Ok, applied.Error);
        Assert.Equal(30, applied.ReadBack!.GetValue<int>());

        var restored = await module.ApplyAsync(new ResourceIntent(intent.Key, baseline.Value), Ct);
        Assert.True(restored.Ok, restored.Error);
        Assert.Equal(80, ddc.Raw["mon-a"]);
    }

    [Fact]
    public async Task A_monitor_whose_maximum_is_not_100_gets_the_scaled_raw_value()
    {
        var ddc = new FakeDdc();
        ddc.Max["mon-a"] = 50;
        ddc.Raw["mon-a"] = 50;
        var r = await new BrightnessModule(ddc).ApplyAsync(new ResourceIntent(KeyA, 40), Ct);
        Assert.True(r.Ok, r.Error);
        Assert.Equal([20], ddc.Writes);
        Assert.Equal(40, r.ReadBack!.GetValue<int>());
    }

    [Fact]
    public async Task The_current_brightness_is_not_written_again()
    {
        var ddc = new FakeDdc();
        var r = await new BrightnessModule(ddc).ApplyAsync(new ResourceIntent(KeyA, 80), Ct);
        Assert.True(r.Ok);
        Assert.Empty(ddc.Writes);
    }

    [Fact]
    public async Task A_monitor_without_DDC_CI_is_a_failed_read_and_a_refused_write()
    {
        var ddc = new FakeDdc();
        var module = new BrightnessModule(ddc);
        Assert.False((await module.ReadAsync(KeyNoDdc, Ct)).Ok);
        Assert.False((await module.ApplyAsync(new ResourceIntent(KeyNoDdc, 50), Ct)).Ok);
        Assert.Empty(ddc.Writes);
        var item = Assert.Single((await module.DiscoverAsync(Ct))["monitors"], m => m.Id == "mon-tv");
        Assert.Contains("no DDC/CI", item.Label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_monitor_answering_DDC_CI_makes_the_module_unavailable()
    {
        var ddc = new FakeDdc();
        Assert.True((await new BrightnessModule(ddc).AvailableAsync(Ct)).Available);
        ddc.Max.Remove("mon-a");
        var a = await new BrightnessModule(ddc).AvailableAsync(Ct);
        Assert.False(a.Available);
        Assert.Contains("DDC/CI", a.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_write_is_a_failure()
    {
        var ddc = new FakeDdc { WriteError = "the monitor refused" };
        var r = await new BrightnessModule(ddc).ApplyAsync(new ResourceIntent(KeyA, 10), Ct);
        Assert.False(r.Ok);
        Assert.Equal("the monitor refused", r.Error);
    }

    [Fact]
    public void One_percent_of_rounding_is_the_same_brightness_two_is_not()
    {
        var module = new BrightnessModule(new FakeDdc());
        Assert.True(module.Equivalent(KeyA, JsonValue.Create(40), JsonValue.Create(41)));
        Assert.False(module.Equivalent(KeyA, JsonValue.Create(40), JsonValue.Create(42)));
    }

    private sealed class FakeDdc : IDdcMonitors
    {
        public Dictionary<string, int> Raw { get; } = new() { ["mon-a"] = 80 };
        public Dictionary<string, int> Max { get; } = new() { ["mon-a"] = 100 };
        public List<int> Writes { get; } = [];
        public string? WriteError { get; init; }

        public Task<IReadOnlyList<DdcMonitor>?> MonitorsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DdcMonitor>?>(
                [new DdcMonitor("mon-a", "Desk", Max.ContainsKey("mon-a")), new DdcMonitor("mon-tv", "TV", false, "no answer")]);

        public Task<DdcBrightness> ReadBrightnessAsync(string monitorId, CancellationToken ct) =>
            Task.FromResult(Max.TryGetValue(monitorId, out int max) ? new DdcBrightness(Raw[monitorId], max) : DdcBrightness.Failed("no DDC/CI"));

        public Task<string?> WriteBrightnessAsync(string monitorId, int raw, CancellationToken ct)
        {
            if (WriteError is not null) return Task.FromResult<string?>(WriteError);
            Writes.Add(raw);
            Raw[monitorId] = raw;
            return Task.FromResult<string?>(null);
        }
    }
}
