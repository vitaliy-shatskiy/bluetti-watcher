<#
install.ps1 - install Bluetti Watcher for the current user. DRY RUN by default: prints every step and changes nothing.

  pwsh -NoProfile -File install.ps1 [-Mac AA:BB:CC:DD:EE:FF -CapacityWh 2048]          dry run: what would happen, and what blocks it
  pwsh -NoProfile -File install.ps1 [-Mac AA:BB:CC:DD:EE:FF -CapacityWh 2048] -Apply   do it
  pwsh -NoProfile -File install.ps1 -RemoveWake [-Apply]              only the wake task: removed

Runs from a release zip (bin\BluettiWatcher.exe beside this script: the exe is copied) or from a source checkout
(src\ beside this script: the exe is published, which needs the .NET SDK and, for NativeAOT, the MSVC linker).
Data directory: %LOCALAPPDATA%\bluetti-watcher.

Both modes first print what an interrupted run left and the state of the task (missing, disabled, ready, running);
-Apply puts it right before anything else: a task the installer left disabled (marker installer-disabled-task)
enabled and started, a bin that never passed the health check replaced by bin.old.

-Apply:
  1. config.json: written from a template only when there is none (with -Mac as the station's address and -CapacityWh
     as its battery capacity in Wh, both required then); an existing one is never changed. No station MAC in it blocks
     the install.
  2. the exe into the staging directory <data dir>\bin.new, never into the live bin: copied from the release, or
     published (tools/publish.ps1; -Fallback = trimmed self-contained single file instead of NativeAOT). The service
     keeps running; a failure here changes nothing
  3. the swap (tools/swap.ps1), the only downtime, seconds: the task disabled and stopped, a bounded wait until no
     process runs from bin and its files are unlocked, bin -> bin.old, bin.new -> bin; then the logon task
     "BluettiWatcher": as the current user, Interactive (the user's session, never session 0: the power-mode mutex is
     Local\), Limited, hidden, no time limit, restarted every minute on failure, one instance (IgnoreNew); a second
     trigger every 5 minutes brings it back after any exit; started at once, then /api/health must answer and
     bin.old is removed. A failed swap or no health answer: bin.old is bin again, the task enabled and started, exit 1.
  4. only then (the new exe answered): power-mode\*.ps1 copied to <data dir>\power-mode\ (each file replaced whole;
     they forward to BluettiWatcher.exe power-mode, which an older exe has not got)
  5. the 1.0.0 Python venv <data dir>\.venv, if there is one, deleted (the built-in reader does not use it); a junction
     or symlink there is unlinked, its target never followed; a failure is named, not fatal
  6. the wake task (config wakeTaskName, default "BluettiWatcherWake"; tools/wake-task.ps1): disabled, WakeToRun, one
     time trigger, a no-op action (System32 conhost.exe --headless cmd.exe /d /c exit 0) as the current user,
     interactive token, least privilege; security: Administrators, SYSTEM and the user full control (the unelevated
     service sets its time and enables it before a hibernation in a cut, wakeEveryMinutes > 0, and disables it on
     mains). Registered from XML through COM and read back; one that differs is deleted again
  7. shortcuts "Bluetti Watcher" on the Desktop and in the Start menu: Chrome (else Edge) --app=http://127.0.0.1:<port>/

