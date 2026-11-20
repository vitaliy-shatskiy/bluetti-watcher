using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static BluettiWatcher.Tray.TrayNative;

namespace BluettiWatcher.Tray;

/// <summary>
/// The notification-area icon, inside the service process: one thread with a hidden window and a blocking message loop
/// (no timer). Other threads hand it a snapshot or a notice and post a message; the icon is rebuilt only when its picture
/// changes. A notice is a Windows toast, kept in the notification centre, while the installer's Start-menu shortcut carries
/// the app id (<see cref="Toasts"/>); else it is a shell balloon (NIF_INFO), a banner Windows keeps nowhere.
/// The icon comes back when Explorer restarts (TaskbarCreated) and also when the taskbar was not there yet at start.
/// "Exit tray" removes the icon and its notices until the next service start; the service keeps running.
/// </summary>
internal sealed class TrayHost : ITrayView, IDisposable
{
    private const uint CallbackMsg = WM_APP + 1;
    private const uint ShowMsg = WM_APP + 2;
    private const uint NoticeMsg = WM_APP + 3;
    private const uint CloseMsg = WM_APP + 4;
    private const uint IconId = 1;
    private const string ClassName = "BluettiWatcherTray";
    private static readonly TraySnapshot Starting = new(null, TrayPower.Unknown, null, null, "starting", null, null, true, false);

    private readonly ITrayModes _modes;
    private readonly Uri _panel;
    private readonly TimeZoneInfo _zone;
    private readonly Action<string> _log;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly ConcurrentQueue<TrayNotice> _notices = new();
    private readonly Lock _gate = new();
    private readonly NoticeSender _sender;
    private TraySnapshot? _pending;
    /// <summary>Set on the tray thread before <see cref="_ready"/>; read by other threads after it.</summary>
    private nint _hwnd;
    private volatile bool _closed;

    // The tray thread's own state.
    private uint _taskbarCreated;
    private bool _added;
    private bool _waitLogged;
    private nint _icon;
    private IconKey? _shownKey;
    private string _shownTip = "";
    private TraySnapshot _shown = Starting;

