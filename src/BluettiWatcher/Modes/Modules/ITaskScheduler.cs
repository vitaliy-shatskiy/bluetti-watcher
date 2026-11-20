namespace BluettiWatcher.Modes.Modules;

/// <summary>Scheduled tasks by full path (<c>\Folder\Name</c>), with the user's own rights.</summary>
internal interface ITaskScheduler
{
    /// <summary>Whether the task is enabled; a failed read (absent task, no access) is <see cref="ResourceRead.Failed"/>.</summary>
    Task<ResourceRead> ReadEnabledAsync(string path, CancellationToken ct);

    /// <summary>Enables or disables the task; null when done, else why not (an admin task refuses a user).</summary>
    Task<string?> SetEnabledAsync(string path, bool enabled, CancellationToken ct);

    /// <summary>Every task path the user can see; null when the list cannot be read.</summary>
    Task<IReadOnlyList<string>?> ListAsync(CancellationToken ct);
}
