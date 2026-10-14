namespace BluettiWatcher.Io;

/// <summary>
/// The known folders the service needs, from the environment Windows sets for every process. Environment.GetFolderPath
/// asks the shell (SHGetKnownFolderPath), which loads shell32.dll and windows.storage.dll into the process for its whole
/// life (about 0.2 MB private and 40 handles); that call stays as the fallback when a variable is missing or not an
/// absolute path.
/// </summary>
internal static class KnownFolders
{
    /// <summary>%LOCALAPPDATA%, else the shell's LocalApplicationData.</summary>
    public static string LocalAppData => Resolve("LOCALAPPDATA", Environment.SpecialFolder.LocalApplicationData, Environment.GetEnvironmentVariable, Environment.GetFolderPath);

    /// <summary>%ProgramData%, else the shell's CommonApplicationData.</summary>
    public static string ProgramData => Resolve("ProgramData", Environment.SpecialFolder.CommonApplicationData, Environment.GetEnvironmentVariable, Environment.GetFolderPath);

    /// <summary>The variable when it holds an absolute path (a drive or UNC root); otherwise the shell's answer for <paramref name="folder"/>.</summary>
    internal static string Resolve(string variable, Environment.SpecialFolder folder, Func<string, string?> environment, Func<Environment.SpecialFolder, string> shell)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(shell);
        string? value = environment(variable);
        return !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value) ? value : shell(folder);
    }
}
