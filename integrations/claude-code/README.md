# Claude Code hook: power mode

`BluettiWatcher.exe hook` tells every running Claude Code session about the power mode Bluetti Watcher (or you, with
[`power-mode.ps1`](../../power-mode/README.md)) switched, on the session's next prompt. A rule in `CLAUDE.md` is read
once at session start; this hook reads the flag file on every event, so a power cut reaches sessions that are already
running. It is part of the watcher's exe: no Node.js, no extra files.

What a session hears:

| Flag | The session gets |
|---|---|
| normal | nothing; a session that heard a mode gets one line that its rules are lifted |
| eco | `power mode: ECO since … until …` and `power-mode.eco.rules.md`: no local models, no GPU work, no benchmarks; everything else as usual; commit often |
| emergency | `power mode: EMERGENCY since …` and `power-mode.rules.md`: finish the current step, start nothing heavy |
| battery `low` (mains off) | the emergency rules |
| battery `critical` (mains off) | `power mode: STOP` and `power-mode.stop.rules.md`: commit, push what may be pushed, write the state, end the turn |
| battery `hibernating` (mains off) | `the PC hibernates at N %: save now` and `power-mode.hibernate.rules.md` |
| a flag that cannot be read | one line at session start naming the file (treated as normal) |

Each session hears each change once (state per session under `%TEMP%\claude-power-mode\`). A battery reading older
than 15 minutes is ignored. Times print in the PC's time zone. The four `*.rules.md` files in this folder are built
into the exe: to change what your sessions are told, edit them and publish the exe again (`tools/publish.ps1`).

## Install

Install the watcher (`install.ps1`), then add the hook to `~/.claude/settings.json` (or a project's
`.claude/settings.json`). The exe is at `%LOCALAPPDATA%\bluetti-watcher\bin\BluettiWatcher.exe`; replace the path
with yours:

```json
{
  "hooks": {
    "SessionStart": [
      { "hooks": [ { "type": "command", "command": "\"C:/Users/you/AppData/Local/bluetti-watcher/bin/BluettiWatcher.exe\" hook" } ] }
    ],
    "UserPromptSubmit": [
      { "hooks": [ { "type": "command", "command": "\"C:/Users/you/AppData/Local/bluetti-watcher/bin/BluettiWatcher.exe\" hook" } ] }
    ],
    "SubagentStart": [
      { "hooks": [ { "type": "command", "command": "\"C:/Users/you/AppData/Local/bluetti-watcher/bin/BluettiWatcher.exe\" hook" } ] }
    ]
  }
}
```

Coming from 1.0 (`node …/power-mode.js`): replace each of those three commands with the line above; the flag, the
state files and the texts are the same.

The hook reads the flag at `%ProgramData%\BluettiWatcher\power-mode.json`. If you moved the flag (the watcher's
`powerMode.flagPath`, the scripts' `POWER_MODE_FILE`), set the same path for Claude Code, for example in the `env`
block of `settings.json`:

```json
{ "env": { "POWER_MODE_FILE": "D:\\power\\power-mode.json" } }
```

The hook never writes the flag. A junction or link in place of the flag's folder or file, or a folder it cannot list,
reads as unreadable. It fails open: any error = no output, exit 0, so a broken hook never blocks a prompt.

## Test

From the repository root (the tests that set folder permissions or run the switch also need `pwsh`):

```powershell
dotnet test tests/BluettiWatcher.Tests --filter "FullyQualifiedName~BluettiWatcher.Tests.Hook"
```

Every test runs the hook's entry with a hook event's JSON against a flag and a `%TEMP%` of its own. To try the exe by
hand:

```powershell
$env:POWER_MODE_FILE = "$env:TEMP\pm-try\power-mode.json"; New-Item -ItemType Directory -Force (Split-Path $env:POWER_MODE_FILE) | Out-Null
'{ "mode": "emergency", "since": "2026-10-13T20:00:00+03:00" }' | Set-Content -NoNewline $env:POWER_MODE_FILE
'{ "hook_event_name": "SessionStart", "session_id": "try" }' | & "$env:LOCALAPPDATA\bluetti-watcher\bin\BluettiWatcher.exe" hook
```
