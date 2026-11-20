using System.Runtime.InteropServices;
using System.Security;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace BluettiWatcher.Tray;

/// <summary>
/// Windows toasts under the app id a Start-menu shortcut carries (install.ps1 sets it on "Bluetti Watcher"): Windows keeps
/// them in the notification centre, unlike a tray balloon. A click opens the page by protocol activation, so no COM
/// activation server is registered. Windows takes a toast for an app id no shortcut names and shows it nowhere, hence
/// <see cref="Registered"/> before each toast.
/// </summary>
internal static partial class Toasts
{
    /// <summary>The same id as tools/shortcut.ps1 WatcherAppId.</summary>
    public const string AppId = "BluettiWatcher";

    private static readonly Guid ShellItemId = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    /// <summary>Whether a Start-menu shortcut carries <see cref="AppId"/>: the apps folder then knows that name.</summary>
    public static bool Registered()
    {
        int hr = SHCreateItemFromParsingName(@"shell:AppsFolder\" + AppId, 0, in ShellItemId, out nint item);
        if (item != 0) Marshal.Release(item);
        return hr >= 0;
    }

    /// <summary>Names this process by the app id; each toast names it as well.</summary>
    public static void ClaimAppId() => _ = SetCurrentProcessExplicitAppUserModelID(AppId);

    /// <summary>Shows the toast; throws what WinRT throws.</summary>
    public static void Show(TrayNotice notice, Uri panel)
    {
        var xml = new XmlDocument();
        xml.LoadXml(Xml(notice, panel));
        ToastNotificationManager.CreateToastNotifier(AppId).Show(new ToastNotification(xml));
    }

    /// <summary>The toast's XML: title and text; a click opens <paramref name="panel"/> in the browser.</summary>
    public static string Xml(TrayNotice notice, Uri panel)
    {
        ArgumentNullException.ThrowIfNull(notice);
        ArgumentNullException.ThrowIfNull(panel);
        return $"""<toast activationType="protocol" launch="{SecurityElement.Escape(panel.AbsoluteUri)}"><visual><binding template="ToastGeneric"><text>{SecurityElement.Escape(notice.Title)}</text><text>{SecurityElement.Escape(notice.Text)}</text></binding></visual></toast>""";
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SHCreateItemFromParsingName(string path, nint bindContext, in Guid iid, out nint item);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);
}
