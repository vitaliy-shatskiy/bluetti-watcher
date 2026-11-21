using BluettiWatcher.Modes;
using BluettiWatcher.PowerMode;

namespace BluettiWatcher.Tests.Modes;

/// <summary>The built-in modes a fresh or a migrated install starts with pass the same validation as a mode the API takes.</summary>
public sealed class BuiltInModesValidationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bw-builtin-valid-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuiltInModes_FreshOrMigratedWithGpuCap_PassTheApiValidationWithTheProductionModules(bool migrated)
    {
        var cfg = new StationConfig { DataDir = _dir, PowerMode = new PowerModeConfig { FlagPath = Path.Combine(_dir, "flag", "power-mode.json") } };
        if (migrated)
        {
            Directory.CreateDirectory(Path.Combine(_dir, "flag"));
            File.WriteAllText(ReturnPlanFile.PathOf(cfg.FlagFile), """{ "returnPlan": "balanced", "gpuCap": true }""");
        }
        var registry = new ModuleRegistry(ModuleCatalog.Production(cfg));
        var settings = migrated ? BuiltInModes.Migrate(cfg) : BuiltInModes.Defaults(180, 180);

        var problems = settings.Modes.SelectMany(m => ModesValidation.Mode(m, registry).Select(p => $"{m.Id}: {p}")).ToList();

        Assert.Empty(problems);
        Assert.Equal(migrated, settings.Modes.Single(m => m.Id == BuiltInModes.Eco).Actions.Single(a => a.TypeId == BuiltInModes.GpuLimitType).Enabled);
    }

    [Fact]
    public void Defaults_Normal_SetsTheBalancedPlan()
    {
        // leaving Eco always ends on Balanced, even when the plan was changed by hand during Eco
        var normal = BuiltInModes.Defaults(180, 180).Modes.Single(m => m.Id == BuiltInModes.Normal);

        var plan = Assert.Single(normal.Actions);
        Assert.Equal((BuiltInModes.PowerPlanType, true, PowerModeSwitch.BalancedPlan), (plan.TypeId, plan.Enabled, plan.Settings["plan"]?.GetValue<string>()));
    }
}
