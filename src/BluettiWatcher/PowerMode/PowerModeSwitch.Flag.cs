using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace BluettiWatcher.PowerMode;

/// <summary>The station's reading in the flag.</summary>
internal sealed class PmBattery
{
    public double Soc { get; set; }
    public string At { get; set; } = "";
    public DateTimeOffset? AtAt { get; set; }
    public string Level { get; set; } = "";
    public bool? Mains { get; set; }
    public double? HibernateAt { get; set; }
}

/// <summary>What an emergency from the station replaced: the user's eco.</summary>
internal sealed record PmReplaced(string Since, string Until, DateTimeOffset UntilAt);

/// <summary>A flag as read (Read-PowerModeFlag) or as written (Write-Flag's answer).</summary>
internal sealed class PmFlag
{
    /// <summary>The mode in force now: an eco past its <c>until</c> reads normal.</summary>
    public string Mode { get; set; } = "normal";
    /// <summary>The mode as written.</summary>
    public string Raw { get; set; } = "normal";
    public string Since { get; set; } = "";
    public string RawSince { get; set; } = "";
    public DateTimeOffset? At { get; set; }
    public string Until { get; set; } = "";
    public DateTimeOffset? UntilAt { get; set; }
    public bool Expired { get; set; }
    public string PrevPlan { get; set; } = "";
    public string Source { get; set; } = "manual";
    public string By { get; set; } = "";
    public PmBattery? Battery { get; set; }
    public PmReplaced? Replaced { get; set; }
    public double? PrevGpuLimitW { get; set; }
    public double? GpuCapW { get; set; }
}

