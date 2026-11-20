using System.Text.Json.Nodes;

namespace BluettiWatcher.Modes;

/// <summary>
/// What a module is, for the registry and the catalogue: a settings UI is rendered from <see cref="SettingsSchema"/> and
/// <see cref="UiHints"/> alone (no module ships UI code).
/// </summary>
/// <param name="TypeId">Stable id the modes refer to (<c>windows.powerPlan</c>); lower-case letters, digits and dots.</param>
/// <param name="Version">Settings version; a module that changes its settings shape bumps it.</param>
/// <param name="Title">Short English name for the UI.</param>
/// <param name="SettingsSchema">JSON Schema (draft 2020-12 subset: type, properties, required, enum, minimum, maximum, items, additionalProperties) of the settings object.</param>
/// <param name="UiHints">Per-field hints: <c>{ "field": { "widget": "select", "discovery": "plans", "label": "..." } }</c> (<c>list</c> is read as <c>discovery</c>); empty when none.</param>
/// <param name="DiscoveryLists">Names of the lists <see cref="IActionModule.DiscoverAsync"/> returns.</param>
/// <param name="Restoration">How leaving a mode undoes this module's writes.</param>
/// <param name="NeedsAdmin">The writes go through the admin broker.</param>
/// <param name="DefaultSettings">Settings a new action starts with; must validate against <see cref="SettingsSchema"/>.</param>
internal sealed record ActionModuleDescriptor(
    string TypeId,
    int Version,
    string Title,
    JsonObject SettingsSchema,
    JsonObject UiHints,
    IReadOnlyList<string> DiscoveryLists,
    RestorationClass Restoration,
    bool NeedsAdmin,
    JsonObject DefaultSettings);
