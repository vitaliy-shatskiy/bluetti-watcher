using BluettiWatcher.Http;
using BluettiWatcher.Modes;

namespace BluettiWatcher.Tests.Modes;

public sealed class ModesWiringTests
{
    [Fact]
    public void Validate_ModeIdAuto_IsReserved()
    {
        var mode = new Mode { Id = "auto", Name = "Auto", FlagWord = FlagWord.Normal, Actions = [] };
        Assert.Contains("id: auto is reserved (it selects the Auto rules)", ModesValidation.Mode(mode, new ModuleRegistry([])));
    }

    [Theory]
    [InlineData("/api/modes", 64 * 1024)]
    [InlineData("/api/modes/rules", 64 * 1024)]
    [InlineData("/api/settings/hibernate", 64 * 1024)]
    [InlineData("/api/mode", 4 * 1024)]
    [InlineData("/api/settings/notifications", 4 * 1024)]
    public void MaxBodyFor_ModesRoutes_Take64Kb(string path, int limit) => Assert.Equal(limit, MiniHttpServer.MaxBodyFor(path));

    [Fact]
    public void StationConfig_ModesCommands_ParseFromConfigJson()
    {
        var c = System.Text.Json.JsonSerializer.Deserialize("""
            { "modes": { "commands": { "pause": { "exe": "C:\\b\\tool.exe", "args": ["pause", "--all"], "timeoutSec": 45 } } } }
            """, StationJson.Default.StationConfig)!;
        Assert.Equal(ModesConfig.V2, c.Modes.Engine);
        var cmd = c.Modes.Commands["pause"];
        Assert.Equal("C:\\b\\tool.exe", cmd.Exe);
        Assert.Equal(["pause", "--all"], cmd.Args);
        Assert.Equal(45, cmd.TimeoutSec);
    }
}
