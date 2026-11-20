using System.Globalization;
using BluettiWatcher.Io;

namespace BluettiWatcher.Modes.Broker;

/// <summary>System32 nvidia-smi, windowless, 5 s per call. Only fixed arguments and validated numbers reach it.</summary>
internal sealed class NvidiaSmi(IProcessRunner runner) : INvidiaSmi
{
    internal static readonly string[] QueryArguments =
        ["--query-gpu=index,name,power.limit,power.min_limit,power.max_limit,power.default_limit", "--format=csv,noheader,nounits"];

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static string Exe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");

    public async Task<GpuQuery> QueryAsync(CancellationToken ct)
    {
        if (!File.Exists(Exe)) return GpuQuery.Failed("nvidia-smi.exe is not in System32 (no NVIDIA driver)");
        ProcessResult r;
        try { r = await runner.RunAsync(Exe, QueryArguments, Timeout, ct); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return GpuQuery.Failed($"nvidia-smi could not start: {e.Message}"); }
        if (r.TimedOut) return GpuQuery.Failed("nvidia-smi timed out");
        if (r.ExitCode != 0) return GpuQuery.Failed($"nvidia-smi failed: {ProcessText.Last(r)}");
        return Parse(r.Lines);
    }

    public async Task<string?> SetPowerLimitAsync(string gpu, double watts, CancellationToken ct)
    {
        ProcessResult r;
        try { r = await runner.RunAsync(Exe, ["-i", gpu, "-pl", watts.ToString("0.##", CultureInfo.InvariantCulture)], Timeout, ct); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return $"nvidia-smi could not start: {e.Message}"; }
        if (r.TimedOut) return "nvidia-smi timed out";
        return r.ExitCode == 0 ? null : $"nvidia-smi -pl failed: {ProcessText.Last(r)}";
    }

    /// <summary>
    /// The query's CSV lines: index, name (may hold commas), then four wattages. A GPU without power management (<c>[N/A]</c>)
    /// is left out: it has no limit to set. A line that is neither is a failed read, never skipped.
    /// </summary>
    internal static GpuQuery Parse(IReadOnlyList<string> lines)
    {
        var gpus = new List<GpuInfo>();
        foreach (var line in lines.Where(l => l.Trim().Length > 0))
        {
            var cells = line.Split(',').Select(c => c.Trim()).ToArray();
            if (cells.Length < 6 || !int.TryParse(cells[0], NumberStyles.None, CultureInfo.InvariantCulture, out _))
                return GpuQuery.Failed($"nvidia-smi answered a line that is not a GPU: {line}");
            var w = cells[^4..];
            if (w.Any(c => c.StartsWith('[')))
                continue;
            var n = new double[4];
            for (int i = 0; i < 4; i++)
            {
                if (!double.TryParse(w[i], NumberStyles.Float, CultureInfo.InvariantCulture, out n[i]) || !double.IsFinite(n[i]) || n[i] <= 0)
                    return GpuQuery.Failed($"nvidia-smi answered a power limit that is not a number: {line}");
            }
            gpus.Add(new GpuInfo(cells[0], string.Join(',', cells[1..^4]), n[0], n[1], n[2], n[3]));
        }
        return GpuQuery.Of(gpus);
    }
}