Exit: 0 done (or the dry run found nothing in the way) · 1 a step failed, or the dry run found something in the way ·
2 could not run.
#>
[CmdletBinding()]
param(
    [switch] $Apply,
    [switch] $Fallback,
    [switch] $RemoveWake,
    [string] $Mac = '',
    [int] $CapacityWh = 0,
    [int] $Port = 18940
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { Write-Output 'install: COULD NOT RUN: needs PowerShell 7 (pwsh)'; exit 2 }
$repo = $PSScriptRoot
. (Join-Path $repo 'tools\swap.ps1')
. (Join-Path $repo 'tools\wake-task.ps1')
$root = Join-Path $env:LOCALAPPDATA 'bluetti-watcher'
$bin = Join-Path $root 'bin'
$exe = Join-Path $bin 'BluettiWatcher.exe'
# Same volume as bin: the swap is two renames.
$staging = Join-Path $root 'bin.new'
$previous = Join-Path $root 'bin.old'
# There while the installer holds the task disabled: a run after a hard kill enables the task again.
$marker = Join-Path $root 'installer-disabled-task'
$task = 'BluettiWatcher'
$config = Join-Path $root 'config.json'
$powerModeFrom = Join-Path $repo 'power-mode'
$powerModeTo = Join-Path $root 'power-mode'
# The Python venv of 1.0.0; nothing uses it since 1.1.0.
$oldVenv = Join-Path $root '.venv'
# Deletes the old venv: a plain folder goes whole; a junction or symlink (at the venv, or inside it) is unlinked alone, never
# followed, so what it points at stays. A plain file is not a venv: refused. Throws on failure.
function Remove-WatcherOldVenv([string] $Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    $isLink = { param($i) $i.LinkType -or ($i.Attributes -band [IO.FileAttributes]::ReparsePoint) }
    if (& $isLink $item) {
        if ($item.PSIsContainer) { [IO.Directory]::Delete($item.FullName) } else { [IO.File]::Delete($item.FullName) }
        return "removed the link $Path (what it points at was left as it is)"
    }
    if (-not $item.PSIsContainer) { throw "$Path is a file, not a venv" }
    foreach ($inner in @(Get-ChildItem -LiteralPath $Path -Recurse -Force -Attributes ReparsePoint -ErrorAction Stop)) {
        if ($inner.PSIsContainer) { [IO.Directory]::Delete($inner.FullName) } else { [IO.File]::Delete($inner.FullName) }
    }
    Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
    "removed the 1.0.0 Python venv $Path"
}
# A release zip carries the exe; a checkout carries the source.
$releaseBin = Join-Path $repo 'bin'
$fromRelease = (Test-Path -LiteralPath (Join-Path $releaseBin 'BluettiWatcher.exe')) -and -not (Test-Path -LiteralPath (Join-Path $repo 'src'))
$url = "http://127.0.0.1:$Port/"
$browser = @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe", "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
    "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe", "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe") |
    Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$places = @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))
$shortcuts = @($places | ForEach-Object { Join-Path $_ 'Bluetti Watcher.lnk' })
$switch = Join-Path $root 'no-hibernate'
$macPattern = '^[0-9A-Fa-f]{2}(:[0-9A-Fa-f]{2}){5}$'

function Step([string] $text) { Write-Output "$(if ($Apply) { 'do ' } else { 'would' })  $text" }

