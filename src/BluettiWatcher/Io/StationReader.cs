using BluettiWatcher.Decision;

namespace BluettiWatcher.Io;

internal interface IStationReader
{
    /// <summary>One reading; null = the station did not answer.</summary>
    Task<Reading?> ReadAsync(CancellationToken ct);
}
