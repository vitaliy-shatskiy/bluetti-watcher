// The flag folder's ACL checks against the exe's own flag code (BluettiWatcher.exe power-mode, or the forwarders over it): the
// cases PmConfigTests runs against power-mode-flag.ps1, which still ships for tools that dot-source it. The configured flag
// (POWER_MODE_FILE) is a private temp path; no live flag, folder or plan is touched.
using static BluettiWatcher.Tests.PowerModeScript.Pm;

namespace BluettiWatcher.Tests.PowerModeScript;

public sealed class PowerModeFlagAclExeTests
{
    private const string Refused = @"^power mode: COULD NOT RUN: the flag folder .* is not the user's: ";

    /// <summary>A world of its own (the ACL cases leave folders the test user cannot delete until their ACL is reset); the configured flag in it does not exist yet.</summary>
    private static (string Dir, string Flag, string Folder) AclWorld(Cleanup t)
    {
        string dir = Path.Combine(Tmp, "power-mode-cli-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)));
        Directory.CreateDirectory(Path.Combine(dir, "pd"));
        t.After(() =>
        {
            Icacls(dir, "/reset", "/T", "/Q");
            RemoveTree(dir);
        });
        string folder = Path.Combine(dir, "pd", "BluettiWatcher");
        return (dir, Path.Combine(folder, "power-mode.json"), folder);
    }

    private static async Task<Res> BuildAsync(string folder, string setup)
    {
        Res r = await SpawnAsync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", $"$ErrorActionPreference = 'Stop'; {AclPs}$d = $env:FLAG_DIR; {setup}"], Merge(BaseEnv(), Env(("FLAG_DIR", folder)))).ConfigureAwait(false);
        Eq(0, r.Status, "building the folder: " + r.Out + r.Err);
        return r;
    }

    /// <summary>A flag folder whose ACL the setup builds by hand, then `on` against it: the exe's answer.</summary>
    private static async Task<Res> AclCaseAsync(Cleanup t, string setup, Dictionary<string, string>? env = null, bool precreate = true)
    {
        (_, string flag, string folder) = AclWorld(t);
        if (precreate) Directory.CreateDirectory(folder);
        await BuildAsync(folder, setup);
        return await CliAsync(["on"], flag, env);
    }

    [Fact(DisplayName = "exe: the configured folder is created with its own ACL (the user Modify, SYSTEM and Administrators Full, nothing inherited)")]
    public async Task The_configured_folder_is_created_with_its_own_ACL()
    {
        await using Cleanup t = new();
        (_, string flag, string folder) = AclWorld(t);
        Res on = await CliAsync(["on"], flag);
        Eq(0, on.Status, on.Out + on.Err);
        Eq(0, (await CliAsync(["on"], flag)).Status);   // the folder it created passes its own check
        const string body = "$acl = Get-Acl -LiteralPath $env:FLAG_DIR; \"protected=$($acl.AreAccessRulesProtected)\"; " +
            "$me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value; " +
            "$acl.Access | ForEach-Object { $s = $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value; if ($s -ceq $me) { $s = 'me' }; \"$s|$($_.FileSystemRights)|$($_.InheritanceFlags)|$($_.IsInherited)\" } | Sort-Object";
        Res acl = Norm(await SpawnAsync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", $"$ErrorActionPreference = 'Stop'; {body}"], Merge(BaseEnv(), Env(("FLAG_DIR", folder)))));
        Eq(new Res(0, string.Join('\n',
            "protected=True",
            "me|Modify, Synchronize|ContainerInherit, ObjectInherit|False",
            "S-1-5-18|FullControl|ContainerInherit, ObjectInherit|False",
            "S-1-5-32-544|FullControl|ContainerInherit, ObjectInherit|False"), ""), acl);
    }

    [Fact(DisplayName = "exe: a missing folder that is not the configured one is never created")]
    public async Task A_missing_folder_that_is_not_the_configured_one_is_never_created()
    {
        await using Cleanup t = new();
        (string dir, string flag, _) = AclWorld(t);
        string other = Path.Combine(dir, "pd", "other", "power-mode.json");
        Res r = await CliAsync(["on", "-Path", other], flag);
        Eq(2, r.Status, r.Out + r.Err);
        Match("directory missing", r.Out);
        Assert.False(Directory.Exists(Path.Combine(dir, "pd", "other")));
    }

    [Fact(DisplayName = "exe: a folder that inherits its ACL refuses the write and keeps its ACL")]
    public async Task A_folder_that_inherits_its_ACL_refuses_the_write()
    {
        await using Cleanup t = new();
        Res r = await AclCaseAsync(t, "$null");
        Eq(2, r.Status, r.Out + r.Err);
        Match(Refused + @"it inherits its ACL \(inheritance must be off\)", r.Out);
    }

    [Fact(DisplayName = "exe: a folder where Users hold read refuses the write")]
    public async Task A_folder_where_Users_hold_read_refuses_the_write()
    {
        await using Cleanup t = new();
        Res r = await AclCaseAsync(t, "Set-TestAcl $d 'Modify' @('S-1-5-32-545=ReadAndExecute')");
        Eq(2, r.Status, r.Out + r.Err);
        Match(Refused + ".*S-1-5-32-545 has rights there", r.Out);
    }

    [Fact(DisplayName = "exe: an extra reader may read, never write")]
    public async Task An_extra_reader_may_read_never_write()
    {
        await using Cleanup t = new();
        Dictionary<string, string> users = Env(("POWER_MODE_EXTRA_READERS", "Users"));
        Res read = await AclCaseAsync(t, "Set-TestAcl $d 'Modify' @('S-1-5-32-545=ReadAndExecute')", users);
        Eq(0, read.Status, read.Out + read.Err);
        Match("^power mode: EMERGENCY since ", read.Out);
        Res write = await AclCaseAsync(t, "Set-TestAcl $d 'Modify' @('S-1-5-32-545=Modify')", users);
        Eq(2, write.Status, write.Out + write.Err);
        Match(Refused + ".*the extra reader S-1-5-32-545 may write there", write.Out);
    }

    [Fact(DisplayName = "exe: a folder where the user holds only read refuses the write")]
    public async Task A_folder_where_the_user_holds_only_read_refuses_the_write()
    {
        await using Cleanup t = new();
        Res r = await AclCaseAsync(t, "Set-TestAcl $d 'ReadAndExecute' @()");
        Eq(2, r.Status, r.Out + r.Err);
        Match(Refused + ".*holds no Modify there", r.Out);
    }

    [Fact(DisplayName = "exe: a folder owned by someone else refuses the write, however good its DACL")]
    public async Task A_folder_owned_by_someone_else_refuses_the_write()
    {
        await using Cleanup t = new();
        // The owner is the real user; the seam (POWER_MODE_TEST=1) makes the running user someone else.
        Res r = await AclCaseAsync(t, "Set-TestAcl $d 'Modify' @(); $o = Get-Acl -LiteralPath $d; $o.SetOwner([Security.Principal.WindowsIdentity]::GetCurrent().User); Set-Acl -LiteralPath $d -AclObject $o", Env(("POWER_MODE_ME_SID", "S-1-5-21-1-2-3-1001")));
        Eq(2, r.Status, r.Out + r.Err);
        Match(Refused + ".* is owned by S-1-5-21-", r.Out);
    }

    [Fact(DisplayName = "exe: a junction in place of the folder refuses the write")]
    public async Task A_junction_in_place_of_the_folder_refuses_the_write()
    {
        await using Cleanup t = new();
        Res r = await AclCaseAsync(t, "$tgt = Join-Path (Split-Path $d) 'target'; New-Item -ItemType Directory $tgt | Out-Null; Set-TestAcl $tgt 'Modify' @(); New-Item -ItemType Junction -Path $d -Target $tgt | Out-Null", null, precreate: false);
        Eq(2, r.Status, r.Out + r.Err);
        Match(Refused + ".* is a reparse point", r.Out);
    }

    [Fact(DisplayName = "exe: a flag file carrying an extra entry refuses the write, and readers treat it as untrusted")]
    public async Task A_flag_file_carrying_an_extra_entry_refuses_the_write()
    {
        await using Cleanup t = new();
        (_, string flag, _) = AclWorld(t);
        Eq(0, (await CliAsync(["on"], flag)).Status);
        Res extra = await SpawnAsync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference = 'Stop'; $fa = Get-Acl -LiteralPath $env:FLAG_FILE; $fa.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-545'), 'Read', 'Allow'))); Set-Acl -LiteralPath $env:FLAG_FILE -AclObject $fa"], Merge(BaseEnv(), Env(("FLAG_FILE", flag))));
        Eq(0, extra.Status, extra.Out + extra.Err);
        Res write = await CliAsync(["off"], flag);
        Eq(2, write.Status, write.Out + write.Err);
        Match(Refused + ".*S-1-5-32-545 has rights there", write.Out);
        Res read = await CliAsync(["status"], flag);
        Eq(2, read.Status, read.Out + read.Err);
        Match("^power mode: COULD NOT READ ", read.Out);
    }

    [Fact(DisplayName = "exe: a reader refuses a configured folder that is not the user's (inheritance on): untrusted")]
    public async Task A_reader_refuses_a_configured_folder_that_is_not_the_users()
    {
        await using Cleanup t = new();
        (_, string flag, string folder) = AclWorld(t);
        Directory.CreateDirectory(folder);   // a plain mkdir: inherits the temp folder's ACL
        WriteText(flag, FlagText("normal"));
        Res r = await CliAsync(["status"], flag);
        Eq(2, r.Status, r.Out + r.Err);
        Match("^power mode: COULD NOT READ ", r.Out);
    }
}
