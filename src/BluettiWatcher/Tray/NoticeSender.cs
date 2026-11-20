namespace BluettiWatcher.Tray;

/// <summary>
/// Shows each notice as a Windows toast while a Start-menu shortcut carries the app id (<see cref="Toasts"/>), else, or
/// when the toast fails, as the tray balloon, which Windows keeps nowhere. Each change of the way is logged once.
/// </summary>
internal sealed class NoticeSender(Func<bool> toastsRegistered, Action<TrayNotice> toast, Func<TrayNotice, bool> balloon, Action<string> log)
{
    private bool? _toasts;

    public void Send(TrayNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        string why;
        if (toastsRegistered())
        {
            try
            {
                toast(notice);
                if (_toasts != true) log("tray: notices are Windows toasts, kept in the notification centre");
                _toasts = true;
                log($"notice: {notice.Kind} {notice.Title}");
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                why = $"the toast failed ({e.GetType().Name}: {e.Message})";
            }
        }
        else why = $"no Start-menu shortcut carries the app id {Toasts.AppId} (install.ps1 sets it)";
        if (_toasts != false) log($"tray: notices are tray balloons, which Windows keeps nowhere: {why}");
        _toasts = false;
        if (balloon(notice)) log($"notice: {notice.Kind} {notice.Title}");
    }
}
