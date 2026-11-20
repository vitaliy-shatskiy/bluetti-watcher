using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace BluettiWatcher.Modes.Broker;

/// <summary>
/// The broker's folder on disk. Every file is opened with FILE_FLAG_OPEN_REPARSE_POINT (a link is opened as itself and
/// reported, never followed) and judged by its handle: attributes, hard-link count, owner and access list. A request is
/// read and deleted through that same handle (delete on close), so a name swapped after the check is never acted on.
/// </summary>
internal sealed partial class WindowsBrokerInbox(BrokerPaths paths, TimeProvider time) : IBrokerInbox
{
    private const uint GenericRead = 0x80000000, ReadControl = 0x00020000, Delete = 0x00010000;
    private const uint ShareRead = 1, ShareDelete = 4, OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000, DeleteOnClose = 0x04000000;
    private const uint AttributeReparsePoint = 0x400, AttributeDirectory = 0x10;
    private const int MaxPending = 100;
    private static readonly TimeSpan ResponseLife = TimeSpan.FromMinutes(10);

    /// <summary>Rights that change a file: data, attributes, its access list or owner, deleting it (generic bits included).</summary>
    private const int WriteRights = 0x2 | 0x4 | 0x10 | 0x100 | 0x10000 | 0x40000 | 0x80000 | 0x40000000 | 0x10000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    public SecuredFile? ReadPolicy() => Open(paths.Policy, deleteAfter: false);

    public IReadOnlyList<string> PendingRequests()
    {
        try { return [.. Directory.EnumerateFiles(paths.Requests).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).Take(MaxPending)]; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    public SecuredFile? TakeRequest(string fileName)
    {
        if (fileName.Contains('\\', StringComparison.Ordinal) || fileName.Contains('/', StringComparison.Ordinal) || fileName.Contains(':', StringComparison.Ordinal)) return null;
        return Open(Path.Combine(paths.Requests, fileName), deleteAfter: true);
    }

    public void WriteResponse(string id, string json)
    {
        Directory.CreateDirectory(paths.Responses);
        string file = Path.Combine(paths.Responses, id + ".json");
        File.WriteAllText(file + ".tmp", json);
        File.Move(file + ".tmp", file, overwrite: true);
        foreach (var old in Directory.EnumerateFiles(paths.Responses))
        {
            try { if (time.GetUtcNow().UtcDateTime - File.GetLastWriteTimeUtc(old) > ResponseLife) File.Delete(old); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _ = e; /* the next run prunes it */ }
        }
    }

    private static SecuredFile? Open(string path, bool deleteAfter)
    {
        uint access = GenericRead | ReadControl | (deleteAfter ? Delete : 0);
        uint flags = OpenReparsePoint | (deleteAfter ? DeleteOnClose : 0);
        using var handle = CreateFileW(path, access, ShareRead | ShareDelete, 0, OpenExisting, flags, 0);
        if (handle.IsInvalid) return null;
        if (!GetFileInformationByHandle(handle, out var info)) return null;
        bool plain = (info.Attributes & (AttributeReparsePoint | AttributeDirectory)) == 0 && info.Links == 1;
        try
        {
            using var stream = new FileStream(handle, FileAccess.Read, 1, false);
            var security = stream.GetAccessControl();
            string owner = security.GetOwner(typeof(SecurityIdentifier))?.Value ?? "";
            var writers = new List<string>();
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow || rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue;
                string sid = rule.IdentityReference.Value;
                if (((int)rule.FileSystemRights & WriteRights) != 0 && !BrokerCore.AdminSids.Contains(sid) && !writers.Contains(sid)) writers.Add(sid);
            }
            if (info.SizeHigh != 0 || info.SizeLow > BrokerRequest.MaxBytes * 4) return new SecuredFile("", owner, false, writers);
            var buffer = new byte[info.SizeLow];
            stream.ReadExactly(buffer);
            return new SecuredFile(System.Text.Encoding.UTF8.GetString(buffer), owner, plain, writers);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { _ = e; return null; }
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
}
