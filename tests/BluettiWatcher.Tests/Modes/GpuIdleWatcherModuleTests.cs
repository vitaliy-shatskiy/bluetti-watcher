using System.Text.Json.Nodes;
using BluettiWatcher.Modes;
using BluettiWatcher.Modes.Modules;

namespace BluettiWatcher.Tests.Modes;

/// <summary>gpu.idleWatcher against a fake nvidia-smi and a manual clock: logs the GPU holders once per idle stretch.</summary>
public sealed class GpuIdleWatcherModuleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Key = GpuIdleWatcherModule.ResourceKey;
    private static readonly GpuSample IdleP0 = new(0, "P0", 1965, 52.4, 0);
    private static readonly GpuSample Busy = new(0, "P0", 1965, 180, 60);

    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero));
    private readonly FakeSmi _smi = new();
    private readonly List<(DateTimeOffset At, string Kind, string Detail)> _events = [];
    private readonly List<string> _log = [];

    private GpuIdleWatcherModule NewModule() => new(_smi, _clock, (at, kind, detail) => _events.Add((at, kind, detail)), _log.Add);

    private async Task<GpuIdleWatcherModule> ArmedAsync(int minutes = 5)
    {
        var module = NewModule();
        var settings = JsonNode.Parse($$"""{ "minutes": {{minutes}}, "minClockMhz": 1000, "maxUtilization": 2 }""")!.AsObject();
        var r = await module.ApplyAsync(Assert.Single(module.Expand(settings)), Ct);
        Assert.True(r.Ok);
        return module;
    }

    private async Task SampleAtAsync(GpuIdleWatcherModule module, int minute, GpuSample? sample)
    {
        _clock.Now = new DateTimeOffset(2026, 11, 20, 10, minute, 0, TimeSpan.Zero);
        _smi.Next = sample is null ? null : [sample];
        await module.SampleOnceAsync(Ct);
    }

    [Fact]
    public async Task A_GPU_idle_at_P0_for_the_set_minutes_is_logged_once_with_its_processes()
    {
        var module = await ArmedAsync();
        for (int minute = 0; minute <= 8; minute++) await SampleAtAsync(module, minute, IdleP0);

        var e = Assert.Single(_events);
        Assert.Equal(GpuIdleWatcherModule.EventKind, e.Kind);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 10, 5, 0, TimeSpan.Zero), e.At);
        Assert.Contains("GPU 0 in P0 at 1965 MHz, 52 W, 0 % busy for 5 min", e.Detail, StringComparison.Ordinal);
        Assert.Contains("dwm.exe (pid 1234, C+G)", e.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("other.exe", e.Detail, StringComparison.Ordinal);
        Assert.Single(_log, l => l.Contains("holding it", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Work_on_the_GPU_breaks_the_stretch_and_a_new_stretch_is_logged_again()
    {
        var module = await ArmedAsync();
        await SampleAtAsync(module, 0, IdleP0);
        await SampleAtAsync(module, 4, IdleP0);
        await SampleAtAsync(module, 5, Busy);
        await SampleAtAsync(module, 9, IdleP0);
        Assert.Empty(_events);
        await SampleAtAsync(module, 14, IdleP0);
        Assert.Single(_events);
    }

    [Fact]
    public async Task A_failed_sample_breaks_the_stretch_instead_of_counting_as_idle()
    {
        var module = await ArmedAsync();
        await SampleAtAsync(module, 0, IdleP0);
        await SampleAtAsync(module, 3, null);
        await SampleAtAsync(module, 6, IdleP0);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task A_low_clock_or_a_lower_p_state_is_not_the_idle_at_P0_case()
    {
        var module = await ArmedAsync(minutes: 1);
        await SampleAtAsync(module, 0, IdleP0 with { ClockMhz = 210 });
        await SampleAtAsync(module, 2, IdleP0 with { ClockMhz = 210 });
        await SampleAtAsync(module, 3, IdleP0 with { PState = "P8" });
        await SampleAtAsync(module, 5, IdleP0 with { PState = "P8" });
        Assert.Empty(_events);
    }

    [Fact]
    public async Task Disarming_stops_the_watch_and_reads_back_unarmed()
    {
        var module = await ArmedAsync();
        var baseline = new JsonObject { ["armed"] = false };
        var r = await module.ApplyAsync(new ResourceIntent(Key, baseline), Ct);
        Assert.True(r.Ok);
        Assert.True(module.Equivalent(Key, baseline, r.ReadBack));
        for (int minute = 0; minute <= 8; minute++) await SampleAtAsync(module, minute, IdleP0);
        Assert.Empty(_events);
        Assert.Equal(0, _smi.Samples);
    }

    [Fact]
    public async Task Before_arming_the_read_is_unarmed_and_after_it_matches_the_target()
    {
        var module = NewModule();
        var before = await module.ReadAsync(Key, Ct);
        Assert.False(before.Value!["armed"]!.GetValue<bool>());
        var intent = Assert.Single(module.Expand(module.Descriptor.DefaultSettings));
        Assert.False(module.Equivalent(Key, intent.Target, before.Value));
        var r = await module.ApplyAsync(intent, Ct);
        Assert.True(module.Equivalent(Key, intent.Target, r.ReadBack));
        await module.ApplyAsync(new ResourceIntent(Key, before.Value), Ct);
    }

    [Fact]
    public void The_csv_query_parses_and_a_malformed_row_is_no_reading()
    {
        var ok = NvidiaSmiSampler.ParseSamples(["0, P0, 1965, 52.31, 0", "1, P8, 210, [N/A], 3"]);
        Assert.Equal([new GpuSample(0, "P0", 1965, 52.31, 0), new GpuSample(1, "P8", 210, null, 3)], ok);
        Assert.Null(NvidiaSmiSampler.ParseSamples(["0, P0, 1965, 52.31"]));
        Assert.Null(NvidiaSmiSampler.ParseSamples(["NVIDIA-SMI has failed because it couldn't communicate with the NVIDIA driver."]));
    }

    [Fact]
    public void The_process_table_of_the_plain_report_parses_compute_and_graphics_rows()
    {
        string[] report =
        [
            "+-----------------------------------------------------------------------------------------+",
            "| Processes:                                                                              |",
            "|  GPU   GI   CI        PID   Type   Process name                              GPU Memory |",
            "|        ID   ID                                                               Usage      |",
            "|=========================================================================================|",
            @"|    0   N/A  N/A      1234    C+G   C:\Windows\System32\dwm.exe               N/A      |",
            @"|    0   N/A  N/A      5678      C   ...\Programs\Some App\app.exe             512MiB   |",
            "+-----------------------------------------------------------------------------------------+",
        ];
        Assert.Equal(
            [new GpuProcess(0, 1234, "C+G", @"C:\Windows\System32\dwm.exe"), new GpuProcess(0, 5678, "C", @"...\Programs\Some App\app.exe")],
            NvidiaSmiSampler.ParseProcesses(report));
    }

    private sealed class FakeSmi : IGpuSampler
    {
        public IReadOnlyList<GpuSample>? Next { get; set; }
        public int Samples { get; private set; }

        public Task<IReadOnlyList<GpuSample>?> SampleAsync(CancellationToken ct)
        {
            Samples++;
            return Task.FromResult(Next);
        }

        public Task<IReadOnlyList<GpuProcess>?> ProcessesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GpuProcess>?>([new GpuProcess(0, 1234, "C+G", @"C:\Windows\System32\dwm.exe"), new GpuProcess(1, 99, "G", "other.exe")]);
    }
}
