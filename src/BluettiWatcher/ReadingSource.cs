namespace BluettiWatcher;

/// <summary>Where readings come from (design.md §4 Reading source): config key <c>readingSource</c>, or the page's choice.</summary>
internal enum ReadingSource
{
    /// <summary>Bluetooth first, the cloud when Bluetooth misses (and a token exists).</summary>
    Auto,
    /// <summary>Bluetooth only: the cloud is never called, no token is needed, no cloud history.</summary>
    Bluetooth,
    /// <summary>The cloud only: Bluetooth is never opened, no station address is needed.</summary>
    Cloud,
}
