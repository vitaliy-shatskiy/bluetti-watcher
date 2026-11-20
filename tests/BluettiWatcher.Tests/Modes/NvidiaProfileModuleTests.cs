using System.Text.Json.Nodes;
using BluettiWatcher.Modes;
using BluettiWatcher.Modes.Broker;
using BluettiWatcher.Modes.Modules;

namespace BluettiWatcher.Tests.Modes;

/// <summary>nvidia.profile against a fake broker: import through the broker, fingerprinted read-back, restore from the export.</summary>
public sealed class NvidiaProfileModuleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Key = NvidiaProfileModule.ResourceKey;

    [Fact]
    public async Task Applying_a_file_imports_it_through_the_broker_and_restoring_imports_the_before_export()
    {
        var broker = new FakeBroker();
        var module = new NvidiaProfileModule(new FakeFiles(), broker);
        var intent = Assert.Single(module.Expand(new JsonObject { ["file"] = "quiet.nip" }));

        var baseline = await module.ReadAsync(Key, Ct);
        Assert.True(baseline.Ok, baseline.Error);
        Assert.Equal("export-1.nip", baseline.Value!["export"]!.GetValue<string>());
        Assert.False(module.Equivalent(Key, intent.Target, baseline.Value));

        var applied = await module.ApplyAsync(intent, Ct);
        Assert.True(applied.Ok, applied.Error);
        Assert.Equal("quiet.nip", applied.ReadBack!["applied"]!.GetValue<string>());
        Assert.True(module.Equivalent(Key, intent.Target, applied.ReadBack));
        Assert.False(module.Equivalent(Key, baseline.Value, applied.ReadBack));

        // A later read names the applied file from the fingerprint alone.
        var later = await module.ReadAsync(Key, Ct);
        Assert.True(module.Equivalent(Key, intent.Target, later.Value));

        var restored = await module.ApplyAsync(new ResourceIntent(Key, baseline.Value), Ct);
        Assert.True(restored.Ok, restored.Error);
        Assert.Equal(["quiet.nip", "export-1.nip"], broker.Imports);
        Assert.True(module.Equivalent(Key, baseline.Value, restored.ReadBack));
    }

    [Fact]
    public async Task A_refused_import_is_a_failure()
    {
        var broker = new FakeBroker { ApplyError = "file outside the policy folder" };
        var r = await new NvidiaProfileModule(new FakeFiles(), broker).ApplyAsync(new ResourceIntent(Key, new JsonObject { ["applied"] = "quiet.nip" }), Ct);
        Assert.False(r.Ok);
        Assert.Contains("file outside the policy folder", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_export_is_a_failed_read_and_a_failed_read_back()
    {
        var broker = new FakeBroker { ExportFails = true };
        var module = new NvidiaProfileModule(new FakeFiles(), broker);
        Assert.False((await module.ReadAsync(Key, Ct)).Ok);
        var r = await module.ApplyAsync(new ResourceIntent(Key, new JsonObject { ["applied"] = "quiet.nip" }), Ct);
        Assert.False(r.Ok);
        Assert.Contains("read-back failed", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_inspector_the_module_is_unavailable_and_discovery_lists_the_nip_files()
    {
        var module = new NvidiaProfileModule(new FakeFiles { Missing = "NVIDIA Profile Inspector is not configured" }, new FakeBroker());
        var a = await module.AvailableAsync(Ct);
        Assert.False(a.Available);
        Assert.Equal("NVIDIA Profile Inspector is not configured", a.Reason);
        Assert.Equal(["quiet.nip"], (await module.DiscoverAsync(Ct))["files"].Select(i => i.Id));
    }

    [Theory]
    [InlineData(@"..\x.nip")]
    [InlineData(@"C:\x.nip")]
    [InlineData("sub/x.nip")]
    [InlineData("x.txt")]
    public void Validate_refuses_a_path_or_a_non_nip_file(string file)
    {
        Assert.Single(new NvidiaProfileModule(new FakeFiles(), new FakeBroker()).Validate(new JsonObject { ["file"] = file }));
    }

    [Fact]
    public void An_empty_file_validates_and_expands_to_nothing()
    {
        var module = new NvidiaProfileModule(new FakeFiles(), new FakeBroker());
        var settings = new JsonObject { ["file"] = "" };
        Assert.Empty(module.Validate(settings));
        Assert.Empty(module.Expand(settings));
    }

    private sealed class FakeFiles : INvidiaProfileFiles
    {
        public string? Missing { get; init; }
        public string? InspectorMissing() => Missing;
        public IReadOnlyList<string>? NipFiles() => ["quiet.nip"];
    }

    /// <summary>The driver store as a fingerprint of the last imported file; each export gets a new name.</summary>
    private sealed class FakeBroker : IAdminBroker
    {
        private string _store = "factory";
        private readonly Dictionary<string, string> _exports = [];
        public List<string> Imports { get; } = [];
        public string? ApplyError { get; init; }
        public bool ExportFails { get; init; }

        public Task<BrokerResult> RunAsync(string operation, JsonObject args, CancellationToken ct)
        {
            switch (operation)
            {
                case NvidiaProfileModule.ExportOperation when ExportFails:
                    return Task.FromResult(new BrokerResult(false, "inspector crashed", null));
                case NvidiaProfileModule.ExportOperation:
                    string name = $"export-{_exports.Count + 1}.nip";
                    _exports[name] = _store;
                    return Task.FromResult(new BrokerResult(true, null, new JsonObject { ["file"] = name, ["sha256"] = "sha-" + _store }));
                case NvidiaProfileModule.ApplyOperation when ApplyError is not null:
                    return Task.FromResult(new BrokerResult(false, ApplyError, null));
                case NvidiaProfileModule.ApplyOperation:
                    string file = args["file"]!.GetValue<string>();
                    Imports.Add(file);
                    _store = _exports.TryGetValue(file, out var exported) ? exported : file;
                    return Task.FromResult(new BrokerResult(true, null, null));
                default:
                    return Task.FromResult(new BrokerResult(false, "unknown operation", null));
            }
        }
    }
}
