using System.Text.Json;
using System.Text.Json.Nodes;
using BluettiWatcher.Io;
using BluettiWatcher.Modes;
using BluettiWatcher.Modes.Broker;
using BluettiWatcher.Modes.Modules;

namespace BluettiWatcher.Tests.Modes;

/// <summary>Every display and GPU module's default settings validate against its schema and survive the catalogue's JSON.</summary>
public sealed class DisplayGpuDescriptorTests
{
    // Constructing the Windows adapters touches nothing: they reach the machine only when a module method runs.
    private static readonly IReadOnlyList<IActionModule> Modules = DisplayGpuModules.All(
        new RefusingBroker(), new RefusingRunner(), null, Path.GetTempPath(), TimeProvider.System, (_, _, _) => { }, _ => { });

    public static TheoryData<string> TypeIds() => [.. Modules.Select(m => m.Descriptor.TypeId)];

    [Fact]
    public void The_group_has_the_four_modules_with_unique_type_ids()
    {
        Assert.Equal(["display.refreshRate", "display.brightness", "nvidia.profile", "gpu.idleWatcher"], Modules.Select(m => m.Descriptor.TypeId));
    }

    [Theory]
    [MemberData(nameof(TypeIds))]
    public void Default_settings_validate_against_the_schema_and_Validate(string typeId)
    {
        var d = Modules.Single(m => m.Descriptor.TypeId == typeId);
        Assert.Empty(SchemaCheck.Problems(d.Descriptor.SettingsSchema, d.Descriptor.DefaultSettings));
        Assert.Empty(d.Validate(d.Descriptor.DefaultSettings));
    }

    [Theory]
    [MemberData(nameof(TypeIds))]
    public void The_descriptor_round_trips_through_the_catalogue_json(string typeId)
    {
        var d = Modules.Single(m => m.Descriptor.TypeId == typeId).Descriptor;
        string json = JsonSerializer.Serialize(d, ModesJson.Default.ActionModuleDescriptor);
        var back = JsonSerializer.Deserialize(json, ModesJson.Default.ActionModuleDescriptor)!;
        Assert.True(JsonNode.DeepEquals(d.DefaultSettings, back.DefaultSettings));
        Assert.True(JsonNode.DeepEquals(d.SettingsSchema, back.SettingsSchema));
        Assert.Equal(d.Restoration, back.Restoration);
        Assert.Empty(SchemaCheck.Problems(back.SettingsSchema, back.DefaultSettings));
    }

    [Fact]
    public void The_schema_check_refuses_a_bad_value()
    {
        // Positive control: the check used above can fail.
        var schema = Modules[0].Descriptor.SettingsSchema;
        Assert.NotEmpty(SchemaCheck.Problems(schema, JsonNode.Parse("""{ "monitors": [ { "monitor": "m", "hz": 0 } ] }""")));
        Assert.NotEmpty(SchemaCheck.Problems(schema, JsonNode.Parse("""{ "monitors": [], "extra": 1 }""")));
    }

    private sealed class RefusingBroker : IAdminBroker
    {
        public Task<BrokerResult> RunAsync(string operation, JsonObject args, CancellationToken ct) => throw new InvalidOperationException("no broker in this test");
    }

    private sealed class RefusingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, IReadOnlyDictionary<string, string>? env = null) =>
            throw new InvalidOperationException("no process in this test");
    }
}
