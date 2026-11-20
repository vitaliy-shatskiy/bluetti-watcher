# power-mode: the power-mode switch and its flag

The switch sets **normal**, **eco** (save electricity, keep working) or **emergency** (save electricity, wind down) and
keeps the mode in one flag file that other programs read (for example the hook in
[`integrations/claude-code/`](../integrations/claude-code/README.md)). The switch is `BluettiWatcher.exe power-mode`;
the scripts here forward to it or read the flag.

| File | What it is |
|---|---|
| `power-mode.ps1` | Forwards its arguments to `BluettiWatcher.exe power-mode` and passes back its lines and exit code. Windows PowerShell 5.1 or PowerShell 7. The exe is `BLUETTI_WATCHER_EXE`, else `..\bin\BluettiWatcher.exe` (the install layout); none = exit 2. |
| `power-mode-flag.ps1` | The PowerShell flag library for your own scripts. Dot-source it, then `Read-PowerModeFlag (Get-PowerModeFlagPath)`: a missing file reads normal, `$null` = the file cannot be trusted. |
| `power-mode-end.ps1` | Forwards to `power-mode end`. Only eco end tasks registered by an old install run it; newer tasks run the exe. |

The installer puts them at `<data dir>\power-mode\`.

## How the service uses it

- With `modes.engine` = `v2` (the default) the modes engine owns the machine: plan, GPU limit and every other action
  of a mode ([design.md](../docs/design.md), section 12). It writes only the active mode's flag word (`normal`, `eco`
  or `emergency`) through the switch's flag code, so readers of the flag see the same file. The station's eco and
  mains-back run no switch command; the station emergency still runs `on`.
- With `modes.engine` = `legacy` the service runs `eco -Source station` when mains goes off, `off -Source station`
  when it is back, and `on -Source station` at the emergency level.
- On battery the service writes its reading with `battery` at each level change.
- The service runs the switch inside its own process. A configured `powerMode.script` makes it run that script through
  `pwshPath` instead ([configuration.md](../docs/configuration.md#power-mode-and-actions)).

## Commands

```powershell
BluettiWatcher.exe power-mode <command> [options] | Write-Output
powershell -NoProfile -File power-mode.ps1 <command> [options]
```

The exe is a windowless app: PowerShell waits for it and sets `$LASTEXITCODE` only when its output is piped.

| Command | What it does |
|---|---|
| `status` | One line, `power mode: normal`, `power mode: EMERGENCY since ...` or `power mode: ECO since ... until ...`, then the plan it returns to, the end task, the GPU cap and the last battery reading when there are any. |
| `on` | Emergency on. Already on: `since` kept. |
| `off` | Normal: the power plan and GPU limit eco changed are put back, the end task removed. Already off: `since` kept. |
| `eco [-For <minutes> \| -Until HH:mm]` | Eco for 240 minutes, or `-For` 1-1440 minutes, or until the next `HH:mm` of the PC's clock (`24:00` = midnight). Activates Power saver, caps the GPU (below) and registers a one-shot task that ends eco then. Refused while emergency is on. |
| `eco -Source station` | Eco with no end time, until `off`. An eco already on is kept; only a step it left unapplied is repaired. |
| `eco -Adopt -ReturnPlan balanced\|high\|<GUID>` | Gives an eco that is on and has no plan to return to one, and its end task. |
| `end` | Ends an eco whose end time has passed (what the end task runs). |
| `battery -Soc <0-100> -Level ok\|low\|critical\|hibernating -Mains on\|off [-HibernateAt <percent>]` | Writes the station reading into the flag; nothing else changes. |
| `prev-plan balanced\|high\|<GUID>` | Rewrites the plan an eco that is on returns to. |
| `gpu-task install [-RestoreWatts <W>]` / `remove` / `status` | The two elevated GPU tasks (below). |

Options for every command: `-Path <flag file>` uses another flag file (its folder must exist; tests), `-Source
manual|station` records who switched.

Exit codes:

| Code | Meaning |
|---|---|
| 0 | Done, or already so. `status`: normal. `gpu-task status`: both tasks registered. |
| 1 | `status`: eco or emergency. `gpu-task install`: a task was not registered as written. `gpu-task status`: a task is missing. `prev-plan`: no eco is on. |
| 2 | Could not run: bad arguments, a flag that cannot be trusted, a failed write, no elevation where needed. A corrupt flag never reads as normal. |

## Configuration

Environment variables, read by the switch and `power-mode-flag.ps1`. The service sets all four from its `powerMode`
section for every power-mode child it starts ([configuration.md](../docs/configuration.md#power-mode-and-actions));
set them yourself when you run the scripts by hand with non-default names. The eco end task gets them on its command
line. An invalid value stops the command with exit 2.

| Variable | Default | Names |
|---|---|---|
| `POWER_MODE_FILE` | `%ProgramData%\BluettiWatcher\power-mode.json` | The flag file. |
| `POWER_MODE_MUTEX` | `Local\BluettiWatcherPowerMode` | The named mutex every writer holds. `Local\` or `Global\` plus 1-100 letters, digits, `_ . -`. |
| `POWER_MODE_TASK_PREFIX` | `BluettiWatcherEco` | The tasks `<prefix>End`, `<prefix>GpuCap`, `<prefix>GpuRestore`. 1-100 letters, digits, `_ . -`. |
| `POWER_MODE_EXTRA_READERS` | none | Comma list of local accounts that may read, never write, the flag folder. Each 1-64 letters, digits, spaces, `_ . -`; no domain part. |

`power-mode.config.json` beside the flag file (optional):

```json
{ "returnPlan": "high", "gpuCap": true }
```

| Key | Default | Meaning |
|---|---|---|
| `returnPlan` | `high` | The plan every end of eco sets: `high` = High performance; `balanced` = Balanced; `previous` = the plan active before eco (High performance when that is unknown, Power saver or gone); or a plan GUID (`powercfg /list`). A plan not on this machine = Balanced, with a `warn:` line. |
| `gpuCap` | `true` | `false` = eco never touches the GPU. |

A missing file or key = the default; one that cannot be read = the default plus a `warn:` line.

## The flag file

One line of JSON, UTF-8 without BOM, written to a temp file and moved over the flag under the mutex:

```json
{ "mode": "eco", "since": "2026-10-13T20:08:00+03:00", "until": "2026-10-14T00:08:00+03:00", "prevPlan": "381b4222-f694-41f0-9685-ff5bb260df2e", "prevGpuLimitW": 250, "gpuCapW": 100 }
```

| Field | Meaning |
|---|---|
| `mode` | `normal`, `eco` or `emergency`. |
| `since` | When the mode began, ISO 8601 with offset. |
| `until` | Eco only: its end. Past it every reader sees normal, whether or not the end task ran. A station eco has none. |
| `source` | `station` when the service set the mode; absent = by hand. |
| `by` | Who wrote a normal flag: `manual` or `station`. |
| `prevPlan` | The power plan eco owes back. |
| `prevGpuLimitW`, `gpuCapW` | The GPU limit eco owes back and the cap it set, in watts. |
| `replaced` | `{ mode, since, until }` of a manual eco a station emergency replaced. |
| `battery` | `{ soc, at, level, mains, hibernateAt }`, the last station reading; `level` is `ok`, `low`, `critical` or `hibernating`. |

A missing file reads normal.

### Folder ACL

The configured flag's folder is created on the first write with its own ACL: the user Modify, SYSTEM and
Administrators Full control, nothing inherited. A folder with any other entry (beyond the read of
`POWER_MODE_EXTRA_READERS`), with inheritance on, or a junction in its place refuses every write and reads as
untrusted; fix it by hand. One exception: a folder that only inherits its ACL gets the folder's own ACL when the user
owns it or it is empty. A reader running as another account (a service, SYSTEM) therefore reads the flag as
untrusted: run readers as the user.

## Power plan and GPU

Eco records the active Windows power plan and activates the built-in Power saver plan; every end of eco (`off`, the
end task) sets the plan `returnPlan` names. A plan that cannot be set stays owed in the flag for the next `off`.

The GPU cap is optional. With an NVIDIA card and `nvidia-smi` in System32, eco records the power limit and sets the
card's minimum; the end of eco puts the recorded limit back. No card or no `nvidia-smi` = a `note:` line and eco
without the cap.

Only the GPU limit needs administrator rights. An elevated shell runs `nvidia-smi -pl` itself. For a shell without
elevation (and the service), run once in an elevated shell:

```powershell
powershell -NoProfile -File power-mode.ps1 gpu-task install      # -RestoreWatts <W>: restore to this limit, not the card's default
```

It registers two on-demand tasks, `<prefix>GpuCap` (`nvidia-smi -pl <card minimum>`) and `<prefix>GpuRestore`
(`nvidia-smi -pl <default or -RestoreWatts>`): System32 `nvidia-smi` under `conhost.exe --headless`, as the user, at
the highest run level, the wattage fixed at install. Nothing a process without elevation writes reaches what runs
elevated. `gpu-task status` lists them; `gpu-task remove` (elevated) deletes them. Registering the admin broker
(`install.ps1 -Apply` or `-Broker -Apply`, elevated; [design.md](../docs/design.md), section 13) removes both tasks:
the modes engine sets the GPU limit through the broker instead.

## Tests

From the repository root, with PowerShell 7 (`pwsh`) on PATH:

```powershell
dotnet test --solution bluetti-watcher.slnx -c Release --filter "FullyQualifiedName~BluettiWatcher.Tests.PowerModeScript"
$env:PM_SWITCH_ENTRY = 'script'; dotnet test --solution bluetti-watcher.slnx -c Release --filter "FullyQualifiedName~BluettiWatcher.Tests.PowerModeScript"
```

The switch under test is the exe built beside the tests, or `BLUETTI_WATCHER_EXE`; `PM_SWITCH_ENTRY=script` runs every
call through `power-mode.ps1`. The tests never touch the real flag, plan, Task Scheduler or GPU: every flag lives in a
temp folder, and `powercfg`, `schtasks` and `nvidia-smi` are replaced by stand-in scripts through
`POWER_MODE_POWERCFG`, `POWER_MODE_SCHTASKS` and `POWER_MODE_NVIDIASMI`. With `POWER_MODE_TEST=1` a call that names no
stand-in exits 2 instead of starting the real tool. `gpu-task install` and `remove` refuse to run while any `POWER_MODE_*` variable other than the four above is set.
