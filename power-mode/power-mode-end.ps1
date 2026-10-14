<#
power-mode-end.ps1 - what an eco end task registered before Bluetti Watcher 1.1.0 runs (under Windows PowerShell 5.1):

  powershell.exe -NoProfile -ExecutionPolicy Bypass -File power-mode-end.ps1 -Path "<flag>" -FlagFile "<configured flag>" -Mutex <mutex> -TaskPrefix <prefix> [-ExtraReaders "<a,b>"] -TaskUntil <ISO>

It forwards every argument to `BluettiWatcher.exe power-mode end` (power-mode.ps1 finds the exe), which ends a due eco:
the plan and the GPU limit paid back, the flag normal, the task removed. Tasks registered from 1.1.0 on run the exe
directly. Exit 0 done or nothing to do · 2 the flag file cannot be trusted, or the write failed.
#>
& (Join-Path $PSScriptRoot 'power-mode.ps1') end @args
exit $LASTEXITCODE
