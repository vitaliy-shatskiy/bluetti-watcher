<#
test-install.ps1 - install.ps1's dry run against a temp data directory (LOCALAPPDATA pointed at a temp folder for the
child only): the station MAC and battery capacity it requires, the config it would write, a release zip told from a checkout. Never runs
-Apply; registers nothing, writes nothing outside its own temp directory.

  pwsh -NoProfile -File tools/test-install.ps1

Exit: 0 every case passed · 1 a case failed · 2 did not run.
#>
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$installer = Join-Path $repo 'install.ps1'
if (-not (Test-Path -LiteralPath $installer)) { Write-Output "test-install: COULD NOT RUN: no $installer"; exit 2 }
try {
    $base = Join-Path ([IO.Path]::GetTempPath()) ('bluetti-watcher-install-test-' + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $base
}
catch { Write-Output "test-install: COULD NOT RUN: $($_.Exception.Message)"; exit 2 }

$script:failed = 0
$script:passed = 0
$script:n = 0
function Check([string] $name, [bool] $ok, [string] $detail = '') {
    if ($ok) { $script:passed++ } else { $script:failed++; Write-Output "FAIL  $name $detail" }
}
# A free port: the dry run reports a taken one as a problem.
function Get-FreePort { $l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $l.Start(); $p = $l.LocalEndpoint.Port; $l.Stop(); $p }

# The dry run in a child pwsh whose LOCALAPPDATA is a fresh temp folder. Config: $null = none, else its text. Dirs: folders
# (relative to the data directory) that exist before the run.
function Invoke-DryRun([string] $Script = $installer, $Config = $null, [string[]] $Extra = @(), [hashtable] $Env = @{}, [string[]] $Dirs = @()) {
    $script:n++
    $local = Join-Path $base "local$script:n"
    $null = New-Item -ItemType Directory -Path $local
    if ($null -ne $Config) {
        $null = New-Item -ItemType Directory -Path (Join-Path $local 'bluetti-watcher')
        [IO.File]::WriteAllText((Join-Path $local 'bluetti-watcher\config.json'), $Config)
    }
    foreach ($d in $Dirs) { $null = New-Item -ItemType Directory -Force -Path (Join-Path $local "bluetti-watcher\$d") }
    # Only the data directory: pwsh itself may write its caches under LOCALAPPDATA.
    $data = Join-Path $local 'bluetti-watcher'
    $list = { if (Test-Path -LiteralPath $data) { @($data) + @(Get-ChildItem -LiteralPath $data -Recurse -Force | ForEach-Object FullName) } else { @() } }
    $before = & $list
    $saved = $env:LOCALAPPDATA
    $savedEnv = @{}
    try {
        $env:LOCALAPPDATA = $local
        foreach ($k in $Env.Keys) { $savedEnv[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $Env[$k]) }
        $out = @(& pwsh -NoProfile -NonInteractive -File $Script -Port (Get-FreePort) @Extra 2>&1 | ForEach-Object { "$_" })
        $code = $LASTEXITCODE
    }
    finally {
        $env:LOCALAPPDATA = $saved
        foreach ($k in $savedEnv.Keys) { [Environment]::SetEnvironmentVariable($k, $savedEnv[$k]) }
    }
    $after = & $list
    @{ Out = $out; Text = ($out -join "`n"); Code = $code; Unchanged = (($before -join '|') -eq ($after -join '|')) }
}

try {
    # AC5: no config and no -Mac: the dry run names the missing MAC, runs nothing, exits 1
    $r = Invoke-DryRun
    Check 'no mac: named' ($r.Text -match 'blocked: no station MAC: pass -Mac') $r.Text
    Check 'no mac: exit 1' ($r.Code -eq 1) "exit $($r.Code)"
    Check 'no mac: nothing written' $r.Unchanged
    Check 'no mac: says nothing changed' ($r.Text -match 'dry run: nothing changed')

    # -Mac: the template would carry it
    $r = Invoke-DryRun -Extra @('-Mac', 'AA:BB:CC:DD:EE:01', '-CapacityWh', '2048')
    Check '-Mac: no mac problem' ($r.Text -notmatch 'no station MAC') $r.Text
    Check '-Mac: the config would be written with it' ($r.Text -match 'would\s+config: write .+config\.json from the template with mac AA:BB:CC:DD:EE:01') $r.Text
    Check '-Mac: nothing written' $r.Unchanged
    Check '-CapacityWh: the config would carry it' ($r.Text -match 'with mac AA:BB:CC:DD:EE:01 and capacityWh 2048') $r.Text
    Check '-Mac and -CapacityWh: exit 1 only with a blocked line' (($r.Code -eq 1) -eq ($r.Text -match 'blocked:')) "exit $($r.Code)"

    # a new install without -CapacityWh: named, nothing written, exit 1
    $r = Invoke-DryRun -Extra @('-Mac', 'AA:BB:CC:DD:EE:01')
    Check 'no capacity: named' ($r.Text -match 'blocked: no battery capacity: pass -CapacityWh' -and $r.Code -eq 1) $r.Text
    Check 'no capacity: nothing written' $r.Unchanged
    foreach ($bad in '0', '-5') {
        $r = Invoke-DryRun -Extra @('-Mac', 'AA:BB:CC:DD:EE:01', '-CapacityWh', $bad)
        Check "capacity ${bad}: refused" ($r.Text -match "blocked: -CapacityWh must be a positive number of Wh: $bad" -and $r.Code -eq 1) $r.Text
    }

    # an existing config (an upgrade): no -CapacityWh needed, the config kept, a missing capacityWh named, not blocked
    $r = Invoke-DryRun -Config '{ "mac": "AA:BB:CC:DD:EE:09" }'
    Check 'upgrade: no capacity problem' ($r.Text -notmatch 'no battery capacity|CapacityWh must') $r.Text
    Check 'upgrade: no capacityWh named with the default' ($r.Text -match 'it has no capacityWh, so the battery time assumes 2304 Wh') $r.Text
    $r = Invoke-DryRun -Config '{ "mac": "AA:BB:CC:DD:EE:09", "capacityWh": 1000 }' -Extra @('-Mac', 'AA:BB:CC:DD:EE:0A', '-CapacityWh', '2048')
    Check 'upgrade: -Mac and -CapacityWh only for a new config' ($r.Text -match 'keep .+config\.json as it is \(-Mac and -CapacityWh are used only for a new config\.json\)') $r.Text
    Check 'upgrade with capacityWh: no default note' ($r.Text -notmatch 'assumes 2304') $r.Text
    Check 'upgrade: unchanged' $r.Unchanged

    # the template install.ps1 writes: JSON with the mac and the capacity
    $tast = [Management.Automation.Language.Parser]::ParseFile($installer, [ref] $null, [ref] $null)
    $tfn = $tast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'New-WatcherConfigText' }, $true) | Select-Object -First 1
    Check 'template: install.ps1 has New-WatcherConfigText' ($null -ne $tfn)
    if ($tfn) {
        . ([scriptblock]::Create($tfn.Extent.Text))
        $cfg = New-WatcherConfigText 'AA:BB:CC:DD:EE:0B' 2048 | ConvertFrom-Json
        Check 'template: mac and capacityWh' ($cfg.mac -ceq 'AA:BB:CC:DD:EE:0B' -and $cfg.capacityWh -eq 2048) "$($cfg | ConvertTo-Json -Compress)"
    }

    # a malformed -Mac
    $r = Invoke-DryRun -Extra @('-Mac', 'not-a-mac')
    Check 'bad -Mac: refused' ($r.Text -match 'blocked: -Mac is not a Bluetooth address: not-a-mac' -and $r.Code -eq 1) $r.Text

    # an existing config without mac: named, never changed
    $r = Invoke-DryRun -Config "{`n  // a comment`n  `"port`": 18940`n}" -Extra @('-Mac', 'AA:BB:CC:DD:EE:02')
    Check 'config without mac: names the file' ($r.Text -match 'blocked: no station MAC: add "mac": "AA:BB:CC:DD:EE:FF" to .+config\.json' -and $r.Code -eq 1) $r.Text
    Check 'config without mac: kept, -Mac only for a new one' ($r.Text -match 'keep .+config\.json as it is \(-Mac is used only for a new config\.json\)') $r.Text
    Check 'config without mac: unchanged' $r.Unchanged

    # an existing config with mac: no mac problem; its wake task name is used
    $r = Invoke-DryRun -Config '{ "mac": "AA:BB:CC:DD:EE:03", "wakeTaskName": "MyWakeTask" }'
    Check 'config with mac: no mac problem' ($r.Text -notmatch 'no station MAC|not a Bluetooth address') $r.Text
    Check 'config with mac: the wake task name from the config' ($r.Text -match "register the wake task 'MyWakeTask'") $r.Text

    # the 1.0.0 Python venv in the data directory: the dry run says it would be removed (after the health check), and removes nothing
    $r = Invoke-DryRun -Config '{ "mac": "AA:BB:CC:DD:EE:06" }' -Dirs @('.venv\Scripts')
    Check 'old venv: named as removed' ($r.Text -match 'would\s+remove the 1\.0\.0 Python venv .+\.venv') $r.Text
    Check 'old venv: nothing removed' $r.Unchanged
    $r = Invoke-DryRun -Config '{ "mac": "AA:BB:CC:DD:EE:07" }'
    Check 'no old venv: nothing about it' ($r.Text -notmatch 'Python venv') $r.Text

    # a config that is no JSON
    $r = Invoke-DryRun -Config '{ "mac": '
    Check 'broken config: named' ($r.Text -match 'blocked: .+config\.json cannot be read as JSON' -and $r.Code -eq 1) $r.Text

    # a release layout: the exe is copied, no dotnet needed; the power-mode switch found beside it
    $rel = Join-Path $base 'release'
    foreach ($d in 'bin', 'tools', 'power-mode') { $null = New-Item -ItemType Directory -Path (Join-Path $rel $d) }
    Copy-Item -LiteralPath $installer -Destination $rel
    foreach ($f in 'swap.ps1', 'wake-task.ps1', 'broker-task.ps1', 'shortcut.ps1') { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $f) -Destination (Join-Path $rel 'tools') }
    [IO.File]::WriteAllText((Join-Path $rel 'bin\BluettiWatcher.exe'), 'stand-in')
    [IO.File]::WriteAllText((Join-Path $rel 'power-mode\power-mode.ps1'), '# stand-in')
    $r = Invoke-DryRun -Script (Join-Path $rel 'install.ps1') -Extra @('-Mac', 'AA:BB:CC:DD:EE:04', '-CapacityWh', '2048')
    Check 'release: copies the exe' ($r.Text -match "would\s+copy the release's .+\\bin to the staging directory") $r.Text
    Check 'release: no publish, no dotnet' ($r.Text -notmatch 'publish NativeAOT|no dotnet') $r.Text
    Check 'release: the power-mode switch is copied' ($r.Text -match 'would\s+copy the power-mode switch .+power-mode\\\*\.ps1 to .+bluetti-watcher\\power-mode') $r.Text
    Check 'release: no power-mode problem' ($r.Text -notmatch 'no .+power-mode\.ps1') $r.Text
    Check 'release: exit 1 only with a blocked line' (($r.Code -eq 1) -eq ($r.Text -match 'blocked:') -and $r.Code -in 0, 1) "exit $($r.Code)"
    Check 'release: nothing written' $r.Unchanged

    # a checkout: published
    $r = Invoke-DryRun -Extra @('-Mac', 'AA:BB:CC:DD:EE:05', '-CapacityWh', '2048')
    Check 'checkout: publishes into the staging directory' ($r.Text -match 'would\s+publish NativeAOT to the staging directory') $r.Text
    $r = Invoke-DryRun -Extra @('-Mac', 'AA:BB:CC:DD:EE:05', '-CapacityWh', '2048', '-Fallback')
    Check 'checkout: -Fallback is the single file' ($r.Text -match 'would\s+publish trimmed self-contained single file') $r.Text

    # -RemoveWake without -Apply: a dry run
    $r = Invoke-DryRun -Extra @('-RemoveWake')
    Check 'remove wake: dry run' ($r.Code -eq 0 -and $r.Text -match "would\s+remove the wake task 'BluettiWatcherWake'" -and $r.Unchanged) $r.Text
    # -Broker without -Apply: the broker's steps alone; no installed exe = blocked, exit 1, nothing written
    $r = Invoke-DryRun -Extra @('-Broker')
    Check 'broker: folder step' ($r.Text -match 'would\s+create .+\\BluettiWatcher\\broker with profiles\\.+ and .+\\BluettiWatcherBroker with requests\\') $r.Text
    Check 'broker: exe copy step, never the user bin' ($r.Text -match 'would\s+copy .+BluettiWatcher\.exe to .+\\broker\\bin\\BluettiWatcher\.exe \(SYSTEM runs this copy') $r.Text
    Check 'broker: task step' ($r.Text -match "would\s+register the task 'BluettiWatcherBroker': SYSTEM, no trigger") $r.Text
    Check 'broker: no exe blocks, exit 1' ($r.Code -eq 1 -and $r.Text -match 'blocked: no installed .+BluettiWatcher\.exe to copy') "exit $($r.Code)"
    Check 'broker: nothing written' $r.Unchanged
    Check 'broker: no service steps' ($r.Text -notmatch 'swap|shortcut') $r.Text
    # a normal dry run lists the broker's steps as the elevated part, and how to run it from a shell that is not elevated
    $r = Invoke-DryRun -Extra @('-Mac', 'AA:BB:CC:DD:EE:01', '-CapacityWh', '2048')
    Check 'install: names the broker step' ($r.Text -match 'install\.ps1 -Broker -Apply in an elevated shell') $r.Text
    Check 'install: the elevated part is labelled' ($r.Text -match '(?m)^elevated part \(the admin broker') $r.Text
    Check 'install: broker folder step is an elevated would line' ($r.Text -match 'would\s+elevated: create .+\\BluettiWatcher\\broker with profiles\\.+ and .+\\BluettiWatcherBroker with requests\\') $r.Text
    Check 'install: broker exe copy step is an elevated would line' ($r.Text -match 'would\s+elevated: copy .+BluettiWatcher\.exe to .+\\broker\\bin\\BluettiWatcher\.exe') $r.Text
    Check 'install: broker task step is an elevated would line' ($r.Text -match "would\s+elevated: register the task 'BluettiWatcherBroker': SYSTEM") $r.Text
    Check 'install: broker steps after the service steps' ($r.Text.IndexOf("register the task 'BluettiWatcherBroker'") -gt $r.Text.IndexOf("register the logon task 'BluettiWatcher'")) $r.Text
    # the shortcut carrying the app id is written before the swap starts the new service: its first notices are toasts
    Check 'install: shortcut step before the swap' ($r.Text.IndexOf('create the shortcuts') -ge 0 -and $r.Text.IndexOf('create the shortcuts') -lt $r.Text.IndexOf('swap, the only downtime')) $r.Text
    Check 'install: no leave-as-it-is broker line' ($r.Text -notmatch 'leave the admin broker') $r.Text
    Check 'install with broker steps: nothing written' $r.Unchanged
    # a prefix no task has: no old GPU task to remove
    $r = Invoke-DryRun -Config ('{ "mac": "AA:BB:CC:DD:EE:0C", "powerMode": { "taskPrefix": "BwInstallTest' + [Guid]::NewGuid().ToString('N') + '" } }')
    Check 'install: no old GPU task present, none named' ($r.Text -notmatch 'old GPU task') $r.Text
    Check 'install: no old GPU task present, unchanged' $r.Unchanged
    # the wake task name from the config: never the service's own task, only plain characters
    $mac = '"mac": "AA:BB:CC:DD:EE:06"'
    foreach ($bad in 'BluettiWatcher', 'bluettiwatcher', 'a\\b', 'x*', 'a;b', 'a\"b', 'abc\n', ' lead', 'trail.', ('n' * 101)) {
        $r = Invoke-DryRun -Config "{ $mac, `"wakeTaskName`": `"$bad`" }"
        Check "wake name '$bad': blocked" ($r.Code -eq 1 -and $r.Text -match 'blocked: wakeTaskName in .+config\.json: refused: the wake task name') $r.Text
        Check "wake name '$bad': nothing written" $r.Unchanged
    }
    $r = Invoke-DryRun -Config "{ $mac, `"wakeTaskName`": `"BluettiWatcher`" }"
    Check 'wake name = service task: says so' ($r.Text -match "is the service's own task") $r.Text
    $r = Invoke-DryRun -Config "{ $mac, `"wakeTaskName`": `"My Wake-task_1.2`" }"
    Check 'wake name with every allowed character: accepted' ($r.Text -notmatch 'wakeTaskName' -and $r.Text -match "register the wake task 'My Wake-task_1\.2'") $r.Text
    $r = Invoke-DryRun -Config "{ $mac, `"wakeTaskName`": `"BluettiWatcher`" }" -Extra @('-RemoveWake')
    Check '-RemoveWake with the service task name: refused, exit 1' ($r.Code -eq 1 -and $r.Text -match 'blocked: wakeTaskName' -and $r.Text -notmatch 'would\s+remove the wake task') $r.Text
    $r = Invoke-DryRun -Config "{ $mac, `"WakeTaskName`": `"Other`" }"
    Check 'wake name key in the wrong case: blocked' ($r.Code -eq 1 -and $r.Text -match 'blocked: the key WakeTaskName in .+config\.json must be written exactly "wakeTaskName"') $r.Text

    # the service reads keys case-sensitively: "MAC" is a missing mac
    $r = Invoke-DryRun -Config '{ "MAC": "AA:BB:CC:DD:EE:07" }'
    Check 'MAC key: named as a missing mac' ($r.Code -eq 1 -and $r.Text -match 'blocked: no station MAC: the key MAC in .+config\.json must be written exactly "mac"') $r.Text
    $r = Invoke-DryRun -Config '{ "mac": "AA:BB:CC:DD:EE:07", "MAC": "x" }'
    Check 'mac key present: a second MAC key is no problem' ($r.Text -notmatch 'no station MAC') $r.Text

    # no Python: a python that fails first on PATH changes nothing, and no step names a venv
    $fake = Join-Path $base 'fakepython'
    $null = New-Item -ItemType Directory -Path $fake
    [IO.File]::WriteAllText((Join-Path $fake 'python.cmd'), "@exit /b 9`r`n")
    $r = Invoke-DryRun -Extra @('-Mac', 'AA:BB:CC:DD:EE:08', '-CapacityWh', '2048') -Env @{ PATH = "$fake;$env:PATH" }
    Check 'no Python needed: no python or venv in any line' ($r.Text -notmatch '(?i)python|venv|requirements\.lock') $r.Text
    Check 'no Python needed: nothing blocked by it' ($r.Text -notmatch 'blocked: .*(?i)python') $r.Text
    Check 'shortcuts: the Start-menu one would carry the app id' ($r.Text -match 'Programs\\Bluetti Watcher\.lnk with the app id BluettiWatcher') $r.Text

    # the shortcuts (tools/shortcut.ps1, on temp files): the Start-menu one carries the app id the service's toasts use
    . (Join-Path $repo 'tools\shortcut.ps1')
    $lnkDir = Join-Path $base 'shortcuts'
    $null = New-Item -ItemType Directory -Path $lnkDir
    $startLnk = Join-Path $lnkDir 'start.lnk'
    $deskLnk = Join-Path $lnkDir 'desk.lnk'
    $browserExe = Join-Path ([Environment]::GetFolderPath('Windows')) 'notepad.exe'
    Set-WatcherShortcut -Path $startLnk -Browser $browserExe -Url 'http://127.0.0.1:18940/' -AppId $script:WatcherAppId
    Set-WatcherShortcut -Path $deskLnk -Browser $browserExe -Url 'http://127.0.0.1:18940/'
    Check 'shortcuts: the app id is set and reads back' ((Get-WatcherShortcutAppId $startLnk) -ceq 'BluettiWatcher') "$(Get-WatcherShortcutAppId $startLnk)"
    Check 'shortcuts: without -AppId none is set' ($null -eq (Get-WatcherShortcutAppId $deskLnk)) "$(Get-WatcherShortcutAppId $deskLnk)"
    $read = (New-Object -ComObject WScript.Shell).CreateShortcut($startLnk)
    Check 'shortcuts: the target and the app-mode page stay' ($read.TargetPath -ieq $browserExe -and $read.Arguments -ceq '--app=http://127.0.0.1:18940/') "$($read.TargetPath) $($read.Arguments)"
    $toastsSource = Get-Content -Raw -LiteralPath (Join-Path $repo 'src\BluettiWatcher\Tray\Toasts.cs')
    Check 'shortcuts: the app id is the one the service toasts under' ($toastsSource -cmatch "AppId = `"$([regex]::Escape($script:WatcherAppId))`";")
    # the 1.0.0 venv's removal (the function out of install.ps1, run on temp folders only): a plain folder goes whole; a
    # junction or symlink at the venv, or inside it, is unlinked alone and what it points at stays; a file is refused
    $ast = [Management.Automation.Language.Parser]::ParseFile($installer, [ref] $null, [ref] $null)
    $fn = $ast.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Remove-WatcherOldVenv' }, $true) | Select-Object -First 1
    Check 'old venv removal: install.ps1 has Remove-WatcherOldVenv' ($null -ne $fn)
    if ($fn) {
        . ([scriptblock]::Create($fn.Extent.Text))
        $v = Join-Path $base 'venvtest'
        $null = New-Item -ItemType Directory -Path $v
        $keepDir = Join-Path $v 'keep'
        $null = New-Item -ItemType Directory -Path $keepDir
        $keep = Join-Path $keepDir 'keep.txt'
        [IO.File]::WriteAllText($keep, 'x')

        $plain = Join-Path $v 'plain'
        $null = New-Item -ItemType Directory -Path (Join-Path $plain 'Scripts')
        [IO.File]::WriteAllText((Join-Path $plain 'Scripts\python.txt'), 'x')
        $text = Remove-WatcherOldVenv $plain
        Check 'old venv removal: a plain folder goes' (-not (Test-Path -LiteralPath $plain)) $text
        Check 'old venv removal: a plain folder is named removed' ($text -match 'removed the 1\.0\.0 Python venv') $text

        $link = Join-Path $v 'linked'
        $null = New-Item -ItemType Junction -Path $link -Target $keepDir
        $text = Remove-WatcherOldVenv $link
        Check 'old venv removal: a junction at the venv is unlinked' (-not (Test-Path -LiteralPath $link)) $text
        Check 'old venv removal: what the junction pointed at stays' (Test-Path -LiteralPath $keep) $text
        Check 'old venv removal: a junction is named a link' ($text -match 'removed the link') $text

        $nested = Join-Path $v 'nested'
        $null = New-Item -ItemType Directory -Path $nested
        $null = New-Item -ItemType Junction -Path (Join-Path $nested 'Lib') -Target $keepDir
        $null = Remove-WatcherOldVenv $nested
        Check 'old venv removal: a venv holding a junction goes' (-not (Test-Path -LiteralPath $nested))
        Check 'old venv removal: what the inner junction pointed at stays' (Test-Path -LiteralPath $keep)

        $symlink = Join-Path $v 'symlinked'
        $made = $false
        try { $null = New-Item -ItemType SymbolicLink -Path $symlink -Target $keepDir; $made = $true } catch { $made = $false }   # needs a privilege: skipped without it
        if ($made) {
            $null = Remove-WatcherOldVenv $symlink
            Check 'old venv removal: a symlink at the venv is unlinked' (-not (Test-Path -LiteralPath $symlink))
            Check 'old venv removal: what the symlink pointed at stays' (Test-Path -LiteralPath $keep)
        }

        $file = Join-Path $v 'afile'
        [IO.File]::WriteAllText($file, 'x')
        $threw = $false
        try { $null = Remove-WatcherOldVenv $file } catch { $threw = $true }
        Check 'old venv removal: a plain file is refused and stays' ($threw -and (Test-Path -LiteralPath $file))
    }

    # Install-WatcherBroker against fake icacls / Task Scheduler in a temp folder: what SYSTEM runs, who may write.
    & {
        . (Join-Path $PSScriptRoot 'broker-task.ps1')
        $root = Join-Path $base 'broker'
        $exchange = Join-Path $base 'exchange'
        $goodAcl = { param($path) @{ owner = 'S-1-5-32-544'; protected = $true; aces = @() } }
        $src = Join-Path $base 'src-bin\BluettiWatcher.exe'
        $null = New-Item -ItemType Directory -Force -Path (Split-Path $src)
        [IO.File]::WriteAllText($src, 'exe')
        $sid = 'S-1-5-21-1-2-3-1001'
        $calls = [Collections.Generic.List[string]]::new()
        $registered = @{}
        $ops = @{
            Icacls   = { param([string[]] $list) $calls.Add($list -join ' ') }.GetNewClosure()
            Register = { param($name, $xml, $sddl) $registered.name = $name; $registered.xml = $xml; $registered.sddl = $sddl }.GetNewClosure()
            Query    = { param($name) $registered.xml }.GetNewClosure()
            ReadAcl  = $goodAcl
        }
        $r = Install-WatcherBroker -Root $root -Exchange $exchange -SourceExe $src -Sid $sid -Ops $ops
        Check 'broker: installed' $r.ok $r.text
        Check 'broker: the exe is copied into the admin folder' ((Get-Content -Raw (Join-Path $root 'bin\BluettiWatcher.exe')) -eq 'exe')
        Check 'broker: the task runs that copy as SYSTEM with --broker' ($registered.xml -match '<UserId>S-1-5-18</UserId>' -and $registered.xml.Contains("<Command>$root\bin\BluettiWatcher.exe</Command>") -and $registered.xml -match '<Arguments>--broker</Arguments>') $registered.xml
        Check 'broker: the task has no trigger' ($registered.xml -notmatch '<Triggers>') $registered.xml
        Check 'broker: the user may read and run the task only' ($registered.sddl -ceq "D:P(A;;FA;;;BA)(A;;FA;;;SY)(A;;FRFX;;;$sid)") $registered.sddl
        Check 'broker: inheritance cut, the user reads' (@($calls | Where-Object { $_.StartsWith("$root /inheritance:r") -and $_.Contains("*${sid}:(OI)(CI)RX") -and $_ -notmatch "\*${sid}:\(OI\)\(CI\)(F|M|W)" }).Count -eq 1) ($calls -join ' | ')
        Check 'broker: requests lets the user add files, never folders' (@($calls | Where-Object { $_ -match 'requests' -and $_.Contains("*${sid}:(WD)") -and $_ -notmatch '\(AD\)|\(CI\)' }).Count -eq 1) ($calls -join ' | ')
        Check 'broker: owner Administrators after every file is written' ($calls[$calls.Count - 1] -match '/setowner \*S-1-5-32-544 /T') ($calls -join ' | ')
        $policy = Get-Content -Raw (Join-Path $root 'policy.json') | ConvertFrom-Json
        Check 'broker: the policy names the user and allows nothing' ($policy.userSid -eq $sid -and @($policy.services).Count -eq 0 -and @($policy.tasks).Count -eq 0 -and @($policy.profiles).Count -eq 0) (Get-Content -Raw (Join-Path $root 'policy.json'))
        [IO.File]::WriteAllText((Join-Path $root 'policy.json'), '{"keep":1}')
        $null = Install-WatcherBroker -Root $root -Exchange $exchange -SourceExe $src -Sid $sid -Ops $ops
        Check 'broker: an existing policy is never replaced' ((Get-Content -Raw (Join-Path $root 'policy.json')) -eq '{"keep":1}')
        $ops.Query = { param($name) '<Task><Principals><Principal><UserId>S-1-5-21-9</UserId></Principal></Principals></Task>' }
        $r = Install-WatcherBroker -Root $root -Exchange $exchange -SourceExe $src -Sid $sid -Ops $ops
        Check 'broker: a task read back as another account fails' (-not $r.ok -and $r.text -match 'not registered as written') $r.text
    }

    # The broker's two folders and their ancestors, against fake icacls / ACL reads / Task Scheduler: what SYSTEM runs sits
    # in an admin-only tree no ancestor of which the user can rename, re-own or re-permission.
    & {
        . (Join-Path $PSScriptRoot 'broker-task.ps1')
        $sid = 'S-1-5-21-1-2-3-1001'
        $src = Join-Path $base 'src-bin2\BluettiWatcher.exe'
        $null = New-Item -ItemType Directory -Force -Path (Split-Path $src)
        [IO.File]::WriteAllText($src, 'exe')
        $world = {
            param([string] $name)
            $w = Join-Path $base $name
            @{ Root = Join-Path $w 'pf\BluettiWatcher\broker'; Exchange = Join-Path $w 'pd\BluettiWatcherBroker'; Old = Join-Path $w 'pd\BluettiWatcher\broker' }
        }
        # Every path reads as admin-owned, protected, the user reading only, unless $acls says otherwise.
        $read = 0x1200A9
        $acl = { param([string] $owner, [int] $userRights, [bool] $inheritOnly = $false) @{ owner = $owner; protected = $true; aces = @(
            @{ sid = 'S-1-5-32-544'; rights = 0x1F01FF; allow = $true; inheritOnly = $false }
            @{ sid = $sid; rights = $userRights; allow = $true; inheritOnly = $inheritOnly }) } }
        $newOps = {
            param([hashtable] $acls)
            $calls = [Collections.Generic.List[string]]::new()
            $reg = @{}
            $removed = [Collections.Generic.List[string]]::new()
            $acl = $acl; $sid = $sid; $read = $read
            @{
                Calls    = $calls
                Removed  = $removed
                Icacls   = { param([string[]] $list) $calls.Add($list -join ' ') }.GetNewClosure()
                Register = { param($name, $xml, $sddl) $reg[$name] = $xml }.GetNewClosure()
                Query    = { param($name) $reg[$name] }.GetNewClosure()
                ReadAcl  = { param([string] $path) if ($acls.ContainsKey($path)) { $a = $acls[$path]; if ($a -is [scriptblock]) { & $a } else { $a } } else { & $acl 'S-1-5-32-544' $read } }.GetNewClosure()
                RemoveTree = { param([string] $path) $removed.Add($path); [IO.Directory]::Delete($path, $true) }.GetNewClosure()
            }
        }
        $install = { param($w, $ops) Install-WatcherBroker -Root $w.Root -Exchange $w.Exchange -OldRoot $w.Old -SourceExe $src -Sid $sid -Ops $ops }

        # a clean install: the exe and the policy under the admin root, requests and responses under the exchange root
        $w = & $world 'c1-clean'
        $ops = & $newOps @{}
        $r = & $install $w $ops
        Check 'c1: installed' $r.ok $r.text
        Check 'c1: the exe SYSTEM runs is under the admin root' ((Get-Content -Raw (Join-Path $w.Root 'bin\BluettiWatcher.exe')) -eq 'exe')
        Check 'c1: the policy is under the admin root' (Test-Path -LiteralPath (Join-Path $w.Root 'policy.json'))
        Check 'c1: requests and responses are under the exchange root only' ((Test-Path -LiteralPath (Join-Path $w.Exchange 'requests')) -and (Test-Path -LiteralPath (Join-Path $w.Exchange 'responses')) -and -not (Test-Path -LiteralPath (Join-Path $w.Root 'requests')))
        $calls = @($ops.Calls)
        Check 'c1: the admin root gives the user read only' (@($calls | Where-Object { $_.StartsWith("$($w.Root) /inheritance:r") -and $_.Contains("*${sid}:(OI)(CI)RX") -and $_ -notmatch "\*${sid}:\(OI\)\(CI\)(F|M|W)" }).Count -eq 1) ($calls -join ' | ')
        Check 'c1: the exchange root gives the user read only' (@($calls | Where-Object { $_.StartsWith("$($w.Exchange) /inheritance:r") -and $_.Contains("*${sid}:(OI)(CI)RX") }).Count -eq 1) ($calls -join ' | ')
        Check 'c1: requests lets the user add files' (@($calls | Where-Object { $_.StartsWith((Join-Path $w.Exchange 'requests')) -and $_.Contains("*${sid}:(WD)") -and $_ -notmatch '\(AD\)|\(CI\)' }).Count -eq 1) ($calls -join ' | ')
        Check 'c1: every child is reset to inherit, under both roots' (((@($calls | Where-Object { $_ -match '/reset /T' }) | ForEach-Object { ($_ -split ' /reset')[0] } | Sort-Object) -join ',') -ceq ((@('bin', 'exports', 'profiles' | ForEach-Object { Join-Path $w.Root $_ }) + @('requests', 'responses' | ForEach-Object { Join-Path $w.Exchange $_ }) | Sort-Object) -join ',')) ($calls -join ' | ')
        Check 'c1: the root ACL is set before its children are reset' ([Array]::FindIndex([string[]]$calls, [Predicate[string]]{ param($c) $c -match '/inheritance:r' }) -lt [Array]::FindIndex([string[]]$calls, [Predicate[string]]{ param($c) $c -match '/reset /T' })) ($calls -join ' | ')

        # an ancestor the user may delete, re-permission, re-own or add into: refused before anything changes
        $cases = @(
            @{ name = 'DELETE on the parent of the admin root'; path = { param($w) Split-Path $w.Root }; acl = (& $acl 'S-1-5-32-544' 0x10000) }
            @{ name = 'WRITE_DAC on the parent of the admin root'; path = { param($w) Split-Path $w.Root }; acl = (& $acl 'S-1-5-32-544' 0x40000) }
            @{ name = 'owner of an ancestor of the admin root'; path = { param($w) Split-Path (Split-Path $w.Root) }; acl = (& $acl $sid $read) }
            @{ name = 'add-subfolder on the parent of the exchange root'; path = { param($w) Split-Path $w.Exchange }; acl = (& $acl 'S-1-5-32-544' 0x4) }
            @{ name = 'delete-child on the parent of the exchange root'; path = { param($w) Split-Path $w.Exchange }; acl = (& $acl 'S-1-5-32-544' 0x40) }
            @{ name = 'an ACL that cannot be read'; path = { param($w) Split-Path $w.Root }; acl = { throw 'access denied' } }
        )
        $i = 0
        foreach ($c in $cases) {
            $w = & $world "c1-anc-$i"; $i++
            $null = New-Item -ItemType Directory -Force -Path (Split-Path $w.Root), (Split-Path $w.Exchange)
            $ops = & $newOps @{ (& $c.path $w) = $c.acl }
            $r = & $install $w $ops
            Check "c1: refused, $($c.name)" (-not $r.ok -and $r.text -match 'refused') $r.text
            Check "c1: nothing changed, $($c.name)" ($ops.Calls.Count -eq 0 -and -not (Test-Path -LiteralPath (Join-Path $w.Root 'bin'))) ($ops.Calls -join ' | ')
        }
        # inherit-only ACEs do not apply to the ancestor itself
        $w = & $world 'c1-io'
        $null = New-Item -ItemType Directory -Force -Path (Split-Path $w.Root)
        $r = & $install $w (& $newOps @{ (Split-Path $w.Root) = (& $acl 'S-1-5-32-544' 0x1F01FF $true) })
        Check 'c1: an inherit-only ACE on an ancestor is not a grant on it' $r.ok $r.text

        # the ACLs are read back after they are set (a root still writable by the user fails the install)
        $w = & $world 'c1-back'
        $r = & $install $w (& $newOps @{ $w.Root = (& $acl 'S-1-5-32-544' 0x1301BF) })
        Check 'c1: a root the user may still write fails the read-back' (-not $r.ok -and $r.text -match 'read back') $r.text
        $w = & $world 'c1-back2'
        $r = & $install $w (& $newOps @{ (Join-Path $w.Exchange 'responses') = (& $acl 'S-1-5-32-544' ($read -bor 0x2)) })
        Check 'c1: responses the user may add to fails the read-back' (-not $r.ok -and $r.text -match 'read back') $r.text
        $w = & $world 'c1-back3'
        $r = & $install $w (& $newOps @{ (Join-Path $w.Exchange 'requests') = (& $acl 'S-1-5-32-544' ($read -bor 0x2)) })
        Check 'c1: requests the user may add files to passes the read-back' $r.ok $r.text
        $w = & $world 'c1-back4'
        $r = & $install $w (& $newOps @{ (Join-Path $w.Root 'bin') = (& $acl $sid $read) })
        Check 'c1: a bin the user owns fails the read-back' (-not $r.ok -and $r.text -match 'read back') $r.text

        # upgrade: the old broker inside the flag folder is carried out and removed
        $w = & $world 'c1-upgrade'
        $null = New-Item -ItemType Directory -Force -Path (Join-Path $w.Old 'bin'), (Join-Path $w.Old 'profiles'), (Join-Path $w.Old 'requests')
        [IO.File]::WriteAllText((Join-Path $w.Old 'bin\BluettiWatcher.exe'), 'old')
        [IO.File]::WriteAllText((Join-Path $w.Old 'policy.json'), '{"version":1,"userSid":"S-1-5-21-1-2-3-1001","services":["WSearch"],"tasks":[],"profiles":["a.nip"],"profileInspector":""}')
        [IO.File]::WriteAllText((Join-Path $w.Old 'profiles\a.nip'), 'nip')
        $ops = & $newOps @{}
        $r = & $install $w $ops
        Check 'c1 upgrade: installed' $r.ok $r.text
        Check 'c1 upgrade: the old broker folder is gone' (-not (Test-Path -LiteralPath $w.Old) -and $ops.Removed -contains $w.Old) ($ops.Removed -join ',')
        Check 'c1 upgrade: the admin-owned policy is carried' ((Get-Content -Raw (Join-Path $w.Root 'policy.json')) -match 'WSearch') (Get-Content -Raw (Join-Path $w.Root 'policy.json'))
        Check 'c1 upgrade: the admin-owned profiles are carried' ((Get-Content -Raw (Join-Path $w.Root 'profiles\a.nip')) -eq 'nip')
        Check 'c1 upgrade: the carry is named' ($r.text -match 'carried') $r.text
        # a policy in the old folder the user owns is not carried (the user could write it): a fresh one, the old folder still removed
        $w = & $world 'c1-upgrade-user'
        $null = New-Item -ItemType Directory -Force -Path $w.Old
        [IO.File]::WriteAllText((Join-Path $w.Old 'policy.json'), '{"version":1,"userSid":"S-1-5-21-1-2-3-1001","services":["Evil"],"tasks":[],"profiles":[],"profileInspector":""}')
        $ops = & $newOps @{ (Join-Path $w.Old 'policy.json') = (& $acl $sid $read) }
        $r = & $install $w $ops
        Check 'c1 upgrade: a user-owned old policy is not carried' ($r.ok -and (Get-Content -Raw (Join-Path $w.Root 'policy.json')) -notmatch 'Evil' -and -not (Test-Path -LiteralPath $w.Old)) $r.text

        # W1: the policy's user is the service task's principal, not the elevated caller
        $q = @{ Query = { param($name) if ($name -ceq 'BluettiWatcher') { '<Task><Principals><Principal id="Author"><UserId>S-1-5-21-7-7-7-1002</UserId><LogonType>InteractiveToken</LogonType></Principal></Principals></Task>' } else { $null } } }
        Check 'w1: the service task principal SID' ((Get-WatcherServiceUserSid -TaskName 'BluettiWatcher' -Ops $q) -ceq 'S-1-5-21-7-7-7-1002')
        Check 'w1: no service task, no SID' ($null -eq (Get-WatcherServiceUserSid -TaskName 'Missing' -Ops $q))
        $q2 = @{ Query = { param($name) '<Task><Principals><Principal id="Author"><UserId>NT AUTHORITY\SYSTEM</UserId></Principal></Principals></Task>' } }
        Check 'w1: a service task run as SYSTEM gives no user SID' ($null -eq (Get-WatcherServiceUserSid -TaskName 'BluettiWatcher' -Ops $q2))
        $r = Invoke-WatcherBrokerStep -Elevated $true -Root 'x' -Exchange 'y' -SourceExe $src -Sid '' -Prefix 'MyEco' -Ops $q2
        Check 'w1: elevated with no service user: not installed, named' (-not $r.ok -and $r.text -match 'service task''s user cannot be read') $r.text

        # the old GPU cap tasks, below, against a broker in its own temp folders
        $root = Join-Path $base 'broker-gpu'
        $exchange = Join-Path $base 'exchange-gpu'
        $goodAcl = { param($path) @{ owner = 'S-1-5-32-544'; protected = $true; aces = @() } }

        # the old GPU cap tasks: their names from config powerMode.taskPrefix (key read case-sensitively, as the service does)
        $cfgDir = Join-Path $base 'prefix'
        $null = New-Item -ItemType Directory -Force -Path $cfgDir
        $cfgFile = Join-Path $cfgDir 'config.json'
        Check 'old gpu: no config = the default prefix' ((Get-WatcherPowerModeTaskPrefix $cfgFile) -ceq 'BluettiWatcherEco')
        [IO.File]::WriteAllText($cfgFile, "{`n  // a comment`n  `"mac`": `"AA:BB:CC:DD:EE:01`", `"powerMode`": { `"taskPrefix`": `"MyEco`" } }")
        Check 'old gpu: the prefix from the config' ((Get-WatcherPowerModeTaskPrefix $cfgFile) -ceq 'MyEco')
        [IO.File]::WriteAllText($cfgFile, '{ "powerMode": { "TaskPrefix": "MyEco" } }')
        Check 'old gpu: a key in the wrong case is not read' ((Get-WatcherPowerModeTaskPrefix $cfgFile) -ceq 'BluettiWatcherEco')
        foreach ($bad in '{ "powerMode": { "taskPrefix": "-Mutex" } }', '{ "mac": ') {
            [IO.File]::WriteAllText($cfgFile, $bad)
            Check "old gpu: no prefix from $bad" ($null -eq (Get-WatcherPowerModeTaskPrefix $cfgFile))
        }
        Check 'old gpu: the two task names' ((@(Get-WatcherOldGpuTaskNames 'MyEco') -join ',') -ceq 'MyEcoGpuCap,MyEcoGpuRestore')

        # given registered tasks and fakes for icacls / Task Scheduler; the GPU restore task is not there
        $present = @{ MyEcoGpuCap = '<Task>cap</Task>' }
        $deleted = [Collections.Generic.List[string]]::new()
        $newOps = {
            param([bool] $registerOk = $true, [bool] $deleteOk = $true)
            # locals, so each fake below closes over them
            $reg = @{}
            $present = $present
            $deleted = $deleted
            @{
                Icacls   = { param([string[]] $list) }
                ReadAcl  = $goodAcl
                Register = { param($name, $xml, $sddl) $reg[$name] = $xml }.GetNewClosure()
                Query    = { param($name) if ($reg.ContainsKey($name)) { if ($registerOk) { $reg[$name] } else { '<Task>other</Task>' } } elseif ($present.ContainsKey($name)) { $present[$name] } else { $null } }.GetNewClosure()
                Delete   = { param($name) if (-not $deleteOk) { throw 'ERROR: Access is denied.' }; $deleted.Add($name) }.GetNewClosure()
            }
        }
        $steps = @(Get-WatcherOldGpuTaskSteps -Prefix 'MyEco' -Ops (& $newOps))
        Check 'old gpu: dry run names each present task' ($steps.Count -eq 1 -and $steps[0] -match "remove the old GPU task 'MyEcoGpuCap'") ($steps -join ' | ')
        Check 'old gpu: dry run removes nothing' ($deleted.Count -eq 0)

        # when the broker registers, then the present old task goes and the missing one is not touched
        $deleted.Clear()
        $r = Complete-WatcherBroker -Root $root -Exchange $exchange -SourceExe $src -Sid $sid -Prefix 'MyEco' -Ops (& $newOps)
        Check 'old gpu: removed after the broker registered' ($r.ok -and ($deleted -join ',') -ceq 'MyEcoGpuCap') "$($r.text) | $($deleted -join ',')"
        Check 'old gpu: the removal is named' ($r.text -match "removed the old GPU task 'MyEcoGpuCap'") $r.text
        # when the broker registration fails, then the old tasks stay (the only GPU cap path left)
        $deleted.Clear()
        $r = Complete-WatcherBroker -Root $root -Exchange $exchange -SourceExe $src -Sid $sid -Prefix 'MyEco' -Ops (& $newOps $false)
        Check 'old gpu: kept when the broker was not registered' (-not $r.ok -and $deleted.Count -eq 0) "$($r.text) | $($deleted -join ',')"
        Check 'old gpu: kept is named' ($r.text -match 'old GPU tasks .*kept') $r.text
        # when the broker install throws, then the old tasks stay
        $deleted.Clear()
        $ops2 = & $newOps
        $ops2.Icacls = { param([string[]] $list) throw 'icacls failed' }
        $r = Complete-WatcherBroker -Root $root -Exchange $exchange -SourceExe $src -Sid $sid -Prefix 'MyEco' -Ops $ops2
        Check 'old gpu: kept when the broker install threw' (-not $r.ok -and $deleted.Count -eq 0) $r.text
        # when the prefix is unknown, then nothing is removed and that is named
        $deleted.Clear()
        $r = Complete-WatcherBroker -Root $root -Exchange $exchange -SourceExe $src -Sid $sid -Prefix $null -Ops (& $newOps)
        Check 'old gpu: no prefix, nothing removed' ($deleted.Count -eq 0 -and $r.text -match 'old GPU tasks') $r.text
        # when the removal fails, then it is named and the step fails
        $deleted.Clear()
        $r = Complete-WatcherBroker -Root $root -Exchange $exchange -SourceExe $src -Sid $sid -Prefix 'MyEco' -Ops (& $newOps $true $false)
        Check 'old gpu: a failed removal fails the step, named' (-not $r.ok -and $r.text -match "could not remove the old GPU task 'MyEcoGpuCap'") $r.text

        # the normal install's broker step: not elevated = one line naming the elevated step, nothing registered, not a failure
        $deleted.Clear()
        $ops3 = & $newOps
        $r = Invoke-WatcherBrokerStep -Elevated $false -Root $root -Exchange $exchange -SourceExe $src -Sid $sid -Prefix 'MyEco' -Ops $ops3
        Check 'not elevated: ok (the service install decides the exit)' ($r.ok) $r.text
        Check 'not elevated: names the elevated step' ($r.text -match 'run install\.ps1 -Broker -Apply in an elevated shell') $r.text
        Check 'not elevated: no broker yet, nothing about a stale copy' ($r.text -notmatch 'stale') $r.text
        Check 'not elevated: nothing registered or removed' ($null -eq (& $ops3.Query 'BluettiWatcherBroker') -and $deleted.Count -eq 0)
        # an upgrade with the broker registered: its copy of the exe is stale until the elevated step runs
        $ops3.Query = { param($name) if ($name -ceq 'BluettiWatcherBroker') { '<Task>broker</Task>' } else { $null } }
        $r = Invoke-WatcherBrokerStep -Elevated $false -Root $root -Exchange $exchange -SourceExe $src -Sid $sid -Prefix 'MyEco' -Ops $ops3
        Check 'not elevated, broker registered: the copy is stale until the step runs' ($r.ok -and $r.text -match 'stale' -and $r.text -match 'install\.ps1 -Broker -Apply in an elevated shell') $r.text
        # elevated: the broker installed and the old GPU task removed
        $r = Invoke-WatcherBrokerStep -Elevated $true -Root $root -Exchange $exchange -SourceExe $src -Sid $sid -Prefix 'MyEco' -Ops (& $newOps)
        Check 'elevated: the broker installed, the old GPU task removed' ($r.ok -and ($deleted -join ',') -ceq 'MyEcoGpuCap') "$($r.text) | $($deleted -join ',')"
    }
}
catch {
    Write-Output "FAIL  unexpected: $($_.Exception.Message) at $($_.InvocationInfo.ScriptLineNumber)"
    $script:failed++
}
finally {
    Remove-Item -LiteralPath $base -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Output "test-install: $script:passed passed, $script:failed failed"
if ($script:failed -gt 0) { exit 1 }
if ($script:passed -eq 0) { exit 2 }
exit 0
