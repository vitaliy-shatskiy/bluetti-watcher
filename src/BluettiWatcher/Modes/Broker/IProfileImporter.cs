namespace BluettiWatcher.Modes.Broker;

/// <summary>Imports a driver profile (.nip) through NVIDIA Profile Inspector; null = imported, else the error.</summary>
internal interface IProfileImporter
{
    Task<string?> ImportAsync(string inspectorExe, string nipFile, CancellationToken ct);
}