# The fresh config.json: the station's address and battery capacity; every other key keeps its default (docs/configuration.md).
function New-WatcherConfigText([string] $Address, [int] $Capacity) {
    $lines = @('{', '  // Every key and its default: docs/configuration.md. The service reads this file at start.')
    $lines += if ($Address) { "  `"mac`": `"$Address`"," } else { '  // "mac": "AA:BB:CC:DD:EE:FF",' }
    $lines += "  `"capacityWh`": $Capacity"
    ($lines + '}') -join "`n"
}

# ---- the config: the existing one as it is, else the template (with -Mac)
$problems = @()
$configNote = ''
$station = ''
$wakeName = $null
$wakeProblem = $null
if (Test-Path -LiteralPath $config) {
    try {
        $cfg = Get-Content -Raw -LiteralPath $config | ConvertFrom-Json
        # The service reads the keys case-sensitively: "MAC" is no "mac", and a wakeTaskName it does not read must not
        # name the task the installer registers.
        $keys = @($cfg.PSObject.Properties.Name)
        $wrongCase = { param($key) @($keys | Where-Object { $_ -ieq $key -and $_ -cne $key }) }
        $station = if ($keys -ccontains 'mac') { "$($cfg.mac)".Trim() } else { '' }
        if ($keys -ccontains 'wakeTaskName') { if ($cfg.wakeTaskName) { $wakeName = "$($cfg.wakeTaskName)" } }
        elseif (@(& $wrongCase 'wakeTaskName').Count) { $wakeProblem = "the key $(@(& $wrongCase 'wakeTaskName')[0]) in $config must be written exactly `"wakeTaskName`" (the service reads keys case-sensitively and would ignore it)" }
        if (-not $station -and @(& $wrongCase 'mac').Count) { $problems += "no station MAC: the key $(@(& $wrongCase 'mac')[0]) in $config must be written exactly `"mac`" (the service reads keys case-sensitively and would ignore it)" }
        elseif (-not $station) { $problems += "no station MAC: add `"mac`": `"AA:BB:CC:DD:EE:FF`" to $config" }
        elseif ($station -notmatch $macPattern) { $problems += "the mac in $config is not a Bluetooth address: $station" }
        $newOnly = @($(if ($Mac) { '-Mac' }), $(if ($PSBoundParameters.ContainsKey('CapacityWh')) { '-CapacityWh' }) | Where-Object { $_ })
        $configNote = "keep $config as it is$(if ($newOnly) { " ($($newOnly -join ' and ') $(if ($newOnly.Count -gt 1) { 'are' } else { 'is' }) used only for a new config.json)" })"
        if ($keys -cnotcontains 'capacityWh') { $configNote += "; it has no capacityWh, so the battery time assumes 2304 Wh (add `"capacityWh`" for another station)" }
    }
    catch { $problems += "$config cannot be read as JSON: $($_.Exception.Message)" }
}
else {
    $station = $Mac.Trim()
    if (-not $station) { $problems += 'no station MAC: pass -Mac AA:BB:CC:DD:EE:FF (the station''s Bluetooth address)' }
    elseif ($station -notmatch $macPattern) { $problems += "-Mac is not a Bluetooth address: $station" }
    if (-not $PSBoundParameters.ContainsKey('CapacityWh')) { $problems += 'no battery capacity: pass -CapacityWh <Wh> (the station''s battery capacity in Wh, from its spec sheet)' }
    elseif ($CapacityWh -le 0) { $problems += "-CapacityWh must be a positive number of Wh: $CapacityWh" }
    $configNote = "write $config from the template$(if ($station) { " with mac $station" })$(if ($CapacityWh -gt 0) { " and capacityWh $CapacityWh" })"
}
if ($wakeName) {
    $nameVerdict = Test-WatcherWakeTaskName $wakeName $task
    if ($nameVerdict -ceq 'ok') { $script:WatcherWakeTask = $wakeName } else { $wakeProblem = "wakeTaskName in ${config}: $nameVerdict" }
}
if ($wakeProblem) { $problems += $wakeProblem }

# ---- the wake task alone: removed (the way back from step 6)
if ($RemoveWake) {
    if ($wakeProblem) { Write-Output "blocked: $wakeProblem"; exit 1 }
    Step "remove the wake task '$script:WatcherWakeTask' (Bluetti Watcher then wakes nothing; set wakeEveryMinutes to 0 too)"
    if (-not $Apply) { Write-Output 'dry run: nothing changed.'; exit 0 }
    $gone = Remove-WatcherWakeTask
    Write-Output "install: $($gone.text)"
    exit $(if ($gone.ok) { 0 } else { 1 })
}

$ops = Get-WatcherSwapOps
$log = { param($text) Write-Host "install: $text" }

# ---- what an interrupted run left goes first (no later failure may leave the task disabled or pointing at nothing),
# then the state of the task (both modes)
if ($Apply) {
    try { $null = Repair-WatcherInstall -Bin $bin -Previous $previous -TaskName $task -Marker $marker -Ops $ops -Log $log }
    catch { Write-Output "install: what an interrupted run left could not be repaired: $($_.Exception.Message)"; exit 1 }
}
else { Get-WatcherPending -Bin $bin -Previous $previous -Marker $marker -TaskName $task | ForEach-Object { Write-Output "pending: $_" } }
Write-Output "task '$task': $(Get-WatcherTaskState -TaskName $task -Ops $ops)"

# ---- preconditions (both modes)
if (-not $fromRelease) {
    if (-not (Test-Path -LiteralPath (Join-Path $repo 'src\BluettiWatcher\BluettiWatcher.csproj'))) { $problems += "neither $releaseBin\BluettiWatcher.exe (a release) nor src\ (a checkout) beside install.ps1" }
    elseif (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { $problems += 'no dotnet on PATH (a source install publishes the exe; a release zip needs none)' }
}
if (-not (Test-Path -LiteralPath (Join-Path $powerModeFrom 'power-mode.ps1'))) { $problems += "no $powerModeFrom\power-mode.ps1 (the power-mode switch)" }
if (-not $browser) { $problems += 'neither Chrome nor Edge found (the shortcut needs one)' }
$holder = Get-NetTCPConnection -LocalAddress 127.0.0.1 -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
if ($holder) {
    $owner = (Get-Process -Id $holder.OwningProcess -ErrorAction SilentlyContinue).Path
    if ($owner -ne $exe) { $problems += "127.0.0.1:$Port is taken by $owner (pass -Port)" }
}
if ($problems) { $problems | ForEach-Object { Write-Output "blocked: $_" }; if ($Apply) { exit 2 } }

Step "config: $configNote"

if ($fromRelease) { Step "copy the release's $releaseBin to the staging directory $staging (one left by a failed run is removed first)" }
else { Step "publish $(if ($Fallback) { 'trimmed self-contained single file' } else { 'NativeAOT' }) to the staging directory $staging (one left by a failed run is removed first); Bluetti Watcher keeps running through the build, a failed publish changes nothing" }
Step "swap, the only downtime (seconds): disable and stop the task '$task', wait up to 30 s until no process runs from $bin and its files are unlocked, move $bin to $previous and $staging to $bin"
Step "register the logon task '$task': $exe --port $Port as $env:USERDOMAIN\$env:USERNAME, Interactive, Limited, hidden, restart on failure, also every 5 minutes (one instance); start it and wait for ${url}api/health"
Step "after the health check passed: remove $previous. A failed swap or no health answer: $previous is $bin again, the task '$task' enabled and started, exit 1"
Step "copy the power-mode switch $powerModeFrom\*.ps1 to $powerModeTo (after the health check: the scripts forward to BluettiWatcher.exe power-mode)"
if (Test-Path -LiteralPath $oldVenv) { Step "remove the 1.0.0 Python venv $oldVenv (after the health check; the built-in reader does not use it)" }
Step "register the wake task '$script:WatcherWakeTask': disabled, WakeToRun, a time trigger the service sets, action $([Environment]::GetFolderPath('System'))\conhost.exe --headless cmd.exe /d /c exit 0 (a no-op) as $env:USERDOMAIN\$env:USERNAME, interactive token, least privilege; security: Administrators, SYSTEM, the user; read back after registering"
Step "create the shortcuts $($shortcuts -join ' and ') -> $browser --app=$url"
if (-not $Apply) { Write-Output 'dry run: nothing changed. Run with -Apply to install.'; exit $(if ($problems) { 1 } else { 0 }) }

# A failure from here on: what runs stays as it is.
function Stop-Install([string] $why) {
    Write-Output "install: $why"
    exit 1
}

try {
    # ---- 1. config, only when there is none
    $null = New-Item -ItemType Directory -Force -Path $root
    if (-not (Test-Path -LiteralPath $config)) {
        [IO.File]::WriteAllText($config, (New-WatcherConfigText $station $CapacityWh) + "`n", [Text.UTF8Encoding]::new($false))
        Write-Output "install: wrote $config"
    }

    # ---- 2. exe, into the staging directory: the service keeps running
    Clear-WatcherStaging -Staging $staging -Log $log
    if ($fromRelease) {
        Copy-Item -LiteralPath $releaseBin -Destination $staging -Recurse
    }
    else {
        $args2 = @('-NoProfile', '-File', (Join-Path $repo 'tools\publish.ps1'), '-Out', $staging)
        if ($Fallback) { $args2 += '-Fallback' }
        & pwsh @args2
        $code = $LASTEXITCODE
        if ($code -ne 0) { throw "the publish failed (exit $code); $bin was not touched; the task '$task' is $(Get-WatcherTaskState -TaskName $task -Ops $ops)" }
    }

    # ---- 3. swap, logon task, health
    # The logon trigger plus one every 5 minutes, one instance (tools/swap.ps1)
    $def = New-WatcherTaskDefinition -Exe $exe -Port $Port -WorkingDirectory $root -User "$env:USERDOMAIN\$env:USERNAME"
    $register = {
        Register-ScheduledTask -TaskName $task -Action $def.Action -Trigger $def.Trigger -Principal $def.Principal -Settings $def.Settings `
            -Description 'Bluetti Watcher: reads the BLUETTI power station once a minute, read only; dashboard on 127.0.0.1' -Force | Out-Null
    }
    # Any answer but 200 is "not yet": a refused connection, a timeout, an error status.
    $health = {
        $ok = Wait-WatcherHealth -TimeoutSec 30 -Probe {
            try { (Invoke-WebRequest -Uri "${url}api/health" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200 } catch { $false }
        }
        if (-not $ok) { Write-Host "install: ${url}api/health did not answer 200 within 30 s (see $root\service.log)" }
        $ok
    }
    Install-WatcherBinaries -Bin $bin -Staging $staging -Previous $previous -TaskName $task -Marker $marker -Ops $ops -Register $register -Health $health -Log $log
    Write-Output "install: ${url}api/health answers"

    # ---- 4. the power-mode scripts, once the new exe answers: they forward to BluettiWatcher.exe power-mode, which an
    # older exe has not got (a failed swap keeps the older scripts with the older exe); each file replaced whole
    $null = New-Item -ItemType Directory -Force -Path $powerModeTo
    foreach ($f in @(Get-ChildItem -LiteralPath $powerModeFrom -Filter '*.ps1' -File)) {
        $to = Join-Path $powerModeTo $f.Name
        Copy-Item -LiteralPath $f.FullName -Destination "$to.new" -Force
        Move-Item -LiteralPath "$to.new" -Destination $to -Force
    }
    Write-Output "install: power-mode scripts in $powerModeTo"

    # ---- 5. the 1.0.0 Python venv: the built-in reader does not use it; a failure is named, not fatal
    if (Test-Path -LiteralPath $oldVenv) {
        try {
            Write-Output "install: $(Remove-WatcherOldVenv $oldVenv)"
        }
        catch { Write-Output "install: could not remove the 1.0.0 Python venv $oldVenv ($($_.Exception.Message)); delete it by hand" }
    }
}
catch { Stop-Install $_.Exception.Message }
Write-Output "install: no-hibernate $(if (Test-Path -LiteralPath $switch) { "present ($switch): hibernation off" } else { 'absent: hibernation on' })"

# ---- 6. the wake task: a failure leaves the service running without a wake (exit 1 at the end)
$wakeTask = Register-WatcherWakeTask
Write-Output "install: $($wakeTask.text)"

# ---- 7. shortcuts
$shell = New-Object -ComObject WScript.Shell
foreach ($lnk in $shortcuts) {
    $s = $shell.CreateShortcut($lnk)
    $s.TargetPath = $browser
    $s.Arguments = "--app=$url"
    $s.Description = 'Bluetti Watcher dashboard'
    $s.Save()
}
Write-Output "install: done - $url"
exit $(if ($wakeTask.ok) { 0 } else { 1 })
