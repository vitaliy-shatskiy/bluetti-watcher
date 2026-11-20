<#
broker-task.ps1 - the admin broker (docs/design.md, power modes): a SYSTEM scheduled task "BluettiWatcherBroker" with no
trigger, started on demand by the unelevated service, that runs BluettiWatcher.exe --broker once and exits. Functions
only: install.ps1 dot-sources it (install.ps1 -Apply or -Broker -Apply, elevated). Dot-sourcing changes nothing. Once the
broker is registered, the old GPU cap tasks "<prefix>GpuCap" and "<prefix>GpuRestore" (power-mode.ps1 gpu-task install)
are removed: the broker's GPU limit replaces them. A failed registration keeps them.

What runs as SYSTEM takes nothing the user can write: the exe and the policy sit in %ProgramFiles%\BluettiWatcher\broker
(bin\ holds the exe's copy; the user's own bin under %LOCALAPPDATA% is never run elevated). The requests and responses
sit in their own root, %ProgramData%\BluettiWatcherBroker, never inside the power-mode flag's folder (the user may change
that one). Both roots: owned by Administrators, inheritance cut, Administrators and SYSTEM full control, the user read; in
requests\ the user may also add files (never folders); every child reset to inherit only that. Before anything changes,
no existing ancestor of either root may let the user rename, re-own, re-permission or add into it (else refused); after,
the ACLs are read back. An ACL that cannot be read refuses. The policy's user is the service task's principal, never the
elevated caller. A pre-release broker inside the flag folder is carried out (its policy and profiles only when
Administrators own them) and removed. The task's own security: Administrators and SYSTEM full control, the user read and
run (the same SDDL the GPU cap tasks use).
#>

$script:WatcherBrokerTask = 'BluettiWatcherBroker'

function Get-WatcherBrokerRoot { Join-Path $env:ProgramFiles 'BluettiWatcher\broker' }
function Get-WatcherBrokerExchange { Join-Path $env:ProgramData 'BluettiWatcherBroker' }
# Where a pre-release put the broker: inside the flag folder.
function Get-WatcherOldBrokerRoot { Join-Path $env:ProgramData 'BluettiWatcher\broker' }

# The SID of the user the service task runs as (its principal), $null when the task is missing or runs as no user: the
# broker takes that user's requests, whoever runs the elevated install.
function Get-WatcherServiceUserSid([string] $TaskName, [hashtable] $Ops = $null) {
    if (-not $Ops) { $Ops = Get-WatcherBrokerOps }
    $xml = "$(& $Ops.Query $TaskName)"
    if ($xml -notmatch '<UserId>([^<]+)</UserId>') { return $null }
    $id = $Matches[1].Trim()
    if ($id -notmatch '^S-1-5-21(-\d+){4}$') {
        try { $id = ([Security.Principal.NTAccount]$id).Translate([Security.Principal.SecurityIdentifier]).Value } catch { return $null }
    }
    if ($id -notmatch '^S-1-5-21(-\d+){4}$') { return $null }   # SYSTEM, a service account: no user
    $id
}

function New-WatcherBrokerTaskXml([string] $Exe) {
    $esc = { param($s) [Security.SecurityElement]::Escape($s) }
    return @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>Bluetti Watcher: the admin broker - runs the allow-listed requests of the power modes (services, GPU limit, driver profile) and exits</Description></RegistrationInfo>
  <Principals><Principal id="Author"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>Queue</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <ExecutionTimeLimit>PT2M</ExecutionTimeLimit>
  </Settings>
  <Actions Context="Author"><Exec><Command>$(& $esc $Exe)</Command><Arguments>--broker</Arguments></Exec></Actions>
</Task>
"@
}

# Administrators and SYSTEM full control, the user read and run - never change or delete it unelevated.
function Get-WatcherBrokerTaskSddl([string] $Sid) { "D:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;FRFX;;;$Sid)" }

