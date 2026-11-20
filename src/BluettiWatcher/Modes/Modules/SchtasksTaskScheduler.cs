using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BluettiWatcher.Io;

namespace BluettiWatcher.Modes.Modules;

/// <summary>
/// <see cref="ITaskScheduler"/> over <c>schtasks.exe</c> (System32, windowless, time-bounded). The enabled state is read
/// from the task XML (<c>Settings/Enabled</c>, absent = enabled), which unlike the status column is not localized.
/// </summary>
internal sealed partial class SchtasksTaskScheduler(IProcessRunner runner) : ITaskScheduler
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly string Exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");

    public async Task<ResourceRead> ReadEnabledAsync(string path, CancellationToken ct)
    {
        var r = await runner.RunAsync(Exe, ["/query", "/tn", path, "/xml"], Timeout, ct);
        if (r.TimedOut || r.ExitCode != 0) return ResourceRead.Failed($"task '{path}' cannot be read: {FirstLine(r)}");
        return ParseEnabled(string.Join('\n', r.Lines)) is { } enabled
            ? ResourceRead.Of(JsonValue.Create(enabled))
            : ResourceRead.Failed($"task '{path}': the task XML has no Settings block");
    }

    public async Task<string?> SetEnabledAsync(string path, bool enabled, CancellationToken ct)
    {
        var r = await runner.RunAsync(Exe, ["/change", "/tn", path, enabled ? "/enable" : "/disable"], Timeout, ct);
        return !r.TimedOut && r.ExitCode == 0 ? null : $"schtasks /change: {FirstLine(r)}";
    }

    public async Task<IReadOnlyList<string>?> ListAsync(CancellationToken ct)
    {
        var r = await runner.RunAsync(Exe, ["/query", "/fo", "csv", "/nh"], Timeout, ct);
        if (r.TimedOut || r.ExitCode != 0) return null;
        return [.. r.Lines
            .Where(l => l.StartsWith("\"\\", StringComparison.Ordinal))
            .Select(l => l[1..l.IndexOf('"', 1)])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The task's <c>Settings/Enabled</c>: true when absent (the schema default), null when the XML has no Settings.</summary>
    internal static bool? ParseEnabled(string xml)
    {
        var settings = SettingsPattern().Match(xml);
        if (!settings.Success) return null;
        var enabled = EnabledPattern().Match(settings.Groups[1].Value);
        return !enabled.Success || enabled.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstLine(ProcessResult r) =>
        r.TimedOut ? $"no answer within {Timeout.TotalSeconds:0} s" : r.Lines.FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? $"exit {r.ExitCode}";

    [GeneratedRegex(@"<Settings>(.*?)</Settings>", RegexOptions.Singleline | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SettingsPattern();

    // Settings' own Enabled is a direct child; the nested blocks inside Settings (IdleSettings, NetworkSettings) carry none.
    [GeneratedRegex(@"<Enabled>\s*(true|false)\s*</Enabled>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex EnabledPattern();
}
