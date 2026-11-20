using System.Text.RegularExpressions;
using BluettiWatcher.Modes.Broker;

namespace BluettiWatcher.Tests.Modes;

/// <summary>Every broker pattern runs under a time-out, and a match that runs out of time refuses instead of passing.</summary>
public sealed class BrokerRegexTimeoutTests
{
    public static TheoryData<string> Patterns => ["sid", "service", "profile", "task", "gpu", "file", "operation"];

    private static Regex Pattern(string name) => name switch
    {
        "sid" => BrokerPolicy.SidPattern(),
        "service" => BrokerPolicy.ServiceNamePattern(),
        "profile" => BrokerPolicy.ProfileNamePattern(),
        "task" => BrokerPolicy.TaskNamePattern(),
        "gpu" => BrokerCore.GpuIdPattern(),
        "file" => BrokerRequest.FileNamePattern(),
        _ => BrokerRequest.OperationPattern(),
    };

    [Theory]
    [MemberData(nameof(Patterns))]
    public void BrokerPattern_Any_HasAFiniteMatchTimeout(string name) =>
        Assert.Equal(TimeSpan.FromMilliseconds(BrokerPolicy.MatchTimeoutMs), Pattern(name).MatchTimeout);

    [Fact]
    public void Matches_InputThatRunsOutOfTime_IsNoMatch()
    {
        // A backtracking pattern on input it cannot match: far past a millisecond.
        var slow = new Regex("^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(1));

        Assert.False(BrokerPolicy.Matches(slow, new string('a', 5000) + "!"));
        Assert.True(BrokerPolicy.Matches(slow, "aaa"));
    }
}
