using System.Text.Json.Nodes;
using BluettiWatcher.Io;
using BluettiWatcher.Modes.Modules;
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
    /// <summary>Settings <c>{ "gpu": "0", "percent": n | "watts": n | "limit": "min" }</c>: the GPU board power limit.</summary>
    public const string GpuLimitType = "gpu.powerLimit";

    public static bool IsBuiltIn(string id) => id is Normal or Eco or Max;

    /// <summary>A fresh install (public defaults): Eco = Power saver; the GPU limit is listed but off; Max = High performance.</summary>
    public static ModesSettings Defaults(int mainsLostSec, int mainsBackSec) =>
        Build(normalPlan: null, gpuCap: false, mainsLostSec, mainsBackSec, autoHibernate: true, enterCommand: "", leaveCommand: "");

    /// <summary>
    /// An install that ran the switch before: <c>returnPlan</c> becomes Normal's plan (<c>previous</c> = no plan action:
    /// leaving Eco restores whatever plan was active before it), <c>gpuCap</c> turns Eco's GPU limit on or off, and the
    /// <c>no-hibernate</c> file turns auto-hibernate off. The config's <c>actions.eco</c> / <c>actions.normal</c> become Eco's
    /// command pair (the commands <c>eco</c> / <c>normal</c>, <see cref="ModuleCatalog.Commands"/>): run on entering and on
    /// leaving Eco, as they ran on the station's eco and mains-back.
    /// </summary>
    public static ModesSettings Migrate(StationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var pm = PowerModeSwitch.ReadConfig(config.FlagFile);
        string? normalPlan = pm.ReturnPlan == "previous" ? null : pm.ReturnPlan;
        bool hibernate = !File.Exists(NoHibernatePath(config));
        string Command(string key) => string.IsNullOrWhiteSpace(config.Actions?.GetValueOrDefault(key)) ? "" : key;
        return Build(normalPlan, pm.GpuCap, config.MainsLostSec, config.MainsBackSec, hibernate, Command(StateActions.Eco), Command(StateActions.Normal));
    }

    /// <summary>The legacy kill switch: its presence turned hibernation off before the modes' autoHibernate setting.</summary>
    public static string NoHibernatePath(StationConfig config) => Path.Combine(config.DataDir, "no-hibernate");

    /// <summary>The switch has run here before: its flag or its config file exists.</summary>
    public static bool HasLegacySettings(StationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return File.Exists(config.FlagFile) || File.Exists(ReturnPlanFile.PathOf(config.FlagFile));
    }

    /// <summary>A built-in mode as a fresh install has it (the target of re-adding one missing from a stored file).</summary>
    public static Mode Fresh(string id) => Defaults(0, 0).Modes.First(m => m.Id == id);

    private static ModesSettings Build(string? normalPlan, bool gpuCap, int mainsLostSec, int mainsBackSec, bool autoHibernate,
        string enterCommand, string leaveCommand) => new()
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
                // The optional actions are listed off and empty: a backup pause (a command pair), services to stop (Windows
                // Search, Xbox gaming services, ...), Phone Link closed, the GPU idle watcher logging what holds the GPU at P0.
                Actions =
                [
                    Plan(PowerModeSwitch.SaverPlan),
                    new ModeAction { TypeId = GpuLimitType, Enabled = gpuCap, Settings = new JsonObject { ["gpu"] = "0", ["limit"] = GpuPowerLimitModule.MinLimit } },
                    new ModeAction
                    {
                        TypeId = CommandPairModule.Id, Enabled = enterCommand.Length + leaveCommand.Length > 0,
                        Settings = new JsonObject { ["name"] = "actions", ["enter"] = enterCommand, ["leave"] = leaveCommand },
                    },
                    new ModeAction { TypeId = ServiceStateModule.Id, Enabled = false, Settings = new JsonObject { ["services"] = new JsonArray() } },
                    new ModeAction { TypeId = PhoneLinkModule.Id, Enabled = false, Settings = [] },
                    new ModeAction { TypeId = GpuIdleWatcherModule.TypeId, Enabled = false, Settings = new JsonObject { ["minutes"] = 5, ["minClockMhz"] = 1000, ["maxUtilization"] = 2 } },
                ],
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
