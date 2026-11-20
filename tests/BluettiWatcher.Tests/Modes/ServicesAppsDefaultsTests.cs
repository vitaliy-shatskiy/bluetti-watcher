using System.Text.Json;
using System.Text.Json.Nodes;
using BluettiWatcher.Modes;
using BluettiWatcher.Modes.Modules;

namespace BluettiWatcher.Tests.Modes;

/// <summary>Every module of the services-and-apps group: its defaults validate against its own schema and survive a JSON round trip.</summary>
public sealed class ServicesAppsDefaultsTests
{
    public static TheoryData<string> TypeIds => [.. Modules().Select(m => m.Descriptor.TypeId)];

    private static IReadOnlyList<IActionModule> Modules()
    {
        var clock = new JumpClock();
        var processes = new FakeProcessControl();
        var broker = new FakeBroker();
        return
        [
            new ServiceStateModule(new FakeServiceReader(), broker, clock),
            new ProcessCloseModule(processes, clock),
            new CommandPairModule(new Dictionary<string, CommandSpec>(StringComparer.Ordinal), new FakeRunner(), _ => { }),
            new PhoneLinkModule(processes, clock),
            new ScheduledTaskModule(new FakeTaskScheduler(), broker),
        ];
    }

    private static IActionModule Module(string typeId) => Modules().Single(m => m.Descriptor.TypeId == typeId);

    [Fact]
    public void All_RealFactory_ListsTheFiveModulesOnce()
    {
        var all = ServicesAppsModules.All(new FakeBroker(), new FakeRunner(), new Dictionary<string, CommandSpec>(StringComparer.Ordinal), new JumpClock(), _ => { });

        Assert.Equal(["service.state", "process.close", "command.pair", "windows.phoneLink", "scheduledTask.state"], all.Select(m => m.Descriptor.TypeId));
    }

    [Theory]
    [MemberData(nameof(TypeIds))]
    public void Defaults_ValidateAgainstTheSchemaAndTheModule(string typeId)
    {
        var d = Module(typeId).Descriptor;

        Assert.Empty(SchemaProblems(d.SettingsSchema, d.DefaultSettings, "$"));
        Assert.Empty(Module(typeId).Validate(d.DefaultSettings));
        // Generic defaults: the list modules target nothing until the user lists something.
        if (typeId is "service.state" or "process.close" or "scheduledTask.state") Assert.Empty(Module(typeId).Expand(d.DefaultSettings));
    }

    [Theory]
    [MemberData(nameof(TypeIds))]
    public void Descriptor_RoundTripsThroughTheModesJson(string typeId)
    {
        var d = Module(typeId).Descriptor;

        string json = JsonSerializer.Serialize(d, ModesJson.Default.ActionModuleDescriptor);
        var back = JsonSerializer.Deserialize(json, ModesJson.Default.ActionModuleDescriptor)!;

        Assert.True(JsonNode.DeepEquals(d.DefaultSettings, back.DefaultSettings));
        Assert.True(JsonNode.DeepEquals(d.SettingsSchema, back.SettingsSchema));
        Assert.Empty(SchemaProblems(back.SettingsSchema, back.DefaultSettings, "$"));
        Assert.Equal(d.Restoration, back.Restoration);
    }

    [Fact]
    public void SchemaProblems_WrongTypeAndUnknownField_AreFound()
    {
        var schema = Module("process.close").Descriptor.SettingsSchema;

        Assert.NotEmpty(SchemaProblems(schema, (JsonObject)JsonNode.Parse("""{"apps":"game.exe"}""")!, "$"));
        Assert.NotEmpty(SchemaProblems(schema, (JsonObject)JsonNode.Parse("""{"apps":[],"extra":1}""")!, "$"));
        Assert.NotEmpty(SchemaProblems(schema, (JsonObject)JsonNode.Parse("""{"apps":[],"graceSec":61}""")!, "$"));
    }

    /// <summary>The descriptor's schema subset: type, properties, required, enum, minimum, maximum, items, additionalProperties.</summary>
    private static List<string> SchemaProblems(JsonObject schema, JsonNode? value, string at)
    {
        var problems = new List<string>();
        string? type = schema["type"]?.GetValue<string>();
        var kind = value?.GetValueKind() ?? JsonValueKind.Null;
        bool typeOk = type switch
        {
            "object" => kind == JsonValueKind.Object,
            "array" => kind == JsonValueKind.Array,
            "string" => kind == JsonValueKind.String,
            "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
            "integer" => kind == JsonValueKind.Number && value!.GetValue<double>() % 1 == 0,
            "number" => kind == JsonValueKind.Number,
            null => true,
            _ => false,
        };
        if (!typeOk) { problems.Add($"{at}: not {type}"); return problems; }
        if (schema["enum"] is JsonArray options && !options.Any(o => JsonNode.DeepEquals(o, value))) problems.Add($"{at}: not in enum");
        if (kind == JsonValueKind.Number)
        {
            double n = value!.GetValue<double>();
            if (schema["minimum"] is { } min && n < min.GetValue<double>()) problems.Add($"{at}: below minimum");
            if (schema["maximum"] is { } max && n > max.GetValue<double>()) problems.Add($"{at}: above maximum");
        }
        if (value is JsonObject obj)
        {
            var props = schema["properties"] as JsonObject ?? [];
            foreach (var r in schema["required"] as JsonArray ?? [])
                if (!obj.ContainsKey(r!.GetValue<string>())) problems.Add($"{at}.{r}: required");
            foreach (var (name, child) in obj)
            {
                if (props[name] is JsonObject sub) problems.AddRange(SchemaProblems(sub, child, $"{at}.{name}"));
                else if (schema["additionalProperties"]?.GetValueKind() == JsonValueKind.False) problems.Add($"{at}.{name}: not allowed");
            }
        }
        if (value is JsonArray arr && schema["items"] is JsonObject items)
            for (int i = 0; i < arr.Count; i++) problems.AddRange(SchemaProblems(items, arr[i], $"{at}[{i}]"));
        return problems;
    }
}
