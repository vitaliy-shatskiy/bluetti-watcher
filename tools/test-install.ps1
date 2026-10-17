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
    foreach ($f in 'swap.ps1', 'wake-task.ps1') { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $f) -Destination (Join-Path $rel 'tools') }
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
    }}
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
