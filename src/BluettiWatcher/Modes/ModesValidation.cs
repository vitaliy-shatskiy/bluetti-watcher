using System.Globalization;

namespace BluettiWatcher.Modes;

/// <summary>The API's checks on a mode or a rule list before anything is saved; each problem names its field.</summary>
internal static class ModesValidation
{
    /// <summary>The selection value that means the Auto rules (POST /api/modes/select): never a mode id.</summary>
    public const string AutoChoice = "auto";
    public const int MaxModes = 32;
    /// <summary>Ids a mode cannot take: Auto's selection value and the fixed routes under /api/modes/.</summary>
    public static readonly IReadOnlySet<string> ReservedIds = new HashSet<string>([AutoChoice, "rules", "select", "catalogue", "journal"], StringComparer.Ordinal);
    public const int MaxActions = 32;
    public const int MaxRules = 32;

    public static List<string> Mode(Mode mode, ModuleRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(registry);
        var p = new List<string>();
        if (!ValidId(mode.Id)) p.Add("id: 1 to 32 lower-case letters, digits or -");
        else if (mode.Id == AutoChoice) p.Add("id: auto is reserved (it selects the Auto rules)");
        else if (ReservedIds.Contains(mode.Id)) p.Add($"id: {mode.Id} is reserved (a route under /api/modes/)");
        if (mode.Name is null || mode.Name.Trim().Length is 0 or > 64) p.Add("name: 1 to 64 characters");
        if (!Enum.IsDefined(mode.FlagWord)) p.Add("flagWord: normal, eco or emergency");
        if (mode.Actions.Count > MaxActions) p.Add($"actions: at most {MaxActions.ToString(CultureInfo.InvariantCulture)}");
        for (int i = 0; i < mode.Actions.Count; i++)
        {
            var a = mode.Actions[i];
            string at = $"actions[{i.ToString(CultureInfo.InvariantCulture)}]";
            if (a is null) { p.Add($"{at}: missing"); continue; }
            // A type this build does not know is kept (an adapter of a newer build): it shows as unsupported, never runs.
            if (!registry.TryGet(a.TypeId, out _)) continue;
            p.AddRange(registry.Validate(a.TypeId, a.Settings).Select(x => $"{at}.settings{x.TrimStart('$')}"));
        }
        return p;
    }

    public static List<string> Rules(IReadOnlyList<Rule> rules, IReadOnlyList<Mode> modes)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(modes);
        var p = new List<string>();
        if (rules.Count > MaxRules) p.Add($"rules: at most {MaxRules.ToString(CultureInfo.InvariantCulture)}");
        for (int i = 0; i < rules.Count; i++)
        {
            string at = $"rules[{i.ToString(CultureInfo.InvariantCulture)}]";
            var r = rules[i];
            if (r?.Condition is not { } c) { p.Add($"{at}: missing condition"); continue; }
            if (modes.All(m => m.Id != r.ModeId)) p.Add($"{at}.modeId: no mode {r.ModeId}");
            string? bad = c.Kind switch
            {
                ConditionKind.MainsOffFor or ConditionKind.MainsOnFor => c.Seconds is < 0 or > 86400 ? "seconds: 0 to 86400" : null,
                ConditionKind.SocBelow => c.Percent is < 0 or > 100 || double.IsNaN(c.Percent) ? "percent: 0 to 100" : null,
                ConditionKind.CutWithin => c.Minutes is < 0 or > 1440 ? "minutes: 0 to 1440" : null,
                ConditionKind.TimeWindow => !ModeSelector.TryTime(c.From, out _) || !ModeSelector.TryTime(c.To, out _) ? "from, to: HH:mm" : null,
                _ => "kind: mainsOffFor, mainsOnFor, socBelow, cutWithin or timeWindow",
            };
            if (bad is not null) p.Add($"{at}.condition.{bad}");
            if (c.Hysteresis is < 0 or > 100 || double.IsNaN(c.Hysteresis)) p.Add($"{at}.condition.hysteresis: 0 to 100");
        }
        return p;
    }

    /// <summary>1 to 32 of a-z, 0-9, <c>-</c> (a plain loop: no Regex in the exe for this).</summary>
    public static bool ValidId(string? id)
    {
        if (id is null || id.Length is 0 or > 32) return false;
        foreach (char c in id)
            if (c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')) return false;
        return true;
    }
}
