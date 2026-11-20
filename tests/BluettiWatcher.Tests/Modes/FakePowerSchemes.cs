using BluettiWatcher.Modes.Modules;

namespace BluettiWatcher.Tests.Modes;

/// <summary>Power plans in memory: a list, an active one, values per (plan, setting, source). Fail* switches break one call each.</summary>
internal sealed class FakePowerSchemes : IPowerSchemes
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid Saver = new("a1841308-3542-4f1f-a8d6-f8ef75ce11d1");
    /// <summary>A plan a user made (powercfg /duplicatescheme): any GUID, any name.</summary>
    public static readonly Guid UserMade = new("0d1f9e3a-5b7c-4e21-9a6d-2c8b4f7e1a30");

    public List<PowerPlan> Plans { get; } = [new(Balanced, "Balanced"), new(Saver, "Power saver"), new(UserMade, "My quiet plan")];
    public Guid Active { get; set; } = Balanced;
    public Dictionary<(Guid, Guid, PowerSource), uint> Values { get; } = [];
    public List<string> Writes { get; } = [];
    public bool FailList { get; set; }
    public bool FailActive { get; set; }
    public bool FailRead { get; set; }
    public string? FailWrite { get; set; }

    public IReadOnlyList<PowerPlan>? ListPlans() => FailList ? null : [.. Plans];

    public Guid? ActivePlan() => FailActive ? null : Active;

    public string? SetActivePlan(Guid plan)
    {
        if (FailWrite is not null) return FailWrite;
        Writes.Add($"active {plan:D}");
        Active = plan;
        return null;
    }

    public uint? ReadValue(Guid plan, PowerSetting setting, PowerSource source) =>
        FailRead ? null : Values.TryGetValue((plan, setting.Setting, source), out var v) ? v : 100;

    public string? WriteValue(Guid plan, PowerSetting setting, PowerSource source, uint value)
    {
        if (FailWrite is not null) return FailWrite;
        Writes.Add($"{plan:D} {setting.Name} {source} {value}");
        Values[(plan, setting.Setting, source)] = value;
        return null;
    }
}
