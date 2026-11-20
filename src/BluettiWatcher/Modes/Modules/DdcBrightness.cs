namespace BluettiWatcher.Modes.Modules;

/// <summary>VCP 0x10 (luminance) as the monitor reports it: raw <paramref name="Current"/> of <paramref name="Max"/>; <paramref name="Error"/> set = no reading.</summary>
internal sealed record DdcBrightness(int Current, int Max, string Error = "")
{
    public bool Ok => Error.Length == 0 && Max > 0;

    public static DdcBrightness Failed(string error) => new(0, 0, string.IsNullOrEmpty(error) ? "DDC/CI read failed" : error);
}
