namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// The Windows power plans (powrprof.dll). A read that fails answers null; a write answers null when done, else the error.
/// </summary>
internal interface IPowerSchemes
{
    /// <summary>Every plan on the machine, user-created ones included · null = the list could not be read.</summary>
    IReadOnlyList<PowerPlan>? ListPlans();

    Guid? ActivePlan();

    string? SetActivePlan(Guid plan);

    uint? ReadValue(Guid plan, PowerSetting setting, PowerSource source);

    /// <summary>Writes one value of <paramref name="plan"/>; when it is the active plan the change takes effect at once.</summary>
    string? WriteValue(Guid plan, PowerSetting setting, PowerSource source, uint value);
}
