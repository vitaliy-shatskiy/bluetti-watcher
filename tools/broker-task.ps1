<#
broker-task.ps1 - the admin broker (docs/design.md, power modes): a SYSTEM scheduled task "BluettiWatcherBroker" with no
trigger, started on demand by the unelevated service, that runs BluettiWatcher.exe --broker once and exits. Functions
only: install.ps1 dot-sources it (install.ps1 -Apply or -Broker -Apply, elevated). Dot-sourcing changes nothing. Once the
broker is registered, the old GPU cap tasks "<prefix>GpuCap" and "<prefix>GpuRestore" (power-mode.ps1 gpu-task install)
are removed: the broker's GPU limit replaces them. A failed registration keeps them.

What runs as SYSTEM takes nothing the user can write: the exe is a copy in %ProgramData%\BluettiWatcher\broker\bin, a
folder owned by Administrators where the user may only read (the user's own bin under %LOCALAPPDATA% is never run
elevated). The folder's access list: Administrators and SYSTEM full control, the user read; in requests\ the user may
also add files (never folders). Every file and folder there is owned by Administrators, so no owner's implicit right to
change the access list is left to the user. The task's own security: Administrators and SYSTEM full control, the user
read and run (the same SDDL the GPU cap tasks use).
#>

$script:WatcherBrokerTask = 'BluettiWatcherBroker'

function Get-WatcherBrokerRoot { Join-Path $env:ProgramData 'BluettiWatcher\broker' }

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
function Get-WatcherBrokerSteps([string] $Root, [string] $SourceExe, [string] $User) {
    @(
        "create $Root with requests\, responses\, profiles\, exports\, bin\: Administrators and SYSTEM full control, $User read; requests\ also lets $User add files (never folders); owner Administrators throughout"
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
function Complete-WatcherBroker([string] $Root, [string] $SourceExe, [string] $Sid, [string] $Prefix, [hashtable] $Ops = $null) {
    if (-not $Ops) { $Ops = Get-WatcherBrokerOps }
    $done = Install-WatcherBroker -Root $Root -SourceExe $SourceExe -Sid $Sid -Ops $Ops
    if (-not $done.ok) { return @{ ok = $false; text = "$($done.text); the old GPU tasks were kept" } }
    $gone = Remove-WatcherOldGpuTasks -Prefix $Prefix -Ops $Ops
    @{ ok = $gone.ok; text = (@($done.text) + $gone.lines) -join '; ' }
}

# The normal install's broker step. Elevated: Complete-WatcherBroker. Not elevated: nothing changes and the one line
# names the elevated step; ok, since the service needs no elevation and its install decides the exit.
function Invoke-WatcherBrokerStep([bool] $Elevated, [string] $Root, [string] $SourceExe, [string] $Sid, [string] $Prefix, [hashtable] $Ops = $null) {
    if (-not $Ops) { $Ops = Get-WatcherBrokerOps }
    if ($Elevated) { return Complete-WatcherBroker -Root $Root -SourceExe $SourceExe -Sid $Sid -Prefix $Prefix -Ops $Ops }
    $text = if ($null -ne (& $Ops.Query $script:WatcherBrokerTask)) {
        "the admin broker's copy of the exe is stale (the previous version) until this step runs: install.ps1 -Broker -Apply in an elevated shell"
    }
    else { 'the admin broker (power modes) is a SYSTEM task and was not installed: run install.ps1 -Broker -Apply in an elevated shell' }
    @{ ok = $true; text = $text }
}

# Folders, ACLs, the exe copy, the policy, the owner, the task. @{ ok; text }, never throws.
function Install-WatcherBroker([string] $Root, [string] $SourceExe, [string] $Sid, [hashtable] $Ops = $null) {
    if (-not $Ops) { $Ops = Get-WatcherBrokerOps }
    try {
        foreach ($d in @('', 'requests', 'responses', 'profiles', 'exports', 'bin')) { $null = New-Item -ItemType Directory -Force -Path (Join-Path $Root $d) }
        & $Ops.Icacls @($Root, '/setowner', '*S-1-5-32-544', '/T', '/C', '/L', '/Q')
        & $Ops.Icacls @($Root, '/inheritance:r', '/grant:r', '*S-1-5-32-544:(OI)(CI)F', '*S-1-5-18:(OI)(CI)F', "*${Sid}:(OI)(CI)RX", '/Q')
        & $Ops.Icacls @((Join-Path $Root 'requests'), '/grant', "*${Sid}:(WD)", "*${Sid}:(OI)(IO)(RX,W,D)", '/Q')
        $exe = Join-Path $Root 'bin\BluettiWatcher.exe'
        Copy-Item -LiteralPath $SourceExe -Destination "$exe.new" -Force
        Move-Item -LiteralPath "$exe.new" -Destination $exe -Force
        $policy = Join-Path $Root 'policy.json'
        if (-not (Test-Path -LiteralPath $policy)) { [IO.File]::WriteAllText($policy, (New-WatcherBrokerPolicyText $Sid) + "`n", [Text.UTF8Encoding]::new($false)) }
        # A file an elevated admin creates may be owned by its creator: Administrators own everything here.
        & $Ops.Icacls @($Root, '/setowner', '*S-1-5-32-544', '/T', '/C', '/L', '/Q')
        $null = & $Ops.Register $script:WatcherBrokerTask (New-WatcherBrokerTaskXml $exe) (Get-WatcherBrokerTaskSddl $Sid)
        $back = "$(& $Ops.Query $script:WatcherBrokerTask)"
        if ($back -notmatch '<UserId>(S-1-5-18|SYSTEM|NT AUTHORITY\\SYSTEM)</UserId>' -or -not $back.Contains([Security.SecurityElement]::Escape($exe)) -or $back -notmatch '<Arguments>--broker</Arguments>') {
            return @{ ok = $false; text = "$script:WatcherBrokerTask was not registered as written (read back: $back)" }
        }
        return @{ ok = $true; text = "registered ${script:WatcherBrokerTask}: SYSTEM, on demand, $exe --broker; policy $policy" }
    }
    catch { return @{ ok = $false; text = "the broker was not installed: $($_.Exception.Message)" } }
}
