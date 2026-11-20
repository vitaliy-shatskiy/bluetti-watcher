using BluettiWatcher.Io;

namespace BluettiWatcher.Modes.Broker;

/// <summary>The line a tool's error message is taken from.</summary>
internal static class ProcessText
{
    /// <summary>The last line the tool printed, or "exit N" when it printed nothing.</summary>
    public static string Last(ProcessResult r) =>
        r.Lines.Count > 0 ? r.Lines[^1] : $"exit {r.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}
