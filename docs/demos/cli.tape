# The command line of BluettiWatcher.exe: help, the power-mode status of a demo flag file, version.
# Read-only: no command here switches a power plan or touches the live flag (status reads a demo flag in %TEMP%).
# The exe is a windowless app, so pwsh does not wait for it: the hidden setup pipes it to Out-Host, which waits.
# Run from the repo root with the exe installed (install.ps1): vhs docs/demos/cli.tape (needs vhs, ttyd, ffmpeg on PATH).

Output docs/demos/cli.gif
Output docs/demos/cli.mp4

Set Shell "pwsh"
Set FontFamily "Cascadia Mono"
Set FontSize 17
Set Width 1060
Set Height 600
Set Padding 24
Set Theme { "name": "station", "background": "#111214", "foreground": "#e4e6ea", "cursor": "#548af7", "selection": "#2c2e33", "black": "#26282c", "red": "#db5c5c", "green": "#5fad65", "yellow": "#e0a53a", "blue": "#548af7", "magenta": "#b48ead", "cyan": "#6cb6c9", "white": "#e4e6ea", "brightBlack": "#8b8e95", "brightRed": "#db5c5c", "brightGreen": "#5fad65", "brightYellow": "#e0a53a", "brightBlue": "#548af7", "brightMagenta": "#b48ead", "brightCyan": "#6cb6c9", "brightWhite": "#ffffff" }
Set TypingSpeed 45ms

Hide
Type `$bw = "$env:LOCALAPPDATA\bluetti-watcher\bin\BluettiWatcher.exe"; function BluettiWatcher.exe { & $bw @args | Out-Host }; $demo = Join-Path $env:TEMP 'bw-cli-demo'; $null = New-Item -ItemType Directory -Force $demo; Set-Content (Join-Path $demo 'power-mode.json') '{ "mode": "eco", "since": "2026-10-20T18:33:00+03:00", "source": "station", "prevPlan": "381b4222-f694-41f0-9685-ff5bb260df2e" }'; Set-Location $demo; function prompt { 'PS> ' }; Clear-Host`
Enter
Sleep 1.5s
# The help is typed off screen, so the first frame (the still view of the README) is a full terminal.
Type "BluettiWatcher.exe --help"
Enter
Sleep 1.5s
Show

Sleep 6s
Type "Clear-Host"
Enter
Sleep 300ms
Type "BluettiWatcher.exe power-mode status -Path .\power-mode.json"
Sleep 300ms
Enter
Sleep 3s

Type "BluettiWatcher.exe --version"
Sleep 300ms
Enter
Sleep 3s
