using System.Text.Json.Nodes;
using BluettiWatcher.Modes;
using BluettiWatcher.PowerMode;

namespace BluettiWatcher.Tests.Modes;

internal sealed class FakeFlagWriter : IFlagWordWriter
{
    public List<FlagWord> Written { get; } = [];
    public string Fail { get; set; } = "";

    public Task<string> WriteAsync(FlagWord word, CancellationToken ct)
    {
        if (Fail.Length == 0) Written.Add(word);
        return Task.FromResult(Fail);
    }
}

public sealed class ModesStorageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bw-modes-store-").FullName;
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeMachine _m = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string JournalPath => Path.Combine(_dir, ModeJournal.FileName);

    private StationConfig Config() => new() { DataDir = _dir, PowerMode = new PowerModeConfig { FlagPath = Path.Combine(_dir, "flag", "power-mode.json") } };

    private (ModesEngine Engine, FakeFlagWriter Flag) Engine()
    {
        var flag = new FakeFlagWriter();
        var reg = new ModuleRegistry([new FakeModule(BuiltInModes.PowerPlanType, _m)]);
        var e = new ModesEngine(Config(), reg, new ModesStore(_dir), new ModeJournal(JournalPath), flag, _clock, _ => { });
        e.Start(_clock.Now.AddDays(-1));
        return (e, flag);
    }

    [Fact]
    public void Load_TornLastLine_IsCutAndIgnored()
    {
        File.WriteAllText(JournalPath, """{"seq":1,"at":"2026-11-20T12:00:00+00:00","kind":"mode","modeId":"eco"}""" + "\n" + """{"seq":2,"at":"2026-""");
        var j = new ModeJournal(JournalPath);

        var load = j.Load();

        Assert.Null(load.Problem);
        Assert.Single(load.Records);
        Assert.Equal(2, j.Append(new JournalRecord { Kind = JournalKind.ModeEntered, ModeId = "x" }).Seq);
        Assert.Equal(2, new ModeJournal(JournalPath).Load().Records.Count);   // the appended line is not glued to the torn one
    }

    [Fact]
    public void Load_CorruptCompleteLine_IsAProblemAndKeepsNothing()
    {
        File.WriteAllText(JournalPath, "not json\n" + """{"seq":2,"at":"2026-11-20T12:00:00+00:00","kind":"mode","modeId":"eco"}""" + "\n");
        var load = new ModeJournal(JournalPath).Load();
        Assert.StartsWith("journal line 1 unreadable", load.Problem, StringComparison.Ordinal);
        Assert.Empty(load.Records);
    }

    [Fact]
    public async Task Start_CorruptJournal_HoldsFallbackAndRestoresNothing()
    {
        await File.WriteAllTextAsync(JournalPath, """{"seq":1,"at":"2026-11-20T12:00:00+00:00","kind":"prepared","modeId":"eco","typeId":"windows.powerPlan","key":"plan","before":"high","value":"saver"}""" + "\nbroken\n", TestContext.Current.CancellationToken);
        _m.Values["plan"] = "saver";

        var (e, _) = Engine();
        await e.StepAsync(new SelectorInput(false, TimeSpan.FromHours(1), 50, null, _clock.Now), false, TestContext.Current.CancellationToken);

        Assert.Single(e.Problems);
        Assert.Equal(new Selection { Kind = SelectionKind.Manual, ManualModeId = "normal" }, e.Settings.Selection);
        Assert.Equal("normal", e.Snapshot.ModeId);   // mains off for an hour, yet nothing switches on its own
        Assert.Empty(_m.Writes);
        Assert.True(File.Exists(JournalPath + ".corrupt-" + _clock.Now.ToUnixTimeSeconds()));
    }

    [Fact]
    public async Task Step_MainsOff_WritesEcoFlagWordOnce()
    {
        var (e, flag) = Engine();
        await e.StepAsync(new SelectorInput(false, TimeSpan.FromSeconds(200), 50, null, _clock.Now), false, TestContext.Current.CancellationToken);
        await e.StepAsync(new SelectorInput(false, TimeSpan.FromSeconds(260), 50, null, _clock.Now), false, TestContext.Current.CancellationToken);
        Assert.Equal([FlagWord.Eco], flag.Written);
        Assert.Equal("\"" + PowerModeSwitch.SaverPlan + "\"", _m.Text("plan"));   // the fake plan key: settings carry plan, not key
    }

    [Fact]
    public async Task Step_Emergency_LeavesTheFlagAlone()
    {
        var (e, flag) = Engine();
        await e.StepAsync(new SelectorInput(false, TimeSpan.FromSeconds(200), 15, null, _clock.Now), true, TestContext.Current.CancellationToken);
        Assert.Empty(flag.Written);
    }

    [Fact]
    public async Task Select_ManualThenAuto_IsSavedAndApplied()
    {
        var (e, flag) = Engine();
        Assert.Equal((0, ""), await e.SelectAsync("eco", TestContext.Current.CancellationToken));
        Assert.Equal("eco", e.Snapshot.ModeId);
        Assert.Equal("eco", new ModesStore(_dir).Load(() => throw new InvalidOperationException(), _clock.Now).Settings.Selection.ManualModeId);
        Assert.Equal(404, (await e.SelectAsync("nope", TestContext.Current.CancellationToken)).Status);
        Assert.Equal((0, ""), await e.SelectAsync("auto", TestContext.Current.CancellationToken));
        Assert.Equal([FlagWord.Eco, FlagWord.Normal], flag.Written);
    }

    [Fact]
    public async Task DeleteMode_BuiltIn_IsRefused()
    {
        var (e, _) = Engine();
        Assert.Equal(409, (await e.DeleteModeAsync("eco", TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task PutMode_BadSettings_IsRefusedWithTheField()
    {
        var (e, _) = Engine();
        var bad = new Mode { Id = "quiet", Name = "Quiet", Actions = [new ModeAction { TypeId = BuiltInModes.PowerPlanType, Settings = new JsonObject { ["key"] = 1 } }] };
        var (status, error) = await e.PutModeAsync(bad, create: true, TestContext.Current.CancellationToken);
        Assert.Equal(400, status);
        Assert.Contains("actions[0].settings.key", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_NewerVersion_IsLeftAloneAndDefaultsUsed()
    {
        File.WriteAllText(Path.Combine(_dir, ModesStore.FileName), """{"version":99,"modes":[]}""");
        var store = new ModesStore(_dir);
        var (s, problem) = store.Load(() => BuiltInModes.Defaults(180, 180), _clock.Now);
        Assert.Contains("newer", problem, StringComparison.Ordinal);
        Assert.False(store.Writable);
        Assert.Equal(3, s.Modes.Count);
        Assert.Contains("99", File.ReadAllText(Path.Combine(_dir, ModesStore.FileName)), StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MissingBuiltIn_IsRestored()
    {
        File.WriteAllText(Path.Combine(_dir, ModesStore.FileName), """{"version":1,"modes":[{"id":"normal","name":"Normal"}],"fallbackModeId":"gone"}""");
        var (s, problem) = new ModesStore(_dir).Load(() => throw new InvalidOperationException(), _clock.Now);
        Assert.Null(problem);
        Assert.Equal(["normal", "eco", "max"], s.Modes.Select(m => m.Id));
        Assert.Equal("normal", s.FallbackModeId);
    }

    [Theory]
    [InlineData("balanced", false, PowerModeSwitch.BalancedPlan)]
    [InlineData("previous", true, null)]
    public void Migrate_SwitchConfig_BecomesBuiltInSettings(string returnPlan, bool gpuCap, string? normalPlan)
    {
        var c = Config();
        Directory.CreateDirectory(Path.GetDirectoryName(c.FlagFile)!);
        File.WriteAllText(ReturnPlanFile.PathOf(c.FlagFile), $$"""{ "returnPlan": "{{returnPlan}}", "gpuCap": {{(gpuCap ? "true" : "false")}} }""");
        File.WriteAllText(Path.Combine(_dir, "no-hibernate"), "");
        c.MainsLostSec = 240;

        Assert.True(BuiltInModes.HasLegacySettings(c));
        var s = BuiltInModes.Migrate(c);

        var normal = s.Modes.Single(m => m.Id == "normal");
        Assert.Equal(normalPlan, normal.Actions.SingleOrDefault()?.Settings["plan"]?.GetValue<string>());
        Assert.Equal(gpuCap, s.Modes.Single(m => m.Id == "eco").Actions.Single(a => a.TypeId == BuiltInModes.GpuLimitType).Enabled);
        Assert.Equal(240, s.Rules[0].Condition.Seconds);
        Assert.False(s.AutoHibernate);
    }
}
