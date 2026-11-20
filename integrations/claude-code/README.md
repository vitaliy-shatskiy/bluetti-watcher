# Claude Code hook: power mode

`BluettiWatcher.exe hook` tells every running Claude Code session the power mode on its next prompt. It reads the
power-mode flag ([power-mode README](../../power-mode/README.md)) on every event, so a power cut reaches sessions that
are already running; a rule in `CLAUDE.md` is read only at session start. The hook is part of the exe: nothing else
to install.

| Flag | The session hears |
|---|---|
| normal | Nothing. A session that heard a mode gets one line: its rules are lifted. |
| eco | `power mode: ECO since ... until ...` (a station eco: `while mains is off`) and `power-mode.eco.rules.md`. |
| emergency | `power mode: EMERGENCY since ...` and `power-mode.rules.md`. |
| battery `low` | The emergency text and rules. |
| battery `critical` | `power mode: STOP - battery N %` and `power-mode.stop.rules.md`. |
| battery `hibernating` | `power mode: battery N % ... the PC hibernates at N %: save now` and `power-mode.hibernate.rules.md`. |
| a flag that cannot be read | At session start only: one line naming the file; treated as normal. |

- A battery level counts only while mains is not on and the reading is under 15 minutes old; it outranks the mode.
- Each session hears each change once. What it last heard is kept in `%TEMP%\claude-power-mode\<session>.json`;
  `SessionStart` removes those files after 14 days.
- `SubagentStart` gives a subagent the current text and rules, and keeps no state.
- Times print in the PC's time zone.
- The four `*.rules.md` files here are built into the exe: to change what sessions are told, edit them and publish
  the exe again ([CONTRIBUTING.md](../../CONTRIBUTING.md)).

## Install

Install the watcher, then add the hook to `~/.claude/settings.json` (or a project's `.claude/settings.json`). The exe
is `%LOCALAPPDATA%\bluetti-watcher\bin\BluettiWatcher.exe`; put your own path in:

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

The hook reads `POWER_MODE_FILE`, else `%ProgramData%\BluettiWatcher\power-mode.json`. If the flag lives elsewhere
(`powerMode.flagPath`), give Claude Code the same path, for example in `settings.json`:

```json
{ "env": { "POWER_MODE_FILE": "D:\\power\\power-mode.json" } }
```

The hook never writes the flag. A junction or link in place of the flag's folder or file, or a folder it cannot list,
reads as unreadable. It fails open: any error = no output and exit 0, so a broken hook never blocks a prompt.

## Test

```powershell
dotnet test --solution bluetti-watcher.slnx -c Release --filter "FullyQualifiedName~BluettiWatcher.Tests.Hook"
```

By hand, against a throwaway flag:

```powershell
$env:POWER_MODE_FILE = "$env:TEMP\pm-try\power-mode.json"; New-Item -ItemType Directory -Force (Split-Path $env:POWER_MODE_FILE) | Out-Null
'{ "mode": "emergency", "since": "2026-10-13T20:00:00+03:00" }' | Set-Content -NoNewline $env:POWER_MODE_FILE
'{ "hook_event_name": "SessionStart", "session_id": "try" }' | & "$env:LOCALAPPDATA\bluetti-watcher\bin\BluettiWatcher.exe" hook
```
