using BluettiWatcher.Modes.Broker;

namespace BluettiWatcher.Tests.Modes;

/// <summary>SYSTEM runs an exe only from Program Files: never from the broker's exchange folder, where the user adds files.</summary>
public sealed class BrokerAdminLocationTests
{
    private static readonly BrokerPaths Paths = BrokerPaths.Default;

    [Fact]
    public void AdminOnlyLocation_FileInTheRequestsFolder_IsRefused() =>
        Assert.False(BrokerCore.AdminOnlyLocation(Path.Combine(Paths.Requests, "inspector.exe"), Paths));

    [Fact]
    public void AdminOnlyLocation_ExchangeInsideTheBrokerFolder_ItsFilesAreRefused()
    {
        var shared = new BrokerPaths(Paths.Root, Paths.Root);

        Assert.False(BrokerCore.AdminOnlyLocation(Path.Combine(shared.Requests, "inspector.exe"), shared));
        Assert.True(BrokerCore.AdminOnlyLocation(Path.Combine(shared.Root, "bin", "inspector.exe"), Paths));
    }

    [Fact]
    public void AdminOnlyLocation_FileInTheOldBrokerFolderUnderProgramData_IsRefused() =>
        Assert.False(BrokerCore.AdminOnlyLocation(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BluettiWatcher", "broker", "bin", "x.exe"), Paths));

    [Fact]
    public void AdminOnlyLocation_FileUnderProgramFiles_IsAllowed() =>
        Assert.True(BrokerCore.AdminOnlyLocation(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Inspector", "inspector.exe"), Paths));
}
