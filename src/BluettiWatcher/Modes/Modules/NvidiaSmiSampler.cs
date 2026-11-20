using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using BluettiWatcher.Io;

namespace BluettiWatcher.Modes.Modules;

/// <summary>System32 nvidia-smi, queries only (no option that changes the GPU is ever passed).</summary>
/// <remarks>Pattern: Adapter.</remarks>
internal sealed partial class NvidiaSmiSampler(IProcessRunner runner) : IGpuSampler
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly string Exe = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");

    public async Task<IReadOnlyList<GpuSample>?> SampleAsync(CancellationToken ct) =>
        await RunAsync(["--query-gpu=index,pstate,clocks.gr,power.draw,utilization.gpu", "--format=csv,noheader,nounits"], ct) is { } lines
            ? ParseSamples(lines)
            : null;

    /// <summary>The plain report's process table: unlike <c>--query-compute-apps</c> it lists graphics processes too.</summary>
    public async Task<IReadOnlyList<GpuProcess>?> ProcessesAsync(CancellationToken ct) =>
        await RunAsync([], ct) is { } lines ? ParseProcesses(lines) : null;

    /// <summary>CSV rows <c>index, pstate, clock, power, util</c>; null when any row is malformed (no half reading).</summary>
    internal static IReadOnlyList<GpuSample>? ParseSamples(IReadOnlyList<string> lines)
    {
        var list = new List<GpuSample>();
        foreach (var line in lines.Where(l => l.Trim().Length > 0))
        {
            var cells = line.Split(',', StringSplitOptions.TrimEntries);
            if (cells.Length != 5 || !int.TryParse(cells[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                || Num(cells[2]) is not { } clock || Num(cells[4]) is not { } util)
                return null;
            list.Add(new GpuSample(index, cells[1], clock, Num(cells[3]), util));
        }
        return list.Count > 0 ? list : null;
    }

    internal static IReadOnlyList<GpuProcess> ParseProcesses(IReadOnlyList<string> lines) =>
        [.. lines.Select(l => ProcessRow().Match(l)).Where(m => m.Success).Select(m => new GpuProcess(
            int.Parse(m.Groups["gpu"].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups["pid"].Value, CultureInfo.InvariantCulture),
            m.Groups["type"].Value,
            m.Groups["name"].Value.Trim()))];

    private async Task<IReadOnlyList<string>?> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        try
        {
            var r = await runner.RunAsync(Exe, args, Timeout, ct);
            return r.ExitCode == 0 && !r.TimedOut ? r.Lines : null;
        }
        catch (Win32Exception)
        {
            return null;   // no NVIDIA driver: nvidia-smi is not installed
        }
    }

    private static double? Num(string cell) =>
        double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

    // "|    0   N/A  N/A      1234    C+G   C:\...\app.exe                         N/A      |"
    [GeneratedRegex(@"^\|\s+(?<gpu>\d+)\s+\S+\s+\S+\s+(?<pid>\d+)\s+(?<type>C\+G|C|G)\s+(?<name>.+?)\s+\S+\s*\|\s*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ProcessRow();
}
