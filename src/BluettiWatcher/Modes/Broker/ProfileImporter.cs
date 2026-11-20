using BluettiWatcher.Io;

namespace BluettiWatcher.Modes.Broker;

/// <summary>NVIDIA Profile Inspector's silent import (<c>-silentImport &lt;file.nip&gt;</c>): two arguments, no shell, 30 s at most.</summary>
internal sealed class ProfileImporter(IProcessRunner runner) : IProfileImporter
{
    public async Task<string?> ImportAsync(string inspectorExe, string nipFile, CancellationToken ct)
    {
        if (!File.Exists(inspectorExe)) return $"NVIDIA Profile Inspector is not at {inspectorExe}";
        ProcessResult r;
        try { r = await runner.RunAsync(inspectorExe, ["-silentImport", nipFile], TimeSpan.FromSeconds(30), ct); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return $"NVIDIA Profile Inspector could not start: {e.Message}"; }
        if (r.TimedOut) return "NVIDIA Profile Inspector timed out";
        return r.ExitCode == 0 ? null : $"NVIDIA Profile Inspector failed: {ProcessText.Last(r)}";
    }
}
