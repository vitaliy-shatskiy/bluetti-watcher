namespace BluettiWatcher.Modes.Broker;

/// <summary>An export of the driver profiles: the file it was moved to, or the error (<see cref="File"/> empty then).</summary>
internal sealed record ProfileExport(string File, string Error);
