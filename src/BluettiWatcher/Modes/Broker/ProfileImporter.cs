using BluettiWatcher.Io;

namespace BluettiWatcher.Modes.Broker;

/// <summary>
/// NVIDIA Profile Inspector, windowless, fixed arguments, no shell: <c>-silentImport -replaceImport &lt;file.nip&gt;</c> to
/// import, <c>-exportCustomized</c> to export (it writes a timestamped .nip beside its exe; that file is moved into the
/// broker's exports folder at once, so nothing is left where it was written).
/// </summary>
internal sealed class ProfileImporter(IProcessRunner runner, TimeProvider time) : IProfileImporter
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<string?> ImportAsync(string inspectorExe, string nipFile, CancellationToken ct)
    {
        if (!File.Exists(inspectorExe)) return $"NVIDIA Profile Inspector is not at {inspectorExe}";
        var r = await RunAsync(inspectorExe, ["-silentImport", "-replaceImport", nipFile], ct);
        return r is null ? "NVIDIA Profile Inspector could not start" : r.TimedOut ? "NVIDIA Profile Inspector timed out"
            : r.ExitCode == 0 ? null : $"NVIDIA Profile Inspector failed: {ProcessText.Last(r)}";
    }

    public async Task<ProfileExport> ExportAsync(string inspectorExe, string exportsFolder, CancellationToken ct)
    {
        if (!File.Exists(inspectorExe)) return new("", $"NVIDIA Profile Inspector is not at {inspectorExe}");
        string folder = Path.GetDirectoryName(inspectorExe)!;   // a fully qualified exe path has a folder
        var started = time.GetUtcNow().UtcDateTime.AddSeconds(-2);
        var r = await RunAsync(inspectorExe, ["-exportCustomized"], ct);
        if (r is null || r.TimedOut || r.ExitCode != 0) return new("", $"NVIDIA Profile Inspector's export failed: {(r is null ? "could not start" : ProcessText.Last(r))}");
        var written = new DirectoryInfo(folder).EnumerateFiles("*.nip").Where(f => f.LastWriteTimeUtc >= started).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        if (written is null) return new("", $"NVIDIA Profile Inspector wrote no .nip beside {inspectorExe}");
        Directory.CreateDirectory(exportsFolder);
        string to = Path.Combine(exportsFolder, written.Name);
        File.Move(written.FullName, to, overwrite: true);
        return new(to, "");
    }

    private async Task<ProcessResult?> RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        try { return await runner.RunAsync(exe, args, Timeout, ct); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { _ = e; return null; }
    }
}
