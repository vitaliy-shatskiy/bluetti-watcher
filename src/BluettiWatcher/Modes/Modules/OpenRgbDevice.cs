namespace BluettiWatcher.Modes.Modules;

/// <summary>One controller as OpenRGB's server describes it: its index on the server, its name and the colours it holds (0x00BBGGRR each).</summary>
internal sealed record OpenRgbDevice(int Index, string Name, IReadOnlyList<uint> Colors)
{
    /// <summary>Every LED black (the alpha byte is ignored); a device with no LEDs counts as off.</summary>
    public bool IsOff => Colors.All(c => (c & 0x00FF_FFFFu) == 0);
}
