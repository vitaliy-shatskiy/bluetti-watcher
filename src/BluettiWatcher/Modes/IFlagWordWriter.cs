namespace BluettiWatcher.Modes;

/// <summary>Writes the active mode's word into the power-mode flag (its readers are unchanged).</summary>
internal interface IFlagWordWriter
{
    /// <summary>"" = written (or already so); else why not.</summary>
    Task<string> WriteAsync(FlagWord word, CancellationToken ct);
}
