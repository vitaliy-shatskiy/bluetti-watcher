using System.Text.Json;
using System.Text.Json.Nodes;
using BluettiWatcher.Modes;

namespace BluettiWatcher.Tests.Modes;

public sealed class ModuleRegistryTests
{
    /// <summary>Every module this build registers, plus the fakes the tests use.</summary>
    public static TheoryData<string> AllTypes()
    {
        var data = new TheoryData<string>();
        foreach (var m in Registry().All) data.Add(m.Descriptor.TypeId);
        return data;
    }

    private static ModuleRegistry Registry() =>
        new([.. ModuleCatalog.Production(new StationConfig()), new FakeModule("fake.one", new FakeMachine()), new FakeModule("fake.token", new FakeMachine(), RestorationClass.Token)]);

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void Descriptor_DefaultSettings_ValidateAgainstSchemaAndRoundTrip(string typeId)
    {
        var reg = Registry();
        Assert.True(reg.TryGet(typeId, out var m));
        Assert.Empty(reg.Validate(typeId, m.Descriptor.DefaultSettings));

        string json = JsonSerializer.Serialize(m.Descriptor, ModesJson.Default.ActionModuleDescriptor);
        var back = JsonSerializer.Deserialize(json, ModesJson.Default.ActionModuleDescriptor)!;
        Assert.True(JsonNode.DeepEquals(m.Descriptor.SettingsSchema, back.SettingsSchema));
        Assert.True(JsonNode.DeepEquals(m.Descriptor.DefaultSettings, back.DefaultSettings));
        Assert.Equal(m.Descriptor.Restoration, back.Restoration);
        Assert.Empty(reg.Validate(typeId, back.DefaultSettings));
    }

    [Fact]
    public void Ctor_DuplicateTypeId_Throws() =>
        Assert.Throws<ArgumentException>(() => new ModuleRegistry([new FakeModule("a", new FakeMachine()), new FakeModule("a", new FakeMachine())]));

    [Fact]
    public void Validate_UnknownType_IsAProblem() =>
        Assert.Equal(["unknown action type nope"], Registry().Validate("nope", []));

    [Theory]
    [InlineData("""{"key":"k"}""", "$.value: required")]
    [InlineData("""{"key":"k","value":"v","extra":1}""", "$.extra: not allowed")]
    [InlineData("""{"key":"","value":"v"}""", "$.key: shorter than 1")]
    [InlineData("""{"key":5,"value":"v"}""", "$.key: expected \"string\", got number")]
    [InlineData("""{"key":"k","value":null}""", "$.value: expected [\"string\",\"number\",\"boolean\"], got null")]
    public void Validate_BadSettings_NamesTheField(string settings, string problem) =>
        Assert.Contains(problem, Registry().Validate("fake.one", JsonNode.Parse(settings)!.AsObject()));

    [Theory]
    [InlineData("""{"type":"integer","minimum":1,"maximum":5}""", "3", true)]
    [InlineData("""{"type":"integer","minimum":1,"maximum":5}""", "3.5", false)]
    [InlineData("""{"type":"integer","minimum":1,"maximum":5}""", "6", false)]
    [InlineData("""{"enum":["a","b"]}""", "\"c\"", false)]
    [InlineData("""{"type":"array","items":{"type":"string"},"maxItems":2}""", """["a",1]""", false)]
    [InlineData("""{"type":"array","items":{"type":"string"},"maxItems":2}""", """["a","b","c"]""", false)]
    [InlineData("""{"type":"array","items":{"type":"string"},"maxItems":2}""", """["a","b"]""", true)]
    public void Check_Keywords(string schema, string value, bool valid) =>
        Assert.Equal(valid, SettingsSchema.Check(JsonNode.Parse(schema)!.AsObject(), JsonNode.Parse(value)).Count == 0);
}
