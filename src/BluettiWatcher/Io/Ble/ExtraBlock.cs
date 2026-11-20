namespace BluettiWatcher.Io.Ble;

/// <summary>One optional read beyond the reading's fields: a station that refuses it is not asked again until a restart.</summary>
internal sealed record ExtraBlock(string Name, int Start, int Quantity);
