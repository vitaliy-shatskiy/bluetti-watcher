using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BluettiWatcher.Modes;

/// <summary>Source-generated JSON for the modes feature (NativeAOT: no reflection). Module settings stay <see cref="JsonObject"/>.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false,
    ReadCommentHandling = JsonCommentHandling.Skip, UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ModesSettings))]
[JsonSerializable(typeof(Mode))]
[JsonSerializable(typeof(List<Rule>))]
[JsonSerializable(typeof(Selection))]
[JsonSerializable(typeof(ActionModuleDescriptor))]
[JsonSerializable(typeof(DiscoveredItem))]
[JsonSerializable(typeof(ActionStatus))]
[JsonSerializable(typeof(JournalRecord))]
[JsonSerializable(typeof(List<JournalRecord>))]
[JsonSerializable(typeof(ActionReport))]
[JsonSerializable(typeof(ResourceStatus))]
[JsonSerializable(typeof(JsonObject))]
internal sealed partial class ModesJson : JsonSerializerContext;
