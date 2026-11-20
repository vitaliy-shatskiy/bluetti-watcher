using System.Xml;
using System.Xml.Linq;
using BluettiWatcher.Io;

namespace BluettiWatcher.Modes.Broker;

/// <summary>
/// Enables or disables one scheduled task through System32 schtasks.exe (fixed arguments, the name as one argument, no
/// shell) and reads <c>Settings/Enabled</c> back from its XML (absent = enabled), which no UI language changes.
/// </summary>
internal sealed class SchTasksTaskControl(IProcessRunner runner) : ITaskControl
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static string Exe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");

    public async Task<BrokerResult> SetEnabledAsync(string name, bool enabled, CancellationToken ct)
    {
        var change = await runner.RunAsync(Exe, ["/change", "/tn", name, enabled ? "/enable" : "/disable"], Timeout, ct);
        if (change.TimedOut || change.ExitCode != 0) return new(false, $"the task {name} could not be changed: {ProcessText.Last(change)}", null);
        var query = await runner.RunAsync(Exe, ["/query", "/tn", name, "/xml"], Timeout, ct);
        if (query.TimedOut || query.ExitCode != 0) return new(false, $"the task {name} could not be read back: {ProcessText.Last(query)}", null);
        return ParseEnabled(string.Join('\n', query.Lines)) is { } on
            ? new(true, null, on ? "enabled" : "disabled")
            : new(false, $"the task {name} answered XML without its settings", null);
    }

    /// <summary>The task's Settings/Enabled · null = not task XML.</summary>
    internal static bool? ParseEnabled(string xml)
    {
        try
        {
            var settings = XDocument.Parse(xml).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Settings");
            if (settings is null) return null;
            var enabled = settings.Elements().FirstOrDefault(e => e.Name.LocalName == "Enabled");
            return enabled is null || !string.Equals(enabled.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase);
        }
        catch (XmlException) { return null; }
    }
}
