using BluettiWatcher.Modes.Broker;
using BluettiWatcher.Modes.Modules;

namespace BluettiWatcher.Tests.Modes;

/// <summary>The service module and the broker read a start type through one reader, so a set start type reads back by its own name.</summary>
public sealed class ServiceStartTypeTests
{
    [Fact]
    public void StartTypeName_EveryStartTypeTheBrokerSets_ReadsBackAsTheSameName()
    {
        foreach (var (name, (start, delayed)) in ScmServiceControl.StartTypes)
            Assert.Equal(name, WindowsServiceReader.StartTypeName((int)start, delayed));
    }
}
