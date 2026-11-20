// The eco end task registered for the console exe: pointed at the windowless twin at service start, nothing else changed.
using System.Text;
using BluettiWatcher.PowerMode;
using static BluettiWatcher.Tests.PowerModeScript.Pm;

namespace BluettiWatcher.Tests.PowerModeScript;

public sealed class PmEndTaskRepointTests
{
    private const string ConsoleExe = @"C:\bw\bin\BluettiWatcher.exe";
    private const string Twin = @"C:\bw\bin\BluettiWatcherService.exe";
    private const string Args = "power-mode end -Path \"C:\\x\\power-mode.json\" -TaskUntil 2026-11-20T18:00:00+02:00";

    private static void Register(Machine m, string exe) =>
        File.WriteAllText(m.TaskFile, PowerModeSwitch.EndTaskXml("S-1-5-21-1", exe, Args, new DateTimeOffset(2026, 11, 20, 18, 0, 0, TimeSpan.FromHours(2))), Encoding.Unicode);

    private static string? Repoint(Machine m, string exePath)
    {
        using var sw = new PowerModeSwitch(m.Env, TextWriter.Null, exePath);
        return sw.RepointEndTask();
    }

    [Fact]
    public async Task RepointEndTask_task_for_the_console_exe_now_starts_the_twin_with_the_same_arguments()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t);
        Register(m, ConsoleExe);
        string? note = Repoint(m, Twin);
        Assert.Equal($"eco end task BluettiWatcherEcoEnd: now starts {Twin} (no console window)", note);
        string xml = m.TaskXml()!;
        Assert.Contains($"<Command>{Twin}</Command>", xml, StringComparison.Ordinal);
        Assert.Contains($"<Arguments>{PowerModeSwitch.XmlEscape(Args)}</Arguments>", xml, StringComparison.Ordinal);
        Assert.Contains("<StartBoundary>2026-11-20T", xml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepointEndTask_reads_the_task_name_from_the_task_prefix()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t);
        Register(m, ConsoleExe);
        Dictionary<string, string> env = new(m.Env, StringComparer.OrdinalIgnoreCase) { ["POWER_MODE_TASK_PREFIX"] = "OtherEco" };
        using var sw = new PowerModeSwitch(env, TextWriter.Null, Twin);
        Assert.Null(sw.RepointEndTask());
        Assert.Contains(m.Calls(), c => c.Contains("/query /tn OtherEcoEnd", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RepointEndTask_no_task_does_nothing()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t);
        Assert.Null(Repoint(m, Twin));
        Assert.DoesNotContain(m.Calls(), c => c.Contains("/create", StringComparison.Ordinal));
        Assert.Null(m.TaskXml());
    }

    [Theory]
    [InlineData(Twin)]
    [InlineData(@"C:\Other\tool.exe")]
    public async Task RepointEndTask_task_already_on_the_twin_or_another_command_is_left_alone(string registered)
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t);
        Register(m, registered);
        Assert.Null(Repoint(m, Twin));
        Assert.DoesNotContain(m.Calls(), c => c.Contains("/create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RepointEndTask_without_the_twin_installed_does_nothing()
    {
        await using Cleanup t = new();
        Machine m = NewMachine(t);
        Register(m, ConsoleExe);
        Assert.Null(Repoint(m, ConsoleExe));
        Assert.Contains($"<Command>{ConsoleExe}</Command>", m.TaskXml()!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_service_repoints_the_end_task_at_start_outside_a_dry_run()
    {
        string program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "BluettiWatcher", "Program.cs"));
        Assert.Contains("if (!config.DryRun && endTaskSwitch.RepointEndTask() is { } repointed) Log(repointed);", program, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "bluetti-watcher.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("no bluetti-watcher.slnx above the test assembly");
    }
}