# The policy a fresh install writes: the user whose requests the broker takes, nothing allowed yet.
function New-WatcherBrokerPolicyText([string] $Sid) {
    @{ version = 1; userSid = $Sid; services = @(); tasks = @(); profiles = @(); profileInspector = '' } | ConvertTo-Json -Compress
}

# The steps, as the dry run prints them and -Apply runs them.
function Get-WatcherBrokerSteps([string] $Root, [string] $SourceExe, [string] $User, [string] $Exchange = (Get-WatcherBrokerExchange), [string] $OldRoot = (Get-WatcherOldBrokerRoot)) {
    @(
        "check that no ancestor of $Root or $Exchange lets $User rename, re-own, re-permission or add into it (else refused, nothing changed)"
        "create $Root with profiles\, exports\, bin\ and $Exchange with requests\, responses\: Administrators and SYSTEM full control, $User read; requests\ also lets $User add files (never folders); owner Administrators throughout; the ACLs read back"
        "carry the policy and profiles Administrators own out of a pre-release broker in $OldRoot (inside the flag folder), then remove it"
        "copy $SourceExe to $Root\bin\BluettiWatcher.exe (SYSTEM runs this copy, never the one in the user's bin)"
        "write $Root\policy.json when there is none: $User's requests, no service, task or profile allowed (BluettiWatcher.exe --broker-allow adds them, elevated)"
        "register the task '$script:WatcherBrokerTask': SYSTEM, no trigger, started on demand, $Root\bin\BluettiWatcher.exe --broker, at most 2 minutes, queued; security: Administrators, SYSTEM, $User read and run; read back after registering"
    )
}