/// <summary>What Write-Flag takes: an empty <see cref="Since"/> = now; <see cref="Until"/> or else <see cref="UntilAt"/>.</summary>
internal sealed class PmWrite
{
    public string Mode { get; set; } = "normal";
    public string Since { get; set; } = "";
    public string Until { get; set; } = "";
    public DateTimeOffset? UntilAt { get; set; }
    public string Source { get; set; } = "";
    public string By { get; set; } = "";
    public string PrevPlan { get; set; } = "";
    public PmReplaced? Replaced { get; set; }
    public PmBattery? Battery { get; set; }
    public double? PrevGpuLimitW { get; set; }
    public double? GpuCapW { get; set; }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed partial class PowerModeSwitch
{
    private const int WriteTries = 10;

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SincePattern();

    [GeneratedRegex(@"^[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GuidPattern();

    private static bool Truthy(double? w) => w is { } v && v != 0;

    // ---- the flag folder's ACL ---------------------------------------------------------------------------------------

    private enum Presence { Present, Absent, Unknown }

    private static Presence Probe(string path)
    {
        try { _ = File.GetAttributes(path); return Presence.Present; }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { Skip(e); return Presence.Absent; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { Skip(e); return Presence.Unknown; }
    }

    /// <summary>
    /// "" = one item of the flag (the folder or power-mode.json) is the user's, else the reason. Never changes anything: not a
    /// reparse point, owned by the user, Administrators or SYSTEM, the folder protected, the user holding Modify (on the
    /// folder: for its files too), every other allow entry SYSTEM, Administrators or an extra reader without write.
    /// </summary>
    private string AclReason(string path, bool isDir)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return $"{path} cannot be read ({e.Message})"; }
        if ((attributes & FileAttributes.ReparsePoint) != 0) return $"{path} is a reparse point (a junction or a link)";
        FileSystemSecurity acl;
        try
        {
            const AccessControlSections sections = AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access;
            acl = isDir ? new DirectoryInfo(path).GetAccessControl(sections) : new FileInfo(path).GetAccessControl(sections);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SystemException) { return $"the ACL of {path} cannot be read ({e.Message})"; }
        string me = MeSid();
        string owner = OwnerOf(acl);
        if (owner != me && owner != AdministratorsSid && owner != SystemSid) return $"{path} is owned by {owner} (only the user, Administrators or SYSTEM may own it)";
        if (isDir && !acl.AreAccessRulesProtected) return "it inherits its ACL (inheritance must be off)";
        var readers = ExtraReaderSids();
        // WriteData, AppendData, WriteExtendedAttributes, DeleteSubdirectoriesAndFiles, WriteAttributes, Delete,
        // ChangePermissions, TakeOwnership, GenericAll, GenericWrite
        const long write = 0x2 | 0x4 | 0x10 | 0x40 | 0x100 | 0x10000 | 0x40000 | 0x80000 | 0x10000000 | 0x40000000;
        const long modify = (long)FileSystemRights.Modify;
        bool userOk = false;
        foreach (FileSystemAccessRule r in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (r.AccessControlType != AccessControlType.Allow) continue;
            string sid = r.IdentityReference.Value;
            long rights = (int)r.FileSystemRights;
            if (sid is SystemSid or AdministratorsSid) continue;
            if (sid == me)
            {
                bool reaches = !isDir || ((r.InheritanceFlags & InheritanceFlags.ObjectInherit) != 0 && r.PropagationFlags == PropagationFlags.None);
                if ((rights & modify) == modify && reaches) userOk = true;
                continue;
            }
            if (readers.Contains(sid))
            {
                if ((rights & write) != 0) return $"the extra reader {sid} may write there";
                continue;
            }
            return $"{sid} has rights there (only the user, SYSTEM, Administrators and the extra readers' read may)";
        }
        return userOk ? "" : $"the user {me} holds no Modify there";
    }

    private static string OwnerOf(FileSystemSecurity acl)
    {
        try { return acl.GetOwner(typeof(SecurityIdentifier))?.Value ?? ""; }
        catch (Exception e) when (e is IdentityNotMappedException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { Skip(e); return ""; }
    }

    /// <summary>"" = the flag folder and the flag file in it (when there) are the user's, else the reason.</summary>
    private string FlagDirAclReason(string dir)
    {
        string why = AclReason(dir, true);
        if (why.Length > 0) return why;
        string file = Path.Combine(dir, Path.GetFileName(DefaultFlagPath));
        return Probe(file) == Presence.Present ? AclReason(file, false) : "";   // a dangling link too
    }

    /// <summary>
    /// Before a write: the flag's directory exists, or - the configured flag only - is created by the user with its own ACL
    /// (the user Modify, SYSTEM and Administrators Full control, protected). An existing configured folder is checked, never
    /// changed. Throws 'directory missing' / 'not the user's'.
    /// </summary>
    private void InitFlagDir(string flagPath)
    {
        string full = Path.GetFullPath(flagPath);
        string dir = Path.GetDirectoryName(full) ?? full;
        bool isDefault = string.Equals(full, DefaultFlagPath, StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(dir))
        {
            if (isDefault && FlagDirAclReason(dir) is { Length: > 0 } why)
                throw new InvalidOperationException($"the flag folder {dir} is not the user's: {why}; nothing written (fix the ACL by hand)");
            return;
        }
        if (!isDefault) throw new InvalidOperationException($"directory missing: {dir}");
        var me = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("the user's SID cannot be read");
        if (me.Value is SystemSid or "S-1-5-19" or "S-1-5-20" || ExtraReaderSids().Contains(me.Value))
            throw new InvalidOperationException($"the flag folder {dir} is created by the user only, not by {me.Value}");
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(true, false);
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(SystemSid), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(AdministratorsSid), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.Modify, inherit, PropagationFlags.None, AccessControlType.Allow));
        // Created WITH the ACL, so the folder is never open under the inherited one, not even for a moment.
        new DirectoryInfo(dir).Create(sec);
    }

    // ---- the writers' lock -----------------------------------------------------------------------------------------------

    /// <summary>The writers' lock, held: released and closed on dispose.</summary>
    private sealed class FlagLock(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    /// <summary>
    /// One writer at a time across processes (the PowerShell scripts, other tools, the watcher): POWER_MODE_MUTEX, held by
    /// this thread · null = not taken, <paramref name="error"/> says why.
    /// </summary>
    private FlagLock? TryEnterLock(out string error, int timeoutMs = 10000)
    {
        error = "";
        Mutex? m = null;
        try
        {
            m = new Mutex(false, MutexName);
            bool owned;
            try { owned = m.WaitOne(timeoutMs); }
            catch (AbandonedMutexException) { owned = true; }   // a writer that died: ours now
            if (owned)
            {
                var held = new FlagLock(m);
                m = null;
                return held;
            }
            error = $"another writer holds the power-mode flag lock ({Num(timeoutMs / 1000)} s)";
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or WaitHandleCannotBeOpenedException)
        {
            error = e.Message;
            return null;
        }
        finally { m?.Dispose(); }
    }

    // ---- read --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The flag: a missing file = normal · null = the file is there and cannot be trusted. The configured flag is read only
    /// while its folder is the user's.
    /// </summary>
    internal PmFlag? ReadFlag(string path)
    {
        var flag = new PmFlag();
        if (path.Length > 0 && IsDefaultFlag(path))
        {
            if (Probe(path) == Presence.Unknown) return null;
            string flagDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
            if ((Directory.Exists(flagDir) || File.Exists(flagDir)) && FlagDirAclReason(flagDir).Length > 0) return null;
        }
        if (!File.Exists(path) && !Directory.Exists(path)) return flag;
        string text;
        try { text = ReadText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { Skip(e); return null; }
        return ParseFlag(text, flag, DateTimeOffset.Now);
    }

    private static bool TryTime(object? value, out string text, out DateTimeOffset at)
    {
        text = value as string ?? "";
        at = default;
        return value is string s && SincePattern().IsMatch(s) && DateTimeOffset.TryParse(s, Inv, System.Globalization.DateTimeStyles.None, out at);
    }

    /// <summary>The flag text as Read-PowerModeFlag reads it into <paramref name="flag"/>; null = not a valid flag.</summary>
    internal static PmFlag? ParseFlag(string text, PmFlag flag, DateTimeOffset now)
    {
        var j = ParseObject(text);
        if (j is null) return null;
        if (Get(j, "mode") is not string mode || Get(j, "since") is not string) return null;
        if (mode is not ("emergency" or "normal" or "eco")) return null;
        if (!TryTime(Get(j, "since"), out var since, out var at)) return null;
        flag.Mode = mode;
        flag.Raw = mode;
        flag.Since = since;
        flag.RawSince = since;
        flag.At = at;
        if (Get(j, "prevPlan") is string pp && GuidPattern().IsMatch(pp)) flag.PrevPlan = pp.ToLowerInvariant();
        if (Get(j, "prevGpuLimitW") is double prevW && prevW > 0)
        {
            flag.PrevGpuLimitW = prevW;
            if (Get(j, "gpuCapW") is double capW && capW > 0) flag.GpuCapW = capW;
        }
        if (Get(j, "source") is "station") flag.Source = "station";
        if (Get(j, "by") is string by && by is "manual" or "station") flag.By = by;
        if (Get(j, "battery") is Dictionary<string, object?> b && Get(b, "soc") is double soc && soc >= 0 && soc <= 100
            && Get(b, "at") is string batAtText && SincePattern().IsMatch(batAtText)
            && Get(b, "level") is string level && level is "ok" or "low" or "critical" or "hibernating"
            && TryTime(batAtText, out _, out var batAt))
        {
            flag.Battery = new PmBattery
            {
                Soc = soc, At = batAtText, AtAt = batAt, Level = level,
                Mains = Get(b, "mains") as bool?, HibernateAt = Get(b, "hibernateAt") as double?,
            };
        }
        if (Get(j, "replaced") is Dictionary<string, object?> r && Get(r, "since") is string rSince && SincePattern().IsMatch(rSince)
            && TryTime(Get(r, "until"), out var rUntil, out var rUntilAt))
        {
            flag.Replaced = new PmReplaced(rSince, rUntil, rUntilAt);
        }
        if (mode != "eco") return flag;
        // An eco the station set lasts while mains is off: no `until`, nothing to expire.
        if (Get(j, "until") is null && flag.Source == "station") return flag;
        if (Get(j, "until") is not string || !TryTime(Get(j, "until"), out var untilText, out var untilAt)) return null;
        flag.Until = untilText;
        flag.UntilAt = untilAt;
        // Past `until` the flag is normal since that instant: no process has to be alive to switch it.
        if (now >= untilAt)
        {
            flag.Mode = "normal";
            flag.Since = untilText;
            flag.At = untilAt;
            flag.Expired = true;
        }
        return flag;
    }

    // ---- write -------------------------------------------------------------------------------------------------------

    private static string FormatBattery(PmBattery b)
    {
        string mains = b.Mains is { } on ? $", \"mains\": {(on ? "true" : "false")}" : "";
        string hib = b.HibernateAt is { } h ? $", \"hibernateAt\": {Watts(h)}" : "";
        return $"{{ \"soc\": {Watts(b.Soc)}, \"at\": \"{b.At}\", \"level\": \"{b.Level}\"{mains}{hib} }}";
    }

    /// <summary>The fields of a flag as the text between its braces; an empty field is left out, <c>source</c> only for the station.</summary>
    internal static string FormatFields(string mode, string since, string until, string source, string by, string prevPlan,
        double? prevGpuLimitW, double? gpuCapW, PmReplaced? replaced, PmBattery? battery)
    {
        var parts = new List<string> { $"\"mode\": \"{mode}\"", $"\"since\": \"{since}\"" };
        if (until.Length > 0) parts.Add($"\"until\": \"{until}\"");
        if (source == "station") parts.Add("\"source\": \"station\"");
        if (by.Length > 0) parts.Add($"\"by\": \"{by}\"");
        if (prevPlan.Length > 0) parts.Add($"\"prevPlan\": \"{prevPlan}\"");
        if (Truthy(prevGpuLimitW))
        {
            parts.Add($"\"prevGpuLimitW\": {Watts(prevGpuLimitW!.Value)}");
            if (Truthy(gpuCapW)) parts.Add($"\"gpuCapW\": {Watts(gpuCapW!.Value)}");
        }
        if (replaced is not null) parts.Add($"\"replaced\": {{ \"mode\": \"eco\", \"since\": \"{replaced.Since}\", \"until\": \"{replaced.Until}\" }}");
        if (battery is not null) parts.Add($"\"battery\": {FormatBattery(battery)}");
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Writes the flag: a temp file in its directory moved over it (atomic, LF, no BOM), retried for under a second while a
    /// reader holds it open. The caller holds the lock.
    /// </summary>
    private PmFlag WriteFlag(string path, PmWrite f)
    {
        var now = DateTimeOffset.Now;
        string since = Iso(now);
        if (f.Since.Length > 0)
        {
            since = f.Since;
            now = DateTimeOffset.Parse(f.Since, Inv);
        }
        string untilText = f.Until;
        if (f.UntilAt is { } ua && untilText.Length == 0) untilText = Iso(ua);
        InitFlagDir(path);
        string text = "{ " + FormatFields(f.Mode, since, untilText, f.Source, f.By, f.PrevPlan, f.PrevGpuLimitW, f.GpuCapW, f.Replaced, f.Battery) + " }\n";
        ReplaceFile(path, text, (tmp, target) => File.Move(tmp, target, true), "the flag file could not be replaced in 10 tries (held open by another process?): ");
        return new PmFlag
        {
            Mode = f.Mode, Raw = f.Mode, Since = since, RawSince = since, At = now, Until = untilText, UntilAt = f.UntilAt,
            PrevPlan = f.PrevPlan, Source = f.Source == "station" ? "station" : "manual", By = f.By, Replaced = f.Replaced,
            Battery = f.Battery, PrevGpuLimitW = f.PrevGpuLimitW, GpuCapW = f.GpuCapW,
        };
    }

    private static void ReplaceFile(string path, string text, Action<string, string> move, string failure)
    {
        string tmp = $"{path}.{Num(Environment.ProcessId)}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            string last = "";
            for (int attempt = 1; attempt <= WriteTries; attempt++)
            {
                try
                {
                    move(tmp, path);
                    return;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    last = e.Message;
                    if (attempt < WriteTries) Thread.Sleep(100);
                }
            }
            throw new IOException(failure + last);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    // ---- the status line ------------------------------------------------------------------------------------------------

    private string StatusLine(PmFlag flag)
    {
        if (flag.Mode == "emergency")
        {
            string over = flag.Replaced is { } r && r.UntilAt > DateTimeOffset.Now ? $" (over the user's eco until {FormatTime(r.UntilAt)})" : "";
            return $"power mode: EMERGENCY since {FormatTime(flag.At!.Value)}{over}";
        }
        if (flag.Mode == "eco" && flag.UntilAt is null) return $"power mode: ECO since {FormatTime(flag.At!.Value)} while mains is off (station; everything else as usual)";
        if (flag.Mode == "eco") return $"power mode: ECO since {FormatTime(flag.At!.Value)} until {FormatTime(flag.UntilAt!.Value)} (everything else as usual)";
        if (flag.Expired) return $"power mode: normal (eco ended {FormatTime(flag.At!.Value)})";
        return "power mode: normal";
    }

    /// <summary>The status line, or - when the local time cannot be formatted - the raw since plus a warn: line. Never throws.</summary>
    private List<string> StatusLines(PmFlag flag, string suffix = "")
    {
        try { return [StatusLine(flag) + suffix]; }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            string raw = flag.Mode == "emergency" ? $"power mode: EMERGENCY since {flag.Since}"
                : flag.Mode == "eco" ? $"power mode: ECO since {flag.Since}{(flag.Until.Length > 0 ? $" until {flag.Until}" : "")}"
                : flag.Expired ? $"power mode: normal (eco ended {flag.Since})"
                : "power mode: normal";
            return [raw + suffix, $"warn: local time unavailable ({e.Message})"];
        }
    }
}
