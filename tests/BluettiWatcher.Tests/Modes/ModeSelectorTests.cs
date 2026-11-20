using BluettiWatcher.Modes;

namespace BluettiWatcher.Tests.Modes;

public sealed class ModeSelectorTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 20, 12, 0, 0, TimeSpan.FromHours(2));
    private static readonly ModesSettings Defaults = BuiltInModes.Defaults(180, 180);

    private static SelectorInput Mains(bool on, int seconds, double? soc = 50) => new(on, TimeSpan.FromSeconds(seconds), soc, null, At);

    private static ModesSettings With(ModesSettings s, Selection sel) => s with { Selection = sel };

    [Theory]
    [InlineData(false, 179, "normal")]
    [InlineData(false, 180, "eco")]
    public void Decide_DefaultRules_EcoAfterMainsOffFor180s(bool on, int seconds, string mode) =>
        Assert.Equal(mode, ModeSelector.Decide(Defaults, Mains(on, seconds)).ModeId);

    [Fact]
    public void Decide_MainsBackUnder180s_EcoIsKept()
    {
        var inEco = ModeSelector.Decide(Defaults, Mains(false, 600));
        var d = ModeSelector.Decide(With(Defaults, inEco.Selection), Mains(true, 179));
        Assert.Equal("eco", d.ModeId);
        Assert.Equal("normal", ModeSelector.Decide(With(Defaults, d.Selection), Mains(true, 180)).ModeId);
    }

    [Fact]
    public void Decide_ManualPick_HoldsUntilAuto()
    {
        var manual = With(Defaults, new Selection { Kind = SelectionKind.Manual, ManualModeId = "max" });
        Assert.Equal("max", ModeSelector.Decide(manual, Mains(false, 3600)).ModeId);
        Assert.Equal("eco", ModeSelector.Decide(manual with { Selection = manual.Selection with { Kind = SelectionKind.Auto } }, Mains(false, 3600)).ModeId);
    }

    [Fact]
    public void Decide_ManualModeDeleted_FallsBackToAuto()
    {
        var gone = With(Defaults, new Selection { Kind = SelectionKind.Manual, ManualModeId = "deleted" });
        var d = ModeSelector.Decide(gone, Mains(true, 10));
        Assert.Equal("normal", d.ModeId);
        Assert.Equal(SelectionKind.Auto, d.Selection.Kind);
    }

    [Fact]
    public void Decide_TwoRulesMatch_FirstWins()
    {
        var s = Defaults with
        {
            Rules =
            [
                new Rule { Condition = new RuleCondition { Kind = ConditionKind.SocBelow, Percent = 40 }, ModeId = "max" },
                new Rule { Condition = new RuleCondition { Kind = ConditionKind.MainsOffFor, Seconds = 0 }, ModeId = "eco" },
            ],
        };
        Assert.Equal("max", ModeSelector.Decide(s, Mains(false, 600, soc: 30)).ModeId);
        Assert.Equal("eco", ModeSelector.Decide(s with { Rules = [.. s.Rules.Reverse()] }, Mains(false, 600, soc: 30)).ModeId);
    }

    [Fact]
    public void Decide_SocRuleInForce_HoldsWithinItsHysteresis()
    {
        var s = Defaults with
        {
            Rules =
            [
                new Rule { Condition = new RuleCondition { Kind = ConditionKind.SocBelow, Percent = 30, Hysteresis = 5 }, ModeId = "eco" },
                new Rule { Condition = new RuleCondition { Kind = ConditionKind.SocBelow, Percent = 101 }, ModeId = "max" },
            ],
        };
        var low = ModeSelector.Decide(s, Mains(false, 0, soc: 29));
        Assert.Equal("eco", low.ModeId);
        Assert.Equal("eco", ModeSelector.Decide(With(s, low.Selection), Mains(false, 0, soc: 33)).ModeId);
        Assert.Equal("max", ModeSelector.Decide(s, Mains(false, 0, soc: 33)).ModeId);   // not in force: threshold only
        Assert.Equal("max", ModeSelector.Decide(With(s, low.Selection), Mains(false, 0, soc: 35)).ModeId);
    }

    [Fact]
    public void Decide_UnknownMains_MatchesNoMainsRule()
    {
        var d = ModeSelector.Decide(Defaults, new SelectorInput(null, null, null, null, At));
        Assert.Equal("normal", d.ModeId);
        Assert.Equal("auto: no rule matches, mode kept", d.Reason);
    }

    [Theory]
    [InlineData("22:00", "06:00", 23, true)]
    [InlineData("22:00", "06:00", 6, false)]
    [InlineData("09:00", "17:00", 12, true)]
    [InlineData("09:00", "bad", 12, false)]
    public void InWindow_LocalTime_WrapsMidnight(string from, string to, int hour, bool expected) =>
        Assert.Equal(expected, ModeSelector.InWindow(from, to, new DateTimeOffset(2026, 11, 20, hour, 0, 0, TimeSpan.FromHours(2))));
}
