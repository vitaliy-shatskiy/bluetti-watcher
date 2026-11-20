using System.Text.Json.Nodes;
using BluettiWatcher.PowerMode;

namespace BluettiWatcher.Modes;

/// <summary>
/// The built-in modes (Normal, Eco, Max), the default Auto rules that reproduce the watcher's automatic behaviour (eco once
/// mains has been off for <c>mainsLostSec</c>, normal once it has been back for <c>mainsBackSec</c>), and the migration of
/// an existing install's switch settings. The well-known action types and their settings shapes are the contract the
/// adapters implement.
/// </summary>
internal static class BuiltInModes
{
    public const string Normal = "normal";
    public const string Eco = "eco";
    public const string Max = "max";

    /// <summary>Settings <c>{ "plan": "&lt;GUID&gt;" }</c>: the Windows power plan made active.</summary>
    public const string PowerPlanType = "windows.powerPlan";
    /// <summary>Settings <c>{ "limit": "min" | "default" | watts }</c>: the GPU board power limit.</summary>
    public const string GpuLimitType = "gpu.powerLimit";

    public static bool IsBuiltIn(string id) => id is Normal or Eco or Max;

    /// <summary>A fresh install (public defaults): Eco = Power saver; the GPU limit is listed but off; Max = High performance.</summary>
    public static ModesSettings Defaults(int mainsLostSec, int mainsBackSec) =>
        Build(normalPlan: null, gpuCap: false, mainsLostSec, mainsBackSec, autoHibernate: true);

    /// <summary>
    /// An install that ran the switch before: <c>returnPlan</c> becomes Normal's plan (<c>previous</c> = no plan action:
    /// leaving Eco restores whatever plan was active before it), <c>gpuCap</c> turns Eco's GPU limit on or off, and the
    /// <c>no-hibernate</c> file turns auto-hibernate off.
    /// </summary>
    public static ModesSettings Migrate(StationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var pm = PowerModeSwitch.ReadConfig(config.FlagFile);
        string? normalPlan = pm.ReturnPlan == "previous" ? null : pm.ReturnPlan;
        bool hibernate = !File.Exists(Path.Combine(config.DataDir, "no-hibernate"));
        return Build(normalPlan, pm.GpuCap, config.MainsLostSec, config.MainsBackSec, hibernate);
    }

    /// <summary>The switch has run here before: its flag or its config file exists.</summary>
    public static bool HasLegacySettings(StationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return File.Exists(config.FlagFile) || File.Exists(ReturnPlanFile.PathOf(config.FlagFile));
    }

    /// <summary>A built-in mode as a fresh install has it (the target of re-adding one missing from a stored file).</summary>
    public static Mode Fresh(string id) => Defaults(0, 0).Modes.First(m => m.Id == id);

    private static ModesSettings Build(string? normalPlan, bool gpuCap, int mainsLostSec, int mainsBackSec, bool autoHibernate) => new()
    {
        Modes =
        [
            new Mode
            {
                Id = Normal, Name = "Normal", FlagWord = FlagWord.Normal, BuiltIn = true,
                Actions = normalPlan is null ? [] : [Plan(normalPlan)],
            },
            new Mode
            {
                Id = Eco, Name = "Eco", FlagWord = FlagWord.Eco, BuiltIn = true,
                Actions = [Plan(PowerModeSwitch.SaverPlan), new ModeAction { TypeId = GpuLimitType, Enabled = gpuCap, Settings = new JsonObject { ["limit"] = "min" } }],
            },
            new Mode { Id = Max, Name = "Max", FlagWord = FlagWord.Normal, BuiltIn = true, Actions = [Plan(PowerModeSwitch.HighPlan)] },
        ],
        Rules =
        [
            new Rule { Condition = new RuleCondition { Kind = ConditionKind.MainsOffFor, Seconds = mainsLostSec }, ModeId = Eco },
            new Rule { Condition = new RuleCondition { Kind = ConditionKind.MainsOnFor, Seconds = mainsBackSec }, ModeId = Normal },
        ],
        FallbackModeId = Normal,
        AutoHibernate = autoHibernate,
    };

    private static ModeAction Plan(string guid) => new() { TypeId = PowerPlanType, Settings = new JsonObject { ["plan"] = guid } };
}
