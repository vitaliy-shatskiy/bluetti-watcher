<#
power-mode.ps1 - the power-mode switch of Bluetti Watcher: emergency (save electricity), eco (save electricity, keep
working) and normal, kept in one flag file. README.md beside this script is the user's guide.

  powershell -NoProfile -File power-mode.ps1 on|off|status|eco|end|battery|prev-plan <plan>|gpu-task install|remove|status [-Until HH:mm | -For <minutes>] [-Source manual|station] [-Path <flag file>]

The switch itself is BluettiWatcher.exe (`BluettiWatcher.exe power-mode <the same arguments>`); this script forwards
every argument to it unchanged and passes back its lines and its exit code, so callers of the script keep working.
Windows PowerShell 5.1 or PowerShell 7. The exe: BLUETTI_WATCHER_EXE when set, else ..\bin\BluettiWatcher.exe beside
this folder (the install layout). No exe = exit 2.

Exit (the exe's): status 0 normal · 1 emergency or eco · 2 could not read. on / off / eco / end / battery 0 written, or
already so · 2 could not run. gpu-task install / remove 0 done · 1 a task was not registered as written · 2 could not
run; gpu-task status 0 both registered · 1 not. prev-plan 0 rewritten · 1 no eco is on · 2 could not run.
#>
$ErrorActionPreference = 'Stop'
$exe = $env:BLUETTI_WATCHER_EXE
if (-not $exe) { $exe = Join-Path (Split-Path -Parent $PSScriptRoot) 'bin\BluettiWatcher.exe' }
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { Write-Output "power mode: COULD NOT RUN: no $exe (power-mode.ps1 forwards to BluettiWatcher.exe; BLUETTI_WATCHER_EXE names another)"; exit 2 }

# One argument as the C runtime splits a command line back (CommandLineToArgvW): quoted when empty or holding a space
# or a quote, backslashes doubled before a quote.
function ConvertTo-ExeArgument([string] $a) {
    if ($a.Length -gt 0 -and $a -notmatch '[\s"]') { return $a }
    $b = New-Object Text.StringBuilder
    [void] $b.Append('"')
    $slashes = 0
    foreach ($ch in $a.ToCharArray()) {
        if ($ch -eq [char] 92) { $slashes++; continue }
        if ($ch -eq [char] 34) { [void] $b.Append([char] 92, 2 * $slashes + 1).Append([char] 34) }
        else { [void] $b.Append([char] 92, $slashes).Append($ch) }
        $slashes = 0
    }
    [void] $b.Append([char] 92, 2 * $slashes).Append('"')
    return $b.ToString()
}

# `-Switch:$false` reaches $args as the name and then a Boolean: `-Switch:` then False from a host call (`& power-mode.ps1`),
# `-Switch` then False under -File. The exe gets it as one argument, `-Switch:False`: the value is not lost, and a plain
# -Switch (no Boolean after it) stays on.
$forward = New-Object Collections.Generic.List[string]
foreach ($a in $args) {
    if ($a -is [bool] -and $forward.Count -gt 0 -and $forward[$forward.Count - 1] -match '^-[A-Za-z_][A-Za-z0-9_]*:?$') {
        $forward[$forward.Count - 1] = $forward[$forward.Count - 1].TrimEnd(':') + ':' + $a
    }
    else { $forward.Add("$a") }
}

$psi = New-Object Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.Arguments = (@('power-mode') + @($forward | ForEach-Object { ConvertTo-ExeArgument "$_" })) -join ' '
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.StandardOutputEncoding = New-Object Text.UTF8Encoding $false
$psi.StandardErrorEncoding = New-Object Text.UTF8Encoding $false
$p = [Diagnostics.Process]::Start($psi)
$err = $p.StandardError.ReadToEndAsync()
$out = $p.StandardOutput.ReadToEnd()
$p.WaitForExit()
$lines = @($out -split "`r?`n")
if ($lines.Count -and $lines[-1] -eq '') { $lines = @($lines | Select-Object -First ($lines.Count - 1)) }
$lines | Write-Output
if ($err.Result) { [Console]::Error.Write($err.Result) }
exit $p.ExitCode
