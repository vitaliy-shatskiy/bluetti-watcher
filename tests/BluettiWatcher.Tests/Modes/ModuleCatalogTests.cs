using BluettiWatcher.Modes;
using BluettiWatcher.Modes.Modules;

namespace BluettiWatcher.Tests.Modes;

public sealed class ModuleCatalogTests
{
    [Fact]
    public void Production_RegistersEveryModule()
    {
        var ids = ModuleCatalog.Production(new StationConfig()).Select(m => m.Descriptor.TypeId).Order(StringComparer.Ordinal);
        Assert.Equal(
        [
            "command.pair", "display.brightness", "display.offTimeout", "display.refreshRate", "gpu.powerLimit",
            "nvidia.profile", "process.close", "rgb.lighting", "scheduledTask.state", "service.state", "windows.cpuPolicy", "windows.phoneLink", "windows.powerPlan",
        ], ids);
    }

    [Fact]
    public void Commands_ConfigAndMigratedActions_AreNamedCommands()
    {
        var config = new StationConfig
        {
            Actions = new() { ["eco"] = "\"C:\\Tools\\backup tool.exe\" pause --all", ["normal"] = "C:\\Tools\\resume.exe" },
            Modes = new ModesConfig { Commands = new() { ["unload"] = new ModeCommandConfig { Exe = "C:\\llm\\ctl.exe", Args = ["unload"], TimeoutSec = 20 } } },
        };
        var commands = ModuleCatalog.Commands(config);
        Assert.Equal(["eco", "normal", "unload"], commands.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("C:\\Tools\\backup tool.exe", commands["eco"].Exe);
        Assert.Equal(["pause", "--all"], commands["eco"].Args);
        Assert.Empty(commands["normal"].Args);
        Assert.Equal(["unload"], commands["unload"].Args);
        Assert.Equal(20, commands["unload"].TimeoutSec);
    }

    [Fact]
    public void Commands_ConfiguredNameWinsOverTheMigratedAction()
    {
        var config = new StationConfig
        {
            Actions = new() { ["eco"] = "old.exe" },
            Modes = new ModesConfig { Commands = new() { ["eco"] = new ModeCommandConfig { Exe = "new.exe" } } },
        };
        Assert.Equal("new.exe", ModuleCatalog.Commands(config)["eco"].Exe);
    }

    [Theory]
    [InlineData("a.exe x y", new[] { "x", "y" })]
    [InlineData("a.exe \"two words\" z", new[] { "two words", "z" })]
    [InlineData("a.exe \"say \\\"hi\\\"\"", new[] { "say \"hi\"" })]
    [InlineData("a.exe C:\\dir\\ \"C:\\other dir\\\\\"", new[] { "C:\\dir\\", "C:\\other dir\\" })]
    [InlineData("a.exe", new string[0])]
    public void SplitArguments_FollowsTheWindowsRules(string line, string[] expected)
    {
        var (_, args) = CommandLineArgs.Split(line);
        Assert.Equal(expected, args);
    }

    [Fact]
    public void Migrate_ActionsEco_BecomesTheEcoCommandPair()
    {
        string dir = Path.Combine(Path.GetTempPath(), "bluetti-catalog-test-" + Guid.NewGuid().ToString("N"));
        var config = new StationConfig { DataDir = dir, FlagPath = Path.Combine(dir, "power-mode.json"), Actions = new() { ["eco"] = "pause.exe", ["normal"] = "resume.exe" } };
        var eco = BuiltInModes.Migrate(config).Modes.Single(m => m.Id == BuiltInModes.Eco);
        var pair = eco.Actions.Single(a => a.TypeId == CommandPairModule.Id && a.Enabled);
        Assert.Equal("eco", (string?)pair.Settings["enter"]);
        Assert.Equal("normal", (string?)pair.Settings["leave"]);
    }
}
