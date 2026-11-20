<#
shortcut.ps1 - the "Bluetti Watcher" shortcuts (install.ps1 step 7). Functions only: install.ps1 dot-sources it,
tools/test-install.ps1 runs them on temp files. Dot-sourcing changes nothing.

The Start-menu shortcut carries the app id (System.AppUserModel.ID) the service's notifications are shown under: Windows
keeps a notification in its notification centre only for an app id a Start-menu shortcut names. Without it the service
falls back to the tray balloon, a banner Windows keeps nowhere.
#>

# The service's notifications use the same id (src/BluettiWatcher/Tray/Toasts.cs AppId).
$script:WatcherAppId = 'BluettiWatcher'

# IPropertyStore on a .lnk through the shell's property handler: Windows PowerShell's WScript.Shell cannot write the app id.
$script:WatcherShortcutType = $null
function Get-WatcherShortcutType {
    if (-not $script:WatcherShortcutType) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WatcherShortcutAppId
{
    [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid Fmtid; public uint Pid; }
    [StructLayout(LayoutKind.Sequential)] struct PropVariant { public ushort Vt; ushort r1, r2, r3; public IntPtr P; IntPtr p2; }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHGetPropertyStoreFromParsingName(string path, IntPtr bind, int flags, ref Guid iid, out IPropertyStore store);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant v);
    const int VtLpwstr = 31;
    static PropertyKey AppIdKey() { return new PropertyKey { Fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Pid = 5 }; }
    static IPropertyStore Open(string path, int flags)
    {
        Guid iid = typeof(IPropertyStore).GUID;
        IPropertyStore store;
        SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, flags, ref iid, out store);
        return store;
    }
    public static void Set(string path, string appId)
    {
        IPropertyStore store = Open(path, 2);   // GPS_READWRITE
        try
        {
            PropertyKey key = AppIdKey();
            PropVariant v = new PropVariant { Vt = VtLpwstr, P = Marshal.StringToCoTaskMemUni(appId) };
            try { store.SetValue(ref key, ref v); store.Commit(); }
            finally { PropVariantClear(ref v); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }
    public static string Get(string path)
    {
        IPropertyStore store = Open(path, 0);   // GPS_DEFAULT
        try
        {
            PropertyKey key = AppIdKey();
            PropVariant v;
            store.GetValue(ref key, out v);
            try { return v.Vt == VtLpwstr ? Marshal.PtrToStringUni(v.P) : null; }
            finally { PropVariantClear(ref v); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }
}
'@
        $script:WatcherShortcutType = [WatcherShortcutAppId]
    }
    $script:WatcherShortcutType
}

# Writes the shortcut (replacing one there) to the browser in app mode on the dashboard; -AppId also sets its app id.
function Set-WatcherShortcut([string] $Path, [string] $Browser, [string] $Url, [string] $AppId = '') {
    $shell = New-Object -ComObject WScript.Shell
    $s = $shell.CreateShortcut($Path)
    $s.TargetPath = $Browser
    $s.Arguments = "--app=$Url"
    $s.Description = 'Bluetti Watcher dashboard'
    $s.Save()
    if ($AppId) { (Get-WatcherShortcutType)::Set($Path, $AppId) }
}

# The shortcut's app id; $null when it has none.
function Get-WatcherShortcutAppId([string] $Path) { (Get-WatcherShortcutType)::Get($Path) }