    private TrayHost(ITrayModes modes, Uri panel, TimeZoneInfo zone, Action<string> log)
    {
        _modes = modes;
        _panel = panel;
        _zone = zone;
        _log = log;
        _sender = new NoticeSender(Toasts.Registered, n => Toasts.Show(n, panel), Balloon, log);
        _thread = new Thread(Run) { IsBackground = true, Name = "tray" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>Starts the tray thread; null (logged) when its window could not be made.</summary>
    public static TrayHost? Start(ITrayModes modes, Uri panel, TimeZoneInfo zone, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        TrayHost? host = new TrayHost(modes, panel, zone, log);
        try
        {
            host._thread.Start();
            if (host._ready.Wait(TimeSpan.FromSeconds(10)) && host._hwnd != 0)
            {
                var started = host;
                host = null;
                return started;
            }
            log("tray: not shown - its window could not be made");
            return null;
        }
        finally { host?.Dispose(); }
    }

    public void Show(TraySnapshot snapshot)
    {
        if (_closed) return;
        lock (_gate) _pending = snapshot;
        PostMessage(_hwnd, ShowMsg, 0, 0);
    }

    public void Notify(TrayNotice notice)
    {
        if (_closed) return;
        _notices.Enqueue(notice);
        PostMessage(_hwnd, NoticeMsg, 0, 0);
    }

    public void Dispose()
    {
        if (!_closed && _hwnd != 0) PostMessage(_hwnd, CloseMsg, 0, 0);
        if (!_thread.Join(TimeSpan.FromSeconds(5))) _log("tray: its thread did not end within 5 s");
        _ready.Dispose();
    }

    private unsafe void Run()
    {
        // Per-monitor DPI on this thread only: the icon is drawn at the taskbar's real size instead of a scaled 16 px one.
        SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        Toasts.ClaimAppId();
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        nint instance = GetModuleHandle(0);
        var self = GCHandle.Alloc(this);
        try
        {
            fixed (char* cls = ClassName)
            {
                var wc = new WndClassEx { cbSize = (uint)sizeof(WndClassEx), lpfnWndProc = &WndProc, hInstance = instance, lpszClassName = cls };
                RegisterClassEx(&wc);   // 0 when a host before this one registered it: the class stays usable
            }
            // A hidden top-level window, not a message-only one: TaskbarCreated is broadcast to top-level windows only.
            _hwnd = CreateWindowEx(WS_EX_TOOLWINDOW, ClassName, "Bluetti Watcher tray", WS_POPUP, 0, 0, 0, 0, 0, 0, instance, GCHandle.ToIntPtr(self));
            _ready.Set();
            if (_hwnd == 0)
            {
                _closed = true;
                return;
            }
            // An elevated service still hears Explorer's restart.
            if (_taskbarCreated != 0) ChangeWindowMessageFilterEx(_hwnd, _taskbarCreated, MSGFLT_ALLOW, 0);
            AddIcon();
            Msg msg;
            while (GetMessage(&msg, 0, 0, 0) > 0)
            {
                TranslateMessage(&msg);
                DispatchMessage(&msg);
            }
        }
        finally
        {
            _closed = true;
            if (_icon != 0) DestroyIcon(_icon);
            self.Free();
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_NCCREATE) SetWindowLongPtr(hwnd, GWLP_USERDATA, *(nint*)lParam);   // CREATESTRUCTW.lpCreateParams: the host's handle
        nint handle = GetWindowLongPtr(hwnd, GWLP_USERDATA);
        if (handle != 0 && GCHandle.FromIntPtr(handle).Target is TrayHost host && host.Handle(hwnd, msg, wParam, lParam) is { } done) return done;
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    /// <summary>The window's messages; null = the default handling. Nothing may escape into the native caller.</summary>
    private nint? Handle(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            switch (msg)
            {
                case CallbackMsg:
                    uint what = (uint)(lParam & 0xFFFF);
                    if (what is NIN_SELECT or NIN_KEYSELECT or WM_CONTEXTMENU) ShowMenu(unchecked((short)(wParam & 0xFFFF)), unchecked((short)((wParam >> 16) & 0xFFFF)));   // signed: a monitor left of or above the main one has negative coordinates
                    return 0;
                case ShowMsg:
                    TraySnapshot? next;
                    lock (_gate) { next = _pending; _pending = null; }
                    if (next is not null) Apply(next);
                    return 0;
                case NoticeMsg:
                    while (_notices.TryDequeue(out var notice)) _sender.Send(notice);
                    return 0;
                case CloseMsg:
                    Close();
                    return 0;
                case WM_DESTROY:
                    _closed = true;
                    PostQuitMessage(0);
                    return 0;
                default:
                    if (msg != _taskbarCreated || _taskbarCreated == 0) return null;
                    // Explorer restarted (or started after the service): its icons are gone, add ours again.
                    _added = false;
                    _shownKey = null;
                    _shownTip = "";
                    AddIcon();
                    return 0;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log($"tray: message 0x{msg:X} failed: {e.GetType().Name}: {e.Message}");
            return 0;
        }
    }

    private unsafe NotifyIconData Data(uint flags) => new()
    {
        cbSize = (uint)sizeof(NotifyIconData),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = flags,
    };

    private static int IconSize()
    {
        int size = GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem());
        return size >= 8 ? size : 16;
    }

    private unsafe void AddIcon()
    {
        var key = TrayIconRenderer.Key(_shown, IconSize());
        if (key != _shownKey) SwapIcon(key);
        _shownTip = TrayText.Tooltip(_shown, _zone);
        var d = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        d.uCallbackMessage = CallbackMsg;
        d.hIcon = _icon;
        Copy(_shownTip, d.szTip, 128);
        _added = Shell_NotifyIcon(NIM_ADD, &d);
        if (!_added)
        {
            if (!_waitLogged) _log("tray: the taskbar is not there yet; the icon comes when it starts");
            _waitLogged = true;
            return;
        }
        d.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, &d);
        _log("tray: icon shown");
    }

    /// <summary>Draws the snapshot; the icon is rebuilt only when its picture changed, the tooltip only when its text did.</summary>
    private unsafe void Apply(TraySnapshot s)
    {
        _shown = s;
        if (!_added) return;   // TaskbarCreated adds it with this snapshot
        uint flags = 0;
        var key = TrayIconRenderer.Key(s, IconSize());
        nint old = 0;
        if (key != _shownKey)
        {
            old = _icon;
            if (SwapIcon(key)) flags |= NIF_ICON;
            else old = 0;
        }
        string tip = TrayText.Tooltip(s, _zone);
        if (!string.Equals(tip, _shownTip, StringComparison.Ordinal)) flags |= NIF_TIP | NIF_SHOWTIP;
        if (flags == 0) return;
        var d = Data(flags);
        d.hIcon = _icon;
        Copy(tip, d.szTip, 128);
        if (Shell_NotifyIcon(NIM_MODIFY, &d)) _shownTip = tip;
        if (old != 0) DestroyIcon(old);
    }

    /// <summary>Builds the icon for <paramref name="key"/>; the old handle is the caller's to destroy. false = kept the old one.</summary>
    private bool SwapIcon(IconKey key)
    {
        nint made = MakeIcon(TrayIconRenderer.Render(key), key.Size);
        if (made == 0)
        {
            _log("tray: the icon could not be made");
            return false;
        }
        if (_icon != 0 && !_added) DestroyIcon(_icon);   // not shown anywhere yet: nothing still points at it
        _icon = made;
        _shownKey = key;
        return true;
    }

    private static unsafe nint MakeIcon(uint[] pixels, int size)
    {
        var header = new BitmapInfoHeader { biSize = (uint)sizeof(BitmapInfoHeader), biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32 };
        void* bits;
        nint color = CreateDIBSection(0, &header, 0, &bits, 0, 0);
        if (color == 0) return 0;
        // The colour bitmap's alpha decides; the mask must exist all the same (rows padded to 16 bits, all zero).
        int maskBytes = (size + 15) / 16 * 2 * size;
        byte* maskBits = stackalloc byte[maskBytes];
        new Span<byte>(maskBits, maskBytes).Clear();
        nint mask = CreateBitmap(size, size, 1, 1, maskBits);
        try
        {
            fixed (uint* src = pixels) Buffer.MemoryCopy(src, bits, (long)size * size * 4, (long)pixels.Length * 4);
            var info = new IconInfo { fIcon = 1, hbmMask = mask, hbmColor = color };
            return mask == 0 ? 0 : CreateIconIndirect(&info);
        }
        finally
        {
            DeleteObject(color);
            if (mask != 0) DeleteObject(mask);
        }
    }

    /// <summary>Shows the notice as a balloon of the icon; false (logged) when it was not shown.</summary>
    private unsafe bool Balloon(TrayNotice n)
    {
        if (!_added)
        {
            _log($"tray: notice not shown (no icon): {n.Title}");
            return false;
        }
        var d = Data(NIF_INFO);
        Copy(n.Title, d.szInfoTitle, 64);
        Copy(n.Text, d.szInfo, 256);
        d.dwInfoFlags = NIIF_RESPECT_QUIET_TIME | n.Level switch
        {
            NoticeLevel.Error => NIIF_ERROR,
            NoticeLevel.Warning => NIIF_WARNING,
            _ => NIIF_INFO,
        };
        if (Shell_NotifyIcon(NIM_MODIFY, &d)) return true;
        _log($"tray: notice not shown: {n.Title}");
        return false;
    }

    private void ShowMenu(int x, int y)
    {
        var modes = _modes.Modes;
        var items = TrayMenu.Build(modes, _modes.Current);
        nint menu = CreatePopupMenu();
        if (menu == 0) return;
        int id;
        try
        {
            foreach (var item in items)
                AppendMenu(menu, item.Separator ? MF_SEPARATOR : MF_STRING, (nuint)item.Id, item.Separator ? null : item.Text);
            var radios = items.Where(i => i.Radio).ToList();
            if (radios.FirstOrDefault(i => i.Checked) is { } chosen)
                CheckMenuRadioItem(menu, (uint)radios[0].Id, (uint)radios[^1].Id, (uint)chosen.Id, MF_BYCOMMAND);
            // The menu closes on a click elsewhere only when its owner is the foreground window.
            SetForegroundWindow(_hwnd);
            id = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON, x, y, _hwnd, 0);
            PostMessage(_hwnd, WM_NULL, 0, 0);
        }
        finally { DestroyMenu(menu); }
        Run(TrayMenu.Resolve(id, modes));
    }

