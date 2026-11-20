using System.Globalization;
using BluettiWatcher.Modes.Broker;

namespace BluettiWatcher.Modes;

/// <summary>
/// An entry of a mode's action (<paramref name="Action"/>, from 0) the broker's policy does not allow, with the elevated
/// command that allows it. The service never elevates: the person runs it.
/// </summary>
internal sealed record PolicyWarning(int Action, BrokerTargetKind Kind, string Name, string Command)
{
    public string KindName => Kind.ToString().ToLowerInvariant();

    /// <summary>One line for the problems list, the log and the event.</summary>
    public string Text(string modeId) =>
        $"{modeId}, action {(Action + 1).ToString(CultureInfo.InvariantCulture)}: {KindName} '{Name}' is not in the admin broker's policy: a change that needs administrator rights is refused. Allow it in an elevated PowerShell: {Command}";

    /// <summary>
    /// The broker targets of <paramref name="mode"/>'s enabled actions that <paramref name="policy"/> does not
    /// list, in action order; <paramref name="exe"/> is the exe that runs <c>--broker-allow</c>.
    /// </summary>
    public static IReadOnlyList<PolicyWarning> For(Mode mode, ModuleRegistry registry, BrokerPolicy policy, string exe)
    {
        var warnings = new List<PolicyWarning>();
        for (int i = 0; i < mode.Actions.Count; i++)
        {
            var a = mode.Actions[i];
            if (!a.Enabled || !registry.TryGet(a.TypeId, out var m) || m is not IBrokerTargets targets) continue;
            foreach (var t in targets.BrokerTargets(a.Settings).Where(t => !Allows(policy, t)))
                warnings.Add(new PolicyWarning(i, t.Kind, t.Name, $"& \"{exe}\" --broker-allow {t.Kind.ToString().ToLowerInvariant()} \"{t.Name}\""));
        }
        return warnings;
    }

    private static bool Allows(BrokerPolicy policy, BrokerTarget t) => t.Kind switch
    {
        BrokerTargetKind.Service => policy.AllowsService(t.Name),
        BrokerTargetKind.Task => policy.ListedTask(t.Name) is not null,
        BrokerTargetKind.Profile => policy.AllowsProfile(t.Name),
        _ => throw new ArgumentOutOfRangeException(nameof(t), t.Kind, "unknown broker target kind"),
    };
}
