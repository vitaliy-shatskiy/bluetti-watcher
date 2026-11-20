using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// Reads and writes one value of a power plan for the plan-setting modules. A value is <c>{ "plan": guid, "value": n }</c>:
/// the plan it was read from or written to travels with it, so a restore writes the baseline back to the plan it came
/// from even after the active plan changed. A target without <c>plan</c> means the plan of the key (or the active one).
/// </summary>
internal sealed class PlanSettingAccess(IPowerSchemes schemes)
{
    /// <summary>The plan a key names: a GUID, or null for "the plan active when read or applied".</summary>
    public static bool TryParsePlan(string text, out Guid? plan)
    {
        plan = null;
        if (string.Equals(text, "active", StringComparison.Ordinal)) return true;
        if (!Guid.TryParseExact(text, "D", out var g)) return false;
        plan = g;
        return true;
    }

    public static string PlanText(Guid? plan) => plan is { } g ? g.ToString("D") : "active";

    public static JsonObject Value(Guid plan, uint value) => new() { ["plan"] = plan.ToString("D"), ["value"] = value };

    public ResourceRead Read(Guid? plan, PowerSetting setting, PowerSource source)
    {
        Guid? which = plan ?? schemes.ActivePlan();
        if (which is not { } p) return ResourceRead.Failed("the active power plan could not be read");
        return schemes.ReadValue(p, setting, source) is { } v
            ? ResourceRead.Of(Value(p, v))
            : ResourceRead.Failed($"{setting.Name} ({source}) of the plan {p:D} could not be read");
    }

    public ApplyResult Apply(Guid? plan, PowerSetting setting, PowerSource source, JsonNode? target)
    {
        if (!TryTarget(target, out Guid? targetPlan, out uint value)) return ApplyResult.Failed($"not a {setting.Name} value: {target?.ToJsonString()}");
        Guid? which = targetPlan ?? plan ?? schemes.ActivePlan();
        if (which is not { } p) return ApplyResult.Failed("the active power plan could not be read");
        var plans = schemes.ListPlans();
        if (plans is null) return ApplyResult.Failed("the power plans could not be read");
        if (!plans.Any(x => x.Id == p)) return ApplyResult.Failed($"the power plan {p:D} is not on this machine");
        if (schemes.WriteValue(p, setting, source, value) is { } error) return ApplyResult.Failed(error);
        var back = Read(p, setting, source);
        return back.Ok ? ApplyResult.Written(back.Value) : ApplyResult.Failed($"written, but the read-back failed: {back.Error}");
    }

    /// <summary>Same value, and the same plan when both name one.</summary>
    public static bool Equivalent(JsonNode? a, JsonNode? b)
    {
        if (!TryTarget(a, out var pa, out uint va) || !TryTarget(b, out var pb, out uint vb)) return false;
        return va == vb && (pa is null || pb is null || pa == pb);
    }

    private static bool TryTarget(JsonNode? node, out Guid? plan, out uint value)
    {
        plan = null;
        value = 0;
        if (node is not JsonObject o || !JsonNumber.TryWhole(o["value"], 0, uint.MaxValue, out long n)) return false;
        value = (uint)n;
        if (o["plan"] is null) return true;
        if (JsonNumber.Text(o["plan"]) is not { } text || !Guid.TryParseExact(text, "D", out var g)) return false;
        plan = g;
        return true;
    }
}
