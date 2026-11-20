# Configuration

Every key of `config.json` and `notifications.json`, with its type, default and meaning. The rules these values feed
are in [design.md](design.md); the HTTP routes are in [api.md](api.md).

## config.json

- Location: `config.json` in the data directory (`%LOCALAPPDATA%\bluetti-watcher`), or the file `--config` names;
  `--data-dir` moves the directory it is looked up in.
- Order: built-in defaults, then the file, then the command-line flags. A key left out keeps its default.
- Keys are camelCase and case-sensitive: `MAC` is ignored, only `mac` is read (the installer refuses a wrong-case
  `mac` or `wakeTaskName`). `//` comments are allowed. Backslashes in strings are written `\\`.
- A change takes effect at the next start of the service:
  `Stop-ScheduledTask BluettiWatcher; Start-ScheduledTask BluettiWatcher`.
- A file that does not parse (bad JSON, a wrong type, an unknown `readingSource`) stops the start: the exe prints
  `COULD NOT RUN: <why>` and exits 2.

### Example

The minimum is the station's address:

```json
{ "mac": "AA:BB:CC:DD:EE:FF" }
```

Without `mac` the service starts, serves the page and reports the missing key in `/api/health` `problem`, and polls
nothing (unless the cloud alone reads the station, see [Cloud fallback](#cloud-fallback)).

A fuller file. Every value shown is the default except `mac`, `capacityWh`, `actions`, `modes.commands` and the
`dtekWatcher` values, which are examples:

```json
{
  "mac": "AA:BB:CC:DD:EE:FF",
  "type": "AC200PL",
  "capacityWh": 2048,
  "efficiency": 0.85,

  "pollSec": 60,
  "fastPollSec": 60,
  "fastBelow": 25,
  "readerTimeoutSec": 30,
  "stationTempHighC": 60,

  "emergencyAt": 20,
  "stopAt": 10,
  "warnAt": 7,
  "hibernateAt": 5,

  "powerModeTimeoutSec": 60,
  "actionTimeoutSec": 30,
  "actions": {
    "grid-voltage-high": "cmd /c echo grid high >> C:\\Temp\\grid.log"
  },

  "modes": {
    "engine": "v2",
    "commands": {
      "backup-pause":  { "exe": "C:\\Program Files\\Backup\\backup.exe", "args": ["pause"],  "timeoutSec": 30 },
      "backup-resume": { "exe": "C:\\Program Files\\Backup\\backup.exe", "args": ["resume"], "timeoutSec": 30 }
    }
  },

  "wakeEveryMinutes": 0,

  "readingSource": "auto",
  "port": 18940,
  "tray": true,

  "dtekWatcher": {
    "enabled": true,
    "queues": ["GPV1.1"],
    "group": "GPV1.1"
  }
}
```

### Station

| Key | Type | Default | Meaning |
|---|---|---|---|
| `mac` | string | none | The station's Bluetooth address, `AA:BB:CC:DD:EE:FF` (the BLUETTI app or Windows Bluetooth settings show it). Not needed when the cloud alone reads the station. An address that does not parse is reported in `/api/health` `problem`. |
| `type` | string | `AC200PL` | The model as BLUETTI names it; also matched against the cloud's device model. Bluetooth reads the 29 models listed in [bluetti-protocol.md](bluetti-protocol.md). Any other name is read through the cloud alone while the cloud is on; otherwise it is reported in `/api/health` `problem` and nothing is polled. |
| `capacityWh` | int | 2304 | Battery capacity, Wh, for the battery-time formula. `install.ps1 -CapacityWh` writes it into a new config. |
| `efficiency` | number | 0.85 | Inverter efficiency for the battery-time formula until a learned value exists. |

### Polling and readings

| Key | Type | Default | Meaning |
|---|---|---|---|
| `pollSec` | int | 60 | Seconds between readings. |
| `fastPollSec` | int | 60 | Seconds between readings while mains is off and the battery is under `fastBelow`. |
| `fastBelow` | number | 25 | Battery percent under which `fastPollSec` applies. |
| `readerTimeoutSec` | int | 30 | Time limit of one Bluetooth read (connect, every request, disconnect). |
| `stationTempHighC` | int | 60 | V2 stations: a station temperature above this, °C, writes a `station-hot` event and runs its action; re-armed 5 °C under it. |
| `maxGapSec` | int | 600 | A gap between accepted readings longer than this resets the decision state. |
| `socRatePerMin` | number | 5 | Largest plausible SOC change, points per minute. |
| `socJumpFloor` | number | 3 | The allowed SOC change is never under this many points. |
| `jumpWindowSec` | int | 120 | A reading accepted after a silence whose SOC change exceeds the rate over this many seconds counts as a rebase for hibernate. |
| `silentAfter` | int | 3 | Misses in a row before the station is reported silent. |

### Mains and battery levels

| Key | Type | Default | Meaning |
|---|---|---|---|
| `mainsLostSec` | int | 180 | No AC input this long: the cut begins. |
| `mainsBackSec` | int | 180 | AC input held this long: the cut ends. |
| `emergencyAt` | number | 20 | Percent: station emergency. |
| `stopAt` | number | 10 | Percent: stop (the flag says critical). |
| `warnAt` | number | 7 | Percent: hibernate warning. |
| `hibernateAt` | number | 5 | Percent: the PC hibernates. `emergencyAt`, `stopAt`, `warnAt` and `hibernateAt` are raised at a high measured drain (design.md, Battery levels and hibernate). |
| `agreeSec` | int | 90 | Two agreeing readings at least this far apart confirm a level. |
| `hibernateAfterWarnSec` | int | 180 | Hibernate waits at least this long after the warning. |
| `rebaseHibernateConfirmSec` | int | 600 | After a rebase in the cut, hibernate needs this long at or under `hibernateAt`, counted from the latest rebase. |
| `batteryRefreshSec` | int | 600 | On battery, the reading is written into the flag at least this often. |

### Battery time

| Key | Type | Default | Meaning |
|---|---|---|---|
| `rateWindowMin` | int | 60 | Minutes of readings off mains the measured discharge rate looks back over. |
| `rateMinDrop` | number | 3 | Least SOC drop, points, the measured rate needs; with less the efficiency formula stands. |
| `rateMinSpanMin` | int | 20 | Least span, minutes, the measured rate needs. |
| `fittedRuntime` | bool | true | Battery time from a loss model fitted to this station's own discharges, with a P10-P90 range. `false` = the measured rate and the efficiency formula only. |

### Power mode and actions

| Key | Type | Default | Meaning |
|---|---|---|---|
| `powerMode.script` | string | not set | Not set: the switch runs inside the service (`BluettiWatcher.exe power-mode`). Set: this `power-mode.ps1` runs through `pwshPath` instead. |
| `powerMode.flagPath` | string | `%ProgramData%\BluettiWatcher\power-mode.json` | The flag file (`POWER_MODE_FILE`). |
| `powerMode.mutex` | string | `Local\BluettiWatcherPowerMode` | The named mutex guarding the flag (`POWER_MODE_MUTEX`). A flag path set while this is left at its default is logged at start and written as a `config-warning` event: set both or neither. |
| `powerMode.taskPrefix` | string | `BluettiWatcherEco` | Prefix of the switch's scheduled tasks (`<prefix>End`, `<prefix>GpuCap`, `<prefix>GpuRestore`; `POWER_MODE_TASK_PREFIX`). |
| `powerMode.extraReaders` | string[] | `[]` | Local accounts allowed to read the flag folder besides the user, SYSTEM and Administrators (`POWER_MODE_EXTRA_READERS`, comma-joined). Each name: ASCII letters, digits, `_ . -` and space, 1-64 characters. One invalid name = none passed, and `/api/health` `problem` names it. |
| `flagPath` | string | not set | Alias of `powerMode.flagPath`; `powerMode.flagPath` wins when both are set. |
| `pwshPath` | string | `pwsh.exe` | PowerShell 7, used for the wake-task commands and a configured `powerMode.script`. |
| `powerModeTimeoutSec` | int | 60 | Clamped to 1-600. Time limit of each power-mode command and wake-task command; a command over it is killed and counts as failed. |
| `actionTimeoutSec` | int | 30 | Clamped to 1-600. Time limit of each `actions` entry. |
| `actions` | object | none | Commands run on a state or alert, below. |
| `dryRun` | bool | false | Log every power-mode command, action and hibernation instead of running it (also `--dry-run`). |
| `modes.engine` | string | `v2` | `v2`: user-defined power modes with Auto rules, managed on the page or through the [API](api.md#power-modes-modesengine--v2); they are stored in `modes.json`, not here. `legacy`: the fixed eco and the page's Auto / Eco / Max. Any other value = `legacy`, named in the log. |
| `modes.commands` | object | `{}` | Named commands the `command.pair` action may run: `{ "<name>": { "exe", "args", "timeoutSec" } }`. Only this file defines them; a mode, the page and the API pick a name, never an executable or an argument. |
| `modes.commands.<name>.exe` | string | `""` | The executable. An entry with an empty `exe` is ignored. |
| `modes.commands.<name>.args` | string[] | `[]` | Its arguments, passed as a list (no shell). |
| `modes.commands.<name>.timeoutSec` | int | 30 | Clamped to 1-600 when run; the command is killed past it. A non-zero exit is a failed action. |
| `modes.openRgbPath` | string | `""` | `OpenRGB.exe` for the `rgb.lighting` action. Empty = `Program Files\OpenRGB` or `%LOCALAPPDATA%\Programs\OpenRGB`. |

`actions` keys:

| Key | Runs when |
|---|---|
| `eco` | The cut begins (eco on), or the page's Eco is applied. `legacy` engine only, see below. |
| `normal` | Mains is back, or the page's Max or an Auto that lifts the page's Eco is applied. `legacy` engine only. |
| `emergency` | The battery reaches `emergencyAt`. |
| `stop` | The battery reaches `stopAt`. |
| `hibernate-warn` | The hibernate warning is given. |
| `grid-voltage-high` | The grid input rises above 253 V; again only after it fell under 250 V. |
| `station-warning`, `station-fault`, `station-alarm-cleared`, `station-hot` | V2 stations: a station warning or fault appears, they clear, or a temperature passes `stationTempHighC`. |

Each value is one command line: an executable and its arguments, started directly with no shell (a pipe, redirect or
shell built-in needs `cmd /c ...`), windowless, within `actionTimeoutSec`. It gets the environment `POWER_MODE_FILE`,
`POWER_MODE_MUTEX`, `POWER_MODE_TASK_PREFIX` and `POWER_MODE_EXTRA_READERS`. Its exit code and the first 300
characters of its output are written as an `action` or `action-failed` event; a missing, failing or hanging command
never changes a decision. A battery-state command runs once per entry into that state per cut.

Under `modes.engine` = `v2`, `actions.eco` and `actions.normal` do not run on their own. They become the named commands
`eco` and `normal` (split by the Windows argument rules, with `actionTimeoutSec` as their limit), unless
`modes.commands` already defines those names; the built-in Eco mode's command pair runs them on entering and leaving
Eco.

### Wake during a cut

See [design.md](design.md#8-wake-during-a-cut).

| Key | Type | Default | Meaning |
|---|---|---|---|
| `wakeEveryMinutes` | int | 0 | A hibernation during a cut arms the wake task this many minutes ahead; clamped to 10-1440. 0 = off. |
| `wakeReadSec` | int | 300 | After a timer wake with no accepted reading for this long, the PC hibernates again; clamped to 60-3600 (0 or less = 300). |
| `wakeFloorPercent` | number or null | null | At or under this battery percent no wake is armed. null = `hibernateAt` - 5, at least 0; kept within 0..`hibernateAt`; 0 = no floor. |
| `wakeTaskName` | string | `BluettiWatcherWake` | The wake task `install.ps1` registers. |

### Cloud fallback

See [design.md](design.md#4-cloud-fallback).

| Key | Type | Default | Meaning |
|---|---|---|---|
| `readingSource` | string or null | null | `auto`: Bluetooth first, the cloud when it misses · `bluetooth`: never the cloud · `cloud`: never Bluetooth (no `mac` needed). null = `auto`, or `bluetooth` when `cloudEnabled` is `false`. A choice saved on the page (`reading-source.json`) wins over this key until it is cleared. |
| `cloudEnabled` | bool or null | null | Read under the `auto` source only: null = on while the token file exists · `true` on · `false` off. |
| `cloudTokenPath` | string or null | null | The token file; null = `cloud-token.json` in the data directory. Written by the page's sign-in or `--cloud-login`. |
| `staleLoadW` | number | 100 | A cloud reading off mains at or above this AC load, W, whose SOC has not moved for `staleAfterMin` is stale (a miss). |
| `staleAfterMin` | int | 30 | Minutes for that check; 0 or less = no check. |
| `cloudHistory` | bool or null | null | Gaps in the stored history filled from the cloud's 5-minute history. null or `true` = on while the source allows the cloud and the token file exists · `false` = off (also no history stand-in reading). |
| `cloudHistoryMaxAgeMin` | int | 10 | When the cloud has no live state, the cloud history's newest point counts as the reading while it is at most this many minutes old; 0 or less = never. |
| `cloudLiveMaxAgeSec` | int | 120 | The cloud's live state counts as a reading while its timestamp is at most this many seconds off the clock; 0 or less = never a live reading. |

### Service

| Key | Type | Default | Meaning |
|---|---|---|---|
| `port` | int | 18940 | HTTP port on 127.0.0.1 (also `--port`). |
| `lockWaitSec` | int | 30 | Clamped to 0-600. A start waits this long for an older instance to release the port's lock; past it the exe exits 2. |
| `dataDir` | string | `%LOCALAPPDATA%\bluetti-watcher` | Database, log and state files. `--data-dir` sets it too, and also moves where `config.json` is looked up; this key does not. |
| `rawRetentionDays` | int | 30 | Raw readings are kept this many days, then folded into 5-minute aggregates. |
| `tray` | bool | true | The notification-area icon and the Windows notifications; shown only in an interactive session. `false` or `--no-tray` = neither. Which notifications show: [notifications.json](#notifications-notificationsjson). |
| `trimWorkingSet` | bool | true | After each poll the process's pages leave its working set (Task Manager "Memory" drops; commit does not change). `false` keeps them. |

### DTEK module (`dtekWatcher`)

Off by default; while off it makes no request. `"dtekWatcher": null` = off. A value out of range turns the module off
with the reason on the page; the service runs on. See
[design.md](design.md#11-dtek-module-planned-outages-kyiv-region).

| Key | Type | Default | Meaning |
|---|---|---|---|
| `enabled` | bool | false | Turns the module on. |
| `datasetUrl` | string | `https://raw.githubusercontent.com/Baskerville42/outage-data-ua/main/data/kyiv-region.json` | The primary schedule source; plain https on the default port, no user info. |
| `staleAfterMin` | int | 90 | The schedule is stale when its newest collection is older than this. |
| `minCutMin` | int | 15 | 0-1440. A cut shorter than this many minutes counts in no DTEK statistic (the usual lateness, the queue search, the manual queue's hint); it stays in the chart and the cut history. 0 = every cut counts. |
| `queues` | string[] | null | The house's queues, such as `GPV1.1`. Without them (and without `site`) the module cannot tell which queue is yours. |
| `group` | string, string[] or null | null | The queue to follow, a hint real cuts can refute. An array = a set whose planned outage is the union: at most 4 queues. |
| `site` | bool | false | Opt-in. `true` = the distributor's website is also read the way a browser does: plan edits arrive sooner, and with `city`, `street` and `house` it names the house's queues and current outage. Use of the site is subject to its own terms. |
| `host` | string | `www.dtek-krem.com.ua` | The site's host; with the dataset's host the only hosts a request may go to. Must be a plain host name. |
| `city`, `street`, `house` | string | `""` | Optional address, sent to the site only. |

### Ignored keys

`bleBackend` and `readerPath` (and the flag `--reader`) are accepted and ignored; `service.log` names them at start.

## Notifications (`notifications.json`)

A file of its own in the data directory, written by the service on `PUT /api/settings/notifications`
([api.md](api.md#get-apisettingsnotifications-put-apisettingsnotifications)); a change applies to the next notification,
no restart. Missing file = every group on. An unreadable file, an unknown key or a non-boolean value: `service.log`
names it and every group stays on until the next change rewrites the file.

| Key | Type | Default | Notifications |
|---|---|---|---|
| `mains` | bool | true | Mains lost, mains back (the confirmed cut). |
| `modeChanges` | bool | true | Modes the service changed itself: eco on, modes lifted when mains is back, a mode the Auto rules put in force (`v2`), a page choice dropped. |
| `lowBattery` | bool | true | Battery emergency, stop, the hibernate warning. |
| `failures` | bool | true | A power-mode command or action that failed, a mode choice refused, an admin broker older than the service. |
| `faults` | bool | true | Station faults and warnings, the grid voltage alert. |
| `overTemperature` | bool | true | A station temperature above `stationTempHighC` (V2 stations). |

```json
{ "mains": true, "modeChanges": false, "lowBattery": true, "failures": true, "faults": true, "overTemperature": true }
```

## Other files in the data directory

Not configuration files; listed so they are not edited by hand by mistake.

| File | Written by | Meaning |
|---|---|---|
| `modes.json` | the power-modes API | Modes, Auto rules, selection, `autoHibernate`. Change it through the [API](api.md#power-modes-modesengine--v2). |
| `modes-journal.jsonl` | the power-modes engine | What each mode wrote and what it must restore. |
| `reading-source.json` | `POST /api/reading-source` | The page's reading-source choice. |
| `cloud-token.json` | the cloud sign-in | The BLUETTI cloud token (default `cloudTokenPath`). |
| `no-hibernate` | you | An empty file: no hibernation. `legacy` engine: read on every poll; `v2`: carried into `autoHibernate` false at start and renamed `no-hibernate.migrated`. |
| `station.db`, `service.log` | the service | Readings, events and state; the log. |

`power-mode.config.json` beside the flag file belongs to the power-mode switch
([power-mode/README.md](../power-mode/README.md#configuration)).

## Command line

```text
BluettiWatcher.exe [--config <file>] [--data-dir <dir>] [--port <n>] [--flag <file>] [--power-mode <script>] [--dry-run] [--no-tray]
BluettiWatcher.exe --cloud-login [--data-dir <dir>]
BluettiWatcher.exe power-mode <args>
BluettiWatcher.exe hook
BluettiWatcher.exe --broker
BluettiWatcher.exe --broker-allow service|task|profile|inspector <name>
BluettiWatcher.exe --help | -h | /? | --version
```

| Flag | Sets |
|---|---|
| `--config <file>` | The config file. |
| `--data-dir <dir>` | `dataDir`, and the directory `config.json` is read from. |
| `--port <n>` | `port`. |
| `--flag <file>` | `powerMode.flagPath`. |
| `--power-mode <script>` | `powerMode.script`. |
| `--dry-run` | `dryRun` = true. |
| `--no-tray` | `tray` = false. |
| `--cloud-login` | Signs in to the BLUETTI cloud in the browser, writes the token file and exits. |

- `power-mode <args>` runs the power-mode switch ([power-mode/README.md](../power-mode/README.md)); `hook` is the
  Claude Code hook ([integrations/claude-code/README.md](../integrations/claude-code/README.md)); `--broker` is the
  admin broker's run (started by the SYSTEM task `BluettiWatcherBroker`); `--broker-allow` adds a target to the broker
  policy and needs an elevated shell.
- `--help` and `--version` print and exit 0, and win over every other word on the line.
- A flag that takes a value refuses a missing value or one that starts with `--`; an unknown flag or word exits 2.
- Exit codes: 0 stopped, printed or token saved · 1 the cloud login failed · 2 could not run (bad arguments, a config
  that does not parse, another instance past `lockWaitSec`, the port taken) · 3 Windows' SQLite missing or older than
  3.24.0.
- The exe has no console of its own: it prints into the terminal it was started from. PowerShell does not wait for it,
  so pipe it to read the exit code: `& .\BluettiWatcher.exe --help | Out-Host`.

## Environment of the power-mode scripts

`power-mode/*.ps1` read `POWER_MODE_FILE`, `POWER_MODE_MUTEX`, `POWER_MODE_TASK_PREFIX` and
`POWER_MODE_EXTRA_READERS`, with the defaults of `powerMode` above (no extra readers). The service sets all four for
every child it starts; set them yourself when running the scripts by hand with non-default names.