# The machine operations; a test passes fakes with the same keys.
function Get-WatcherBrokerOps {
    @{
        Icacls   = { param([string[]] $list) $r = & icacls.exe @list 2>&1; if ($LASTEXITCODE -ne 0) { throw "icacls $($list -join ' ') failed: $(@($r)[-1])" } }
        # 6 = TASK_CREATE_OR_UPDATE, 5 = TASK_LOGON_SERVICE_ACCOUNT
        Register = { param($name, $xml, $sddl) $svc = New-Object -ComObject 'Schedule.Service'; $svc.Connect(); $null = $svc.GetFolder('\').RegisterTask($name, $xml, 6, 'SYSTEM', $null, 5, $sddl) }
        Query    = { param($name) $r = (& schtasks.exe /query /tn $name /xml 2>&1) -join "`n"; if ($LASTEXITCODE -eq 0) { $r } else { $null } }
        Delete   = { param($name) $r = & schtasks.exe /delete /tn $name /f 2>&1; if ($LASTEXITCODE -ne 0) { throw "$(@($r)[-1])" } }
        # @{ owner; protected; aces = @{ sid; rights; allow; inheritOnly } }: SIDs, rights as an unsigned 32-bit mask.
        ReadAcl  = {
            param([string] $path)
            $a = Get-Acl -LiteralPath $path
            @{
                owner     = $a.GetOwner([Security.Principal.SecurityIdentifier]).Value
                protected = $a.AreAccessRulesProtected
                aces      = @($a.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | ForEach-Object {
                        @{ sid = $_.IdentityReference.Value; rights = ([long][int]$_.FileSystemRights) -band 0xFFFFFFFFL
                            allow = $_.AccessControlType -eq 'Allow'; inheritOnly = [bool]($_.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) } })
            }
        }
        # Recursive; a junction or link inside is unlinked, never followed.
        RemoveTree = { param([string] $path) [IO.Directory]::Delete($path, $true) }
    }
}

# The old GPU cap tasks (power-mode.ps1 gpu-task install): "<prefix>GpuCap" and "<prefix>GpuRestore". The broker's GPU
# limit replaces them; they go only once the broker is registered, so a GPU cap path is always left.
$script:WatcherDefaultTaskPrefix = 'BluettiWatcherEco'

# The prefix the service uses: config.json powerMode.taskPrefix, keys read case-sensitively as the service reads them,
# the default when not set. $null when the config cannot be read or the prefix is no plain name: nothing is removed then.
function Get-WatcherPowerModeTaskPrefix([string] $ConfigPath) {
    if (-not (Test-Path -LiteralPath $ConfigPath)) { return $script:WatcherDefaultTaskPrefix }
    try { $cfg = Get-Content -Raw -LiteralPath $ConfigPath | ConvertFrom-Json -ErrorAction Stop } catch { return $null }
    $prefix = $script:WatcherDefaultTaskPrefix
    if ($cfg -and @($cfg.PSObject.Properties.Name) -ccontains 'powerMode' -and $cfg.powerMode -and
        @($cfg.powerMode.PSObject.Properties.Name) -ccontains 'taskPrefix' -and $cfg.powerMode.taskPrefix) { $prefix = "$($cfg.powerMode.taskPrefix)" }
    if ($prefix -cnotmatch '^[A-Za-z0-9_][A-Za-z0-9_.-]{0,99}$') { return $null }
    $prefix
}

function Get-WatcherOldGpuTaskNames([string] $Prefix) { @("${Prefix}GpuCap", "${Prefix}GpuRestore") }

# The dry run's lines: one per old GPU task that is registered now.
function Get-WatcherOldGpuTaskSteps([string] $Prefix, [hashtable] $Ops = $null) {
    if (-not $Ops) { $Ops = Get-WatcherBrokerOps }
    if (-not $Prefix) { return @("keep the old GPU tasks: config.json powerMode.taskPrefix cannot be read (their names are not known)") }
    foreach ($name in Get-WatcherOldGpuTaskNames $Prefix) {
        if ($null -ne (& $Ops.Query $name)) { "remove the old GPU task '$name' once '$script:WatcherBrokerTask' is registered (the broker's GPU limit replaces it)" }
    }
}

# Removes each old GPU task that is registered. @{ ok; lines }, never throws.
function Remove-WatcherOldGpuTasks([string] $Prefix, [hashtable] $Ops) {
    if (-not $Prefix) { return @{ ok = $true; lines = @('the old GPU tasks were kept: config.json powerMode.taskPrefix cannot be read (their names are not known)') } }
    $ok = $true
    $lines = foreach ($name in Get-WatcherOldGpuTaskNames $Prefix) {
        if ($null -eq (& $Ops.Query $name)) { continue }
        try { $null = & $Ops.Delete $name; "removed the old GPU task '$name' (the broker's GPU limit replaces it)" }
        catch { $ok = $false; "could not remove the old GPU task '$name': $($_.Exception.Message)" }
    }
    @{ ok = $ok; lines = @($lines) }
}

# The broker installed, then, only when it is registered, the old GPU tasks removed. @{ ok; text }, never throws.
function Complete-WatcherBroker([string] $Root, [string] $Exchange, [string] $SourceExe, [string] $Sid, [string] $Prefix, [hashtable] $Ops = $null, [string] $OldRoot = '') {
    if (-not $Ops) { $Ops = Get-WatcherBrokerOps }
    $done = Install-WatcherBroker -Root $Root -Exchange $Exchange -SourceExe $SourceExe -Sid $Sid -Ops $Ops -OldRoot $OldRoot
    if (-not $done.ok) { return @{ ok = $false; text = "$($done.text); the old GPU tasks were kept" } }
    $gone = Remove-WatcherOldGpuTasks -Prefix $Prefix -Ops $Ops
    @{ ok = $gone.ok; text = (@($done.text) + $gone.lines) -join '; ' }
}

# The normal install's broker step. Elevated: Complete-WatcherBroker. Not elevated: nothing changes and the one line
# names the elevated step; ok, since the service needs no elevation and its install decides the exit.
function Invoke-WatcherBrokerStep([bool] $Elevated, [string] $Root, [string] $Exchange, [string] $SourceExe, [string] $Sid, [string] $Prefix, [hashtable] $Ops = $null, [string] $OldRoot = '') {
    if (-not $Ops) { $Ops = Get-WatcherBrokerOps }
    if ($Elevated -and -not $Sid) { return @{ ok = $false; text = 'the admin broker was not installed: the service task''s user cannot be read (install.ps1 -Apply registers it first)' } }
    if ($Elevated) { return Complete-WatcherBroker -Root $Root -Exchange $Exchange -SourceExe $SourceExe -Sid $Sid -Prefix $Prefix -Ops $Ops -OldRoot $OldRoot }
    $text = if ($null -ne (& $Ops.Query $script:WatcherBrokerTask)) {
        "the admin broker's copy of the exe is stale (the previous version) until this step runs: install.ps1 -Broker -Apply in an elevated shell"
    }
    else { 'the admin broker (power modes) is a SYSTEM task and was not installed: run install.ps1 -Broker -Apply in an elevated shell' }
    @{ ok = $true; text = $text }
}

# Rights on an ancestor that would let the user swap what SYSTEM runs: delete or rename it (DELETE, FILE_DELETE_CHILD
# for its children), change its ACL or owner (WRITE_DAC, WRITE_OWNER), add into it (FILE_ADD_FILE, FILE_ADD_SUBDIRECTORY),
# and the generic rights that carry them. The groups every user is in are held to the rename and re-permission rights only
# (a stock C:\ lets Authenticated Users add folders).
$script:WatcherUserAncestorMask = [long](0x2 -bor 0x4 -bor 0x40 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x10000000 -bor 0x40000000)
$script:WatcherBroadSids = @('S-1-1-0', 'S-1-5-11', 'S-1-5-32-545', 'S-1-5-4')
$script:WatcherBroadAncestorMask = [long](0x40 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x10000000)
# What the user may hold on the broker's own folders and files: read, traverse, SYNCHRONIZE, generic read and execute.
$script:WatcherReadMask = [long]0x1200A9 -bor 0x80000000L -bor 0x20000000L
# Administrators, SYSTEM, TrustedInstaller: the owners and writers the broker's folders may have.
$script:WatcherAdminSids = @('S-1-5-32-544', 'S-1-5-18', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')

# The existing ancestors of a path, nearest first, up to the drive root.
function Get-WatcherPathAncestors([string] $Path) {
    $p = Split-Path -Parent $Path
    while ($p) {
        if (Test-Path -LiteralPath $p) { $p }
        $next = Split-Path -Parent $p
        if ($next -eq $p) { break }
        $p = $next
    }
}

# One ACL through the seam, or the reason it cannot be read (an ACL that cannot be read is never a pass).
function Read-WatcherAcl([string] $Path, [hashtable] $Ops) {
    try { $acl = & $Ops.ReadAcl $Path } catch { return @{ error = "the ACL of $Path cannot be read ($($_.Exception.Message))" } }
    if (-not $acl) { return @{ error = "the ACL of $Path cannot be read" } }
    $acl
}

# "" = no existing ancestor of the paths lets the user rename, re-own, re-permission or add into it; else the reason.
function Test-WatcherBrokerAncestors([string[]] $Paths, [string] $Sid, [hashtable] $Ops) {
    foreach ($a in @($Paths | ForEach-Object { Get-WatcherPathAncestors $_ } | Select-Object -Unique)) {
        $acl = Read-WatcherAcl $a $Ops
        if ($acl.error) { return $acl.error }
        if ($acl.owner -ceq $Sid) { return "$a is owned by the user $Sid" }
        foreach ($ace in @($acl.aces)) {
            if (-not $ace.allow -or $ace.inheritOnly) { continue }
            $mask = if ($ace.sid -ceq $Sid) { $script:WatcherUserAncestorMask } elseif ($script:WatcherBroadSids -ccontains $ace.sid) { $script:WatcherBroadAncestorMask } else { 0L }
            $bad = ([long]$ace.rights) -band $mask
            if ($bad -ne 0) { return ("$a grants $($ace.sid) the rights 0x{0:X} (rename, re-permission, re-own or add into it)" -f $bad) }
        }
    }
    ''
}

# "" = every broker folder and the files SYSTEM trusts read back owned by Administrators (or SYSTEM), the roots cut from
# inheritance, the user holding read only (and adding files in requests\), no other writer; else the reason.
function Test-WatcherBrokerReadBack([string] $Root, [string] $Exchange, [string] $Sid, [hashtable] $Ops) {
    $checks = @(
        @{ path = $Root; root = $true; extra = 0L }, @{ path = (Join-Path $Root 'bin'); extra = 0L }
        @{ path = (Join-Path $Root 'bin\BluettiWatcher.exe'); extra = 0L }, @{ path = (Join-Path $Root 'policy.json'); extra = 0L }
        @{ path = $Exchange; root = $true; extra = 0L }, @{ path = (Join-Path $Exchange 'requests'); extra = 0x2L }
        @{ path = (Join-Path $Exchange 'responses'); extra = 0L }
    )
    foreach ($c in $checks) {
        $acl = Read-WatcherAcl $c.path $Ops
        if ($acl.error) { return "read back: $($acl.error)" }
        if ($script:WatcherAdminSids -notcontains $acl.owner) { return "read back: $($c.path) is owned by $($acl.owner), not Administrators" }
        if ($c.root -and -not $acl.protected) { return "read back: $($c.path) still inherits its ACL" }
        foreach ($ace in @($acl.aces)) {
            if (-not $ace.allow -or $ace.inheritOnly -or $script:WatcherAdminSids -contains $ace.sid) { continue }
            $allowed = if ($ace.sid -ceq $Sid) { $script:WatcherReadMask -bor $c.extra } else { $script:WatcherReadMask }
            $beyond = ([long]$ace.rights) -band (-bnot $allowed)
            if ($beyond -ne 0) { return ("read back: $($c.path) gives $($ace.sid) the rights 0x{0:X} beyond read" -f $beyond) }
        }
    }
    ''
}

# The pre-release broker lived inside the flag folder, which the user may change: its policy and profiles come
# along only when Administrators or SYSTEM own them (never a file the user could have written), then the old folder goes
# (a junction inside it is unlinked, never followed). Lines of what was done.
function Move-WatcherOldBroker([string] $OldRoot, [string] $Root, [hashtable] $Ops) {
    if (-not $OldRoot -or -not (Test-Path -LiteralPath $OldRoot)) { return @() }
    $adminOwned = { param([string] $f) $a = Read-WatcherAcl $f $Ops; -not $a.error -and $script:WatcherAdminSids -contains $a.owner }
    $lines = @()
    $old = Join-Path $OldRoot 'policy.json'
    $new = Join-Path $Root 'policy.json'
    if ((Test-Path -LiteralPath $old -PathType Leaf) -and -not (Test-Path -LiteralPath $new)) {
        if (& $adminOwned $old) { Copy-Item -LiteralPath $old -Destination $new; $lines += "carried the policy from $OldRoot" }
        else { $lines += "the policy in $OldRoot was not carried (not owned by Administrators): a new one allows nothing" }
    }
    $profiles = Join-Path $OldRoot 'profiles'
    if (Test-Path -LiteralPath $profiles -PathType Container) {
        $n = 0
        foreach ($f in @(Get-ChildItem -LiteralPath $profiles -File -Filter '*.nip' -Attributes '!ReparsePoint')) {
            $to = Join-Path $Root "profiles\$($f.Name)"
            if (-not (Test-Path -LiteralPath $to) -and (& $adminOwned $f.FullName)) { Copy-Item -LiteralPath $f.FullName -Destination $to; $n++ }
        }
        if ($n) { $lines += "carried $n profile(s) from $profiles" }
    }
    & $Ops.RemoveTree $OldRoot
    $lines + "removed the old broker folder $OldRoot"
}

# One root: owned by Administrators, inheritance cut at the root with Administrators and SYSTEM full control and the user
# read, then every child (one a user pre-created with its own entries) reset to inherit only that. The root's ACL is set
# first, so no moment opens it to the parent's (a stock ProgramData lets Users add folders).
function Set-WatcherBrokerRootAcl([string] $Path, [string] $Sid, [hashtable] $Ops) {
    & $Ops.Icacls @($Path, '/setowner', '*S-1-5-32-544', '/T', '/C', '/L', '/Q')
    & $Ops.Icacls @($Path, '/inheritance:r', '/grant:r', '*S-1-5-32-544:(OI)(CI)F', '*S-1-5-18:(OI)(CI)F', "*${Sid}:(OI)(CI)RX", '/Q')
    foreach ($child in @(Get-ChildItem -LiteralPath $Path -Force)) { & $Ops.Icacls @($child.FullName, '/reset', '/T', '/C', '/L', '/Q') }
}

# The ancestors checked, the folders and their ACLs, the old broker carried out, the exe copy, the policy, the owner, the
# ACLs read back, the task. @{ ok; text }, never throws; any check that fails or cannot run refuses.
function Install-WatcherBroker([string] $Root, [string] $Exchange, [string] $SourceExe, [string] $Sid, [hashtable] $Ops = $null, [string] $OldRoot = '') {
    if (-not $Ops) { $Ops = Get-WatcherBrokerOps }
    try {
        if (-not $Exchange) { return @{ ok = $false; text = 'the broker was not installed: no exchange folder' } }
        $why = Test-WatcherBrokerAncestors @($Root, $Exchange) $Sid $Ops
        if ($why) { return @{ ok = $false; text = "the broker was not installed, refused: $why" } }
        foreach ($d in @('', 'profiles', 'exports', 'bin')) { $null = New-Item -ItemType Directory -Force -Path (Join-Path $Root $d) }
        foreach ($d in @('', 'requests', 'responses')) { $null = New-Item -ItemType Directory -Force -Path (Join-Path $Exchange $d) }
        Set-WatcherBrokerRootAcl $Root $Sid $Ops
        Set-WatcherBrokerRootAcl $Exchange $Sid $Ops
        & $Ops.Icacls @((Join-Path $Exchange 'requests'), '/grant', "*${Sid}:(WD)", "*${Sid}:(OI)(IO)(RX,W,D)", '/Q')
        $moved = @(Move-WatcherOldBroker $OldRoot $Root $Ops)
        $exe = Join-Path $Root 'bin\BluettiWatcher.exe'
        Copy-Item -LiteralPath $SourceExe -Destination "$exe.new" -Force
        Move-Item -LiteralPath "$exe.new" -Destination $exe -Force
        $policy = Join-Path $Root 'policy.json'
        if (-not (Test-Path -LiteralPath $policy)) { [IO.File]::WriteAllText($policy, (New-WatcherBrokerPolicyText $Sid) + "`n", [Text.UTF8Encoding]::new($false)) }
        # A file an elevated admin creates may be owned by its creator: Administrators own everything here.
        & $Ops.Icacls @($Root, '/setowner', '*S-1-5-32-544', '/T', '/C', '/L', '/Q')
        & $Ops.Icacls @($Exchange, '/setowner', '*S-1-5-32-544', '/T', '/C', '/L', '/Q')
        $why = Test-WatcherBrokerReadBack $Root $Exchange $Sid $Ops
        if ($why) { return @{ ok = $false; text = "the broker was not installed: $why" } }
        $null = & $Ops.Register $script:WatcherBrokerTask (New-WatcherBrokerTaskXml $exe) (Get-WatcherBrokerTaskSddl $Sid)
        $back = "$(& $Ops.Query $script:WatcherBrokerTask)"
        if ($back -notmatch '<UserId>(S-1-5-18|SYSTEM|NT AUTHORITY\\SYSTEM)</UserId>' -or -not $back.Contains([Security.SecurityElement]::Escape($exe)) -or $back -notmatch '<Arguments>--broker</Arguments>') {
            return @{ ok = $false; text = "$script:WatcherBrokerTask was not registered as written (read back: $back)" }
        }
        return @{ ok = $true; text = (@($moved) + "registered ${script:WatcherBrokerTask}: SYSTEM, on demand, $exe --broker; policy $policy; requests $Exchange\requests") -join '; ' }
    }
    catch { return @{ ok = $false; text = "the broker was not installed: $($_.Exception.Message)" } }
}
