using System.Diagnostics;
using System.Globalization;

namespace BluettiWatcher.Modes.Broker;

/// <summary>
/// The broker task runs its own copy of the exe (<c>bin\</c> under the broker folder), refreshed only by the elevated
/// <c>install.ps1 -Broker -Apply</c>: after an upgrade it can be older than the service. That is never silent: /api/health
/// names the state and the service adds the event <see cref="StaleEvent"/> (a tray notice) at start.
/// </summary>
internal static class BrokerVersion
{
    public const string StaleEvent = "broker-stale";

    /// <summary><paramref name="broker"/> null = no broker copy installed. Compared on major.minor.build: a revision is the same release.</summary>
    public static BrokerStatus Check(Version own, Version? broker)
    {
        ArgumentNullException.ThrowIfNull(own);
        if (broker is null)
            return new BrokerStatus("missing", null, "the admin broker is not installed: the power modes' admin actions fail until install.ps1 -Broker -Apply runs in an elevated shell");
        var mine = new Version(own.Major, own.Minor, Math.Max(0, own.Build));
        var theirs = new Version(broker.Major, broker.Minor, Math.Max(0, broker.Build));
        return theirs < mine
            ? new BrokerStatus("stale", Text(broker), $"the admin broker is {Text(broker)}, older than the service ({Text(own)}): run install.ps1 -Broker -Apply in an elevated shell")
            : new BrokerStatus("current", Text(broker), null);
    }

    /// <summary>The file version of the broker's copy; null when there is none or it carries no version.</summary>
    public static Version? Read(BrokerPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string exe = Path.Combine(paths.Root, "bin", "BluettiWatcher.exe");
        if (!File.Exists(exe)) return null;
        var v = FileVersionInfo.GetVersionInfo(exe);
        return v.FileMajorPart + v.FileMinorPart + v.FileBuildPart == 0 ? null : new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
    }

    private static string Text(Version v) => string.Create(CultureInfo.InvariantCulture, $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}");
}
