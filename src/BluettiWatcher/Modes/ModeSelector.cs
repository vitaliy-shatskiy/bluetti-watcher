using System.Globalization;

namespace BluettiWatcher.Modes;

/// <summary>
/// Which mode is active. Manual: the person's pick, held until Auto (a pick of a mode since deleted falls back). Auto: the
/// first rule that matches; the rule already in force is checked with its hysteresis, the others with their thresholds;
/// when none matches the last pick stays (Auto never flips back on a gap between rules). Pure: no clock, no I/O.
/// </summary>
/// <remarks>Pattern: State machine (Manual / Auto), its state saved as <see cref="Selection"/>.</remarks>
internal static class ModeSelector
{
    public static SelectorDecision Decide(ModesSettings settings, SelectorInput input)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(input);
        var sel = settings.Selection;
        if (sel.Kind == SelectionKind.Manual)
        {
            return sel.ManualModeId is { } held && Exists(settings, held)
                ? new SelectorDecision(held, sel, "manual")
                : new SelectorDecision(settings.FallbackModeId, Selection.Auto, "manual mode gone: auto");
        }
        for (int i = 0; i < settings.Rules.Count; i++)
        {
            var rule = settings.Rules[i];
            if (!Exists(settings, rule.ModeId) || !Matches(rule.Condition, input, sel.AutoRule == i)) continue;
            return new SelectorDecision(rule.ModeId, sel with { AutoModeId = rule.ModeId, AutoRule = i },
                $"auto: rule {(i + 1).ToString(CultureInfo.InvariantCulture)} ({Describe(rule.Condition)})");
        }
        string kept = sel.AutoModeId is { } last && Exists(settings, last) ? last : settings.FallbackModeId;
        return new SelectorDecision(kept, sel with { AutoModeId = kept, AutoRule = null }, "auto: no rule matches, mode kept");
    }

    private static bool Exists(ModesSettings s, string id) => s.Modes.Any(m => string.Equals(m.Id, id, StringComparison.Ordinal));

    /// <summary>Whether a condition holds; <paramref name="inForce"/> applies its hysteresis.</summary>
    internal static bool Matches(RuleCondition c, SelectorInput i, bool inForce) => c.Kind switch
    {
        ConditionKind.MainsOffFor => i.MainsOn == false && i.MainsFor is { } off && (inForce || off.TotalSeconds >= c.Seconds),
        ConditionKind.MainsOnFor => i.MainsOn == true && i.MainsFor is { } on && (inForce || on.TotalSeconds >= c.Seconds),
        ConditionKind.SocBelow => i.Soc is { } soc && soc < c.Percent + (inForce ? c.Hysteresis : 0),
        ConditionKind.CutWithin => i.MinutesToCut is { } m && m <= c.Minutes + (inForce ? c.Hysteresis : 0),
        ConditionKind.TimeWindow => InWindow(c.From, c.To, i.LocalNow),
        _ => false,
    };

    /// <summary>[from, to) local time; wraps midnight when to is before from; an unreadable bound never matches.</summary>
    internal static bool InWindow(string from, string to, DateTimeOffset localNow)
    {
        if (!TryTime(from, out var f) || !TryTime(to, out var t)) return false;
        var now = TimeOnly.FromTimeSpan(localNow.TimeOfDay);
        return f <= t ? now >= f && now < t : now >= f || now < t;
    }

    internal static bool TryTime(string text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private static string Describe(RuleCondition c) => c.Kind switch
    {
        ConditionKind.MainsOffFor => $"mains off for {c.Seconds.ToString(CultureInfo.InvariantCulture)} s",
        ConditionKind.MainsOnFor => $"mains on for {c.Seconds.ToString(CultureInfo.InvariantCulture)} s",
        ConditionKind.SocBelow => $"battery under {c.Percent.ToString("0.##", CultureInfo.InvariantCulture)} %",
        ConditionKind.CutWithin => $"outage within {c.Minutes.ToString(CultureInfo.InvariantCulture)} min",
        ConditionKind.TimeWindow => $"{c.From}-{c.To}",
        _ => c.Kind.ToString(),
    };
}
