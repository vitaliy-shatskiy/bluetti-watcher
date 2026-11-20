namespace BluettiWatcher.Modes.Modules;

/// <summary>The configured NVIDIA Profile Inspector path and the policy folder the broker imports <c>.nip</c> files from.</summary>
/// <remarks>Pattern: Adapter.</remarks>
internal sealed class NvidiaProfileFiles(string? inspectorPath, string policyFolder) : INvidiaProfileFiles
{
    public string? InspectorMissing() =>
        string.IsNullOrWhiteSpace(inspectorPath) ? "NVIDIA Profile Inspector is not configured"
        : !File.Exists(inspectorPath) ? $"NVIDIA Profile Inspector is not at {inspectorPath}"
        : null;

    public IReadOnlyList<string>? NipFiles()
    {
        try
        {
            return [.. Directory.EnumerateFiles(policyFolder, "*.nip", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
