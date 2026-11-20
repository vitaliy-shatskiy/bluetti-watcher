namespace BluettiWatcher.Modes.Broker;

/// <summary>NVIDIA Profile Inspector's import and export of driver profiles (.nip).</summary>
internal interface IProfileImporter
{
    /// <summary>Imports <paramref name="nipFile"/>, replacing what it names; null = imported, else the error.</summary>
    Task<string?> ImportAsync(string inspectorExe, string nipFile, CancellationToken ct);

    /// <summary>Exports the customized profiles and moves the file into <paramref name="exportsFolder"/>.</summary>
    Task<ProfileExport> ExportAsync(string inspectorExe, string exportsFolder, CancellationToken ct);
}