    private void Run((TrayCommand Command, string? ModeId) choice)
    {
        switch (choice.Command)
        {
            case TrayCommand.SelectMode:
                // Off the tray thread: the service's gate may be held by a poll. A choice runs to its end once made.
                string? mode = choice.ModeId;
                _ = Task.Run(async () =>
                {
                    try { await _modes.SelectAsync(mode, CancellationToken.None); }
                    catch (Exception e) when (e is not OperationCanceledException) { _log($"tray: mode {mode ?? "auto"} failed: {e.GetType().Name}: {e.Message}"); }
                });
                break;
            case TrayCommand.OpenPanel:
                _ = Task.Run(OpenPanel);
                break;
            case TrayCommand.ExitTray:
                _log("tray: closed from its menu until the next start");
                Close();
                break;
            default:
                break;
        }
    }

    private void OpenPanel()
    {
        try { using var _ = Process.Start(new ProcessStartInfo(_panel.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { _log($"tray: the panel did not open ({e.GetType().Name}: {e.Message})"); }
    }

    private unsafe void Close()
    {
        _closed = true;
        if (_added)
        {
            var d = Data(0);
            Shell_NotifyIcon(NIM_DELETE, &d);
            _added = false;
        }
        DestroyWindow(_hwnd);
    }
}
