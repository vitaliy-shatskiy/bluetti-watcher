# Configuration

The service reads its defaults, then `config.json` in the data directory (`%LOCALAPPDATA%\bluetti-watcher` unless
`--data-dir` or `--config` says otherwise), then the command-line flags. Keys are camelCase and case-sensitive (`MAC`
is not `mac`; the installer refuses it too); comments (`//`) are allowed; a key left out keeps its default. A change takes effect at the next start of the service
(`Stop-ScheduledTask BluettiWatcher; Start-ScheduledTask BluettiWatcher`).

The rules these values feed are in [design.md](design.md).

## Minimal example

```json
{
  "mac": "AA:BB:CC:DD:EE:FF"
}
```

`mac` is the station's Bluetooth address (the BLUETTI app or Windows Bluetooth settings show it). Without it the
service starts, serves the page and `/api/health` with a problem naming the missing `mac`, and polls nothing.

## Full example

Every key with its default, except `mac`, `actions` and the DTEK address, which have none.

```json
{
  "mac": "AA:BB:CC:DD:EE:FF",
  "type": "AC200PL",
  "capacityWh": 2304,
  "efficiency": 0.85,

  "pollSec": 60,
  "fastPollSec": 60,
  "fastBelow": 25,
  "readerTimeoutSec": 30,

  "emergencyAt": 20,
  "stopAt": 10,
  "warnAt": 7,
  "hibernateAt": 5,
  "agreeSec": 90,
  "hibernateAfterWarnSec": 180,
  "rebaseHibernateConfirmSec": 600,
  "jumpWindowSec": 120,
  "mainsLostSec": 180,
  "mainsBackSec": 180,
  "maxGapSec": 600,
  "socRatePerMin": 5,
  "socJumpFloor": 3,
  "silentAfter": 3,
  "batteryRefreshSec": 600,

  "rateWindowMin": 60,
  "rateMinDrop": 3,
  "rateMinSpanMin": 20,
  "fittedRuntime": true,

  "powerMode": {
    "flagPath": "C:\\ProgramData\\BluettiWatcher\\power-mode.json",
    "mutex": "Local\\BluettiWatcherPowerMode",
    "taskPrefix": "BluettiWatcherEco",
    "extraReaders": []
  },
  "pwshPath": "pwsh.exe",
  "powerModeTimeoutSec": 60,
  "actionTimeoutSec": 30,
  "actions": {
    "eco": "pwsh -NoProfile -File C:\\scripts\\on-eco.ps1",
    "normal": "pwsh -NoProfile -File C:\\scripts\\on-normal.ps1"
  },

  "wakeEveryMinutes": 0,
  "wakeReadSec": 300,
  "wakeFloorPercent": null,
  "wakeTaskName": "BluettiWatcherWake",

  "readingSource": null,
  "cloudEnabled": null,
  "cloudTokenPath": null,
  "staleLoadW": 100,
  "staleAfterMin": 30,
  "cloudHistory": null,
  "cloudHistoryMaxAgeMin": 10,
  "cloudLiveMaxAgeSec": 120,

  "port": 18940,
  "lockWaitSec": 30,
  "dataDir": "%LOCALAPPDATA%\\bluetti-watcher",
  "rawRetentionDays": 30,
  "dryRun": false,
  "trimWorkingSet": true,

  "dtekWatcher": {
    "enabled": false,
    "queues": ["GPV1.1", "GPV2.1"],
    "group": "GPV1.1",
    "datasetUrl": "https://raw.githubusercontent.com/Baskerville42/outage-data-ua/main/data/kyiv-region.json",
    "staleAfterMin": 90,
    "minCutMin": 15,
    "site": false,
    "host": "www.dtek-krem.com.ua",
    "city": "",
    "street": "",
    "house": ""
  }
}
```

`<exe dir>` and `%LOCALAPPDATA%` above stand for the real paths: write an absolute path in a real file, or leave the
key out for its default. JSON strings need `\\` for each backslash.

## Station

| Key | Type | Default | Meaning |
|---|---|---|---|
| `mac` | string | none (required) | The station's Bluetooth address, `AA:BB:CC:DD:EE:FF`. A model only the cloud reads (below) needs none. |
| `type` | string | `AC200PL` | The model, as BLUETTI names it; also matched against the cloud's device model. Bluetooth reads 29 models: the IoT V1 models AC200L, AC200M, AC200PL, AC300, AC500, EB3A, EP500, EP500P (the AC300 without its battery packs) and the IoT V2 models AC2A, AC2P, AC50B, AC60, AC60P, AC70, AC70P, AC180, AC180T, AC180P, AP300, EL10, EL30V2, EL100V2, EP600, EP760, EP800, EP2000, Handsfree 1, PR30V2, PR100V2 (the EP600, EP760, EP800 and EP2000 report the battery level only, no mains state: they are a missed reading over Bluetooth, and the cloud fallback answers); read only: Modbus function 0x03 under register 2000. A model with no output-mode register (every V2 model and the EB3A) gets the mode derived from the AC powers, as the cloud reading does: AC input above 0 W = `BLE_MAINS`, 0 W with AC output above 0 W = `INVERTER_OUTPUT`. A station that encrypts its Bluetooth link is a missed reading, not connected to again until the service restarts, and the cloud fallback answers. Any other model (an unknown name) is read through the cloud fallback alone when it is on; without it, it is reported in `/api/health` `problem` and nothing is polled, as is a `mac` that is not a Bluetooth address. |

Since 1.1.0 the Python reader's keys `bleBackend` and `readerPath` (and the flag `--reader`) are ignored: an old
config.json still loads, and `service.log` names them once at start.
| `capacityWh` | number | 2304 | Battery capacity, Wh, for the battery-time formula. A new install writes the value of `install.ps1 -CapacityWh` (required there); an upgrade keeps the config as it is. |
| `efficiency` | number | 0.85 | Inverter efficiency for the formula, until a learned value exists. |

## Polling and readings

| Key | Type | Default | Meaning |
|---|---|---|---|
| `pollSec` | int | 60 | Seconds between readings. |
| `fastPollSec` | int | 60 | Seconds between readings on battery under `fastBelow`. |
| `fastBelow` | number | 25 | Battery percent under which `fastPollSec` applies. |
| `readerTimeoutSec` | int | 30 | Time limit of one Bluetooth read (connect, every request, disconnect); a Bluetooth call that does not return is given up 2 s after it. |
| `stationTempHighC` | int | 60 | V2 stations: a station temperature (registers 1151-1153) above this writes a `station-hot` event and runs its action; re-armed 5 °C under it. |
| `maxGapSec` | int | 600 | A gap between accepted readings over this resets the decision state. |
| `socRatePerMin` | number | 5 | Largest plausible SOC change, points per minute. |
| `socJumpFloor` | number | 3 | The allowed SOC change is never under this many points. |
| `jumpWindowSec` | int | 120 | A reading accepted after a silence that jumps more than the rate allows over this many seconds counts as a rebase for hibernate. |
| `silentAfter` | int | 3 | Misses in a row before the station is reported silent. |

## Mains and battery levels

| Key | Type | Default | Meaning |
|---|---|---|---|
| `mainsLostSec` | int | 180 | No AC input this long -> the cut begins, eco on. |
| `mainsBackSec` | int | 180 | AC input held this long -> every mode lifted. |
| `emergencyAt` | number | 20 | Percent: station emergency. |
| `stopAt` | number | 10 | Percent: stop (the flag's level says critical). |
| `warnAt` | number | 7 | Percent: hibernate warning. `emergencyAt`, `stopAt` and `warnAt` rise above the configured value at a high measured drain (design.md section 6). |
| `hibernateAt` | number | 5 | Percent: the PC hibernates. |
| `agreeSec` | int | 90 | Two agreeing readings at least this far apart confirm a level. |
| `hibernateAfterWarnSec` | int | 180 | Hibernate waits at least this long after the warning. |
| `rebaseHibernateConfirmSec` | int | 600 | After a rebase in the cut, hibernate needs this long at or under `hibernateAt`. |
| `batteryRefreshSec` | int | 600 | On battery, the reading goes into the flag at least this often. |

## Battery time

| Key | Type | Default | Meaning |
|---|---|---|---|
| `rateWindowMin` | int | 60 | Minutes of readings off mains the measured discharge rate looks back over. |
| `rateMinDrop` | number | 3 | Least SOC drop, points, the measured rate needs. |
| `rateMinSpanMin` | int | 20 | Least span, minutes, the measured rate needs. |
| `fittedRuntime` | bool | true | Battery time from a loss model fitted to this station's own discharges, with a P10-P90 range; `false` = the measured rate and the efficiency formula alone. |

## Power mode and actions

| Key | Type | Default | Meaning |
|---|---|---|---|
| `powerMode.script` | string | not set | Not set: the service runs the power-mode switch in its own process (`BluettiWatcher.exe power-mode`). Set: that script, through `pwshPath`, instead. |
| `powerMode.flagPath` | string | `%ProgramData%\BluettiWatcher\power-mode.json` | The flag file; passed to the switch as `-Path` and `POWER_MODE_FILE`. |
| `powerMode.mutex` | string | `Local\BluettiWatcherPowerMode` | The named mutex guarding the flag; passed as `POWER_MODE_MUTEX`. |
| `powerMode.taskPrefix` | string | `BluettiWatcherEco` | Prefix of the switch's scheduled tasks (`<prefix>End`, `<prefix>GpuCap`, `<prefix>GpuRestore`); passed as `POWER_MODE_TASK_PREFIX`. |
| `powerMode.extraReaders` | string[] | `[]` | Local accounts allowed to read the flag folder besides the user, SYSTEM and Administrators (e.g. a service that shows the mode); passed as `POWER_MODE_EXTRA_READERS`, comma-joined. Each name `A-Z a-z 0-9 _ . - space`, 1-64 chars; an invalid one is reported in `/api/health` `problem` and none are passed. |
| `flagPath` | string | - | Older name of `powerMode.flagPath`; still read. |
| `pwshPath` | string | `pwsh.exe` | PowerShell 7 used to run the wake-task commands and a configured `powerMode.script`. |
| `powerModeTimeoutSec` | int | 60 | 1-600: time limit of each power-mode command and each wake-task command. A command over it is killed and counts as failed. |
| `actionTimeoutSec` | int | 30 | 1-600: time limit of each action (only actions). |
| `actions` | object | none | Commands run when the service enters a state: keys `eco`, `emergency`, `stop`, `hibernate-warn`, `normal`; and the alert `grid-voltage-high` (grid input above 253 V, again after it was under 250 V); V2 stations also `station-warning`, `station-fault`, `station-alarm-cleared` and `station-hot`. See below. |
| `dryRun` | bool | false | Log every power-mode command, action and hibernation instead of running it (also `--dry-run`). |
| `modes.engine` | string | `v2` | `v2`: user-defined power modes with Auto rules, set on the page or through the API ([design.md](design.md) §12, [api.md](api.md) Power modes); their settings live in `modes.json` in the data directory, not here. `legacy`: the fixed eco above and the page's Auto / Eco / Max. Any other value = `legacy`, named in the log. |
| `modes.commands` | object | `{}` | Named commands a mode's "Run a command on enter and on leave" action (`command.pair`) may run: `{ "<name>": { "exe": "<path>", "args": ["..."], "timeoutSec": 30 } }`. Only this file defines them; a mode, the page and the API pick a name, never an executable or an argument. Each run is windowless, with no shell, killed at `timeoutSec` (1-600); a non-zero exit is a failed action. See below. |
| `modes.openRgbPath` | string | `""` | OpenRGB.exe for the "RGB lighting off (OpenRGB)" action (`rgb.lighting`); empty = `Program Files\OpenRGB` or `%LOCALAPPDATA%\Programs\OpenRGB`. Only this file names it, never a mode or the API. The action starts it as a headless SDK server (`--server --noautoconnect`, no window, no tray icon) when none runs and leaves it running. |

An action is an executable followed by its arguments, started directly with no shell: a pipe, a redirect or a shell
built-in needs `cmd /c ...`. It runs windowless, with the environment values `POWER_MODE_FILE`, `POWER_MODE_MUTEX`,
`POWER_MODE_TASK_PREFIX` and `POWER_MODE_EXTRA_READERS` set, within `actionTimeoutSec`. Its exit code and the first 300 characters of its output are
logged as an event; a missing, failing or hanging command never changes a decision. It fires once per entry into a
state per power cut (the next cut fires again; `normal` fires once per lift). The page's Eco fires `eco`; its Max and
Auto-lift fire `normal`. A dry run logs the actions and their environment instead of running them.

A flag path set while `powerMode.mutex` is left at its default is reported at start (log and event `config-warning`):
set both, or neither.

Under `modes.engine` = `v2` (the default) the station's eco and mains-back run no power-mode command and no
`actions.eco` / `actions.normal` line: at the first v2 start those two lines become the commands `eco` and `normal`
(split into the executable and its arguments by the Windows rules, the same arguments the program received before;
a name already in `modes.commands` wins) and the built-in Eco's command pair runs `eco` on entering Eco and `normal`
on leaving it. The other `actions` keys fire as before. A pair entered before a restart still runs its leave command
when the mode is left: the modes journal, not the process's memory, says it was entered.

```json
"modes": {
  "commands": {
    "backup-pause":  { "exe": "C:\\Program Files\\Backup\\backup.exe", "args": ["pause"],  "timeoutSec": 30 },
    "backup-resume": { "exe": "C:\\Program Files\\Backup\\backup.exe", "args": ["resume"], "timeoutSec": 30 }
  }
}
```

The built-in Eco lists its optional actions off and empty: a command pair (a backup pause), services to stop (Windows
Search, Xbox gaming services, Windows Health, ... - the admin broker runs the stop and the start), Phone Link closed,
the GPU idle watcher, which logs what holds the GPU at full clock, and the RGB lighting off through OpenRGB (leaving
Eco loads the OpenRGB profile you pick; none picked = the lighting stays off). Turn them on and fill them in on the page.

Hibernation off: create an empty file `no-hibernate` in the data directory. It is read on every poll. With
`modes.engine` = `v2`, `PUT /api/settings/hibernate` turns it off too; the file is still honoured.

## Wake during a cut

| Key | Type | Default | Meaning |
|---|---|---|---|
| `wakeEveryMinutes` | int | 0 | 10-1440: a hibernation during a cut arms the wake task this many minutes ahead. 0 = off. Turn it on only after the check in the README passes. |
| `wakeReadSec` | int | 300 | 60-3600: woken by the timer with no accepted reading for this long, the PC hibernates again. |
| `wakeFloorPercent` | number or null | null | At or under this percent no wake is armed. null = `hibernateAt` - 5 (at least 0); bounded to 0..`hibernateAt`; 0 = no floor. |
| `wakeTaskName` | string | `BluettiWatcherWake` | The wake task `install.ps1` registers: 1-100 characters of letters, digits, space, `.`, `_`, `-`; never `BluettiWatcher` (the service's own task). A task of that name that is not the wake task the installer registers is never replaced or removed. |

## Cloud fallback

| Key | Type | Default | Meaning |
|---|---|---|---|
| `readingSource` | string or null | null | Where readings come from ([design.md §4 Reading source](design.md#reading-source)): `auto` Bluetooth first, the cloud when it misses · `bluetooth` never the cloud (no token, no cloud history) · `cloud` never Bluetooth (no `mac` needed). null = `auto`, or `bluetooth` when `cloudEnabled` is `false`. The page's Reading source buttons save a choice in `reading-source.json` in the data directory that wins over this key without a restart; the page's "Use config" (or deleting that file) returns to the key. While the source allows the cloud, the token is refreshed once a day even when Bluetooth answers. Another value refuses the start. |
| `cloudEnabled` | bool or null | null | Under the `auto` source: null = on while the token file exists; `true` on; `false` off. |
| `cloudTokenPath` | string or null | null | The token file; null = `cloud-token.json` in the data directory. The page's "Sign in to BLUETTI cloud" and `BluettiWatcher.exe --cloud-login` write it; the page's "Sign out" deletes it. |
| `staleLoadW` | number | 100 | A cloud reading off mains with at least this AC load whose SOC has not moved for `staleAfterMin` is stale (a miss). |
| `staleAfterMin` | int | 30 | Minutes for the stale check; 0 or less = no check. |
| `cloudHistory` | bool or null | null | Gaps in the history filled from the cloud's 5-minute power curve ([design.md §4a](design.md#4a-cloud-history-back-fill)): null or `true` = on while the reading source allows the cloud (`auto` with the cloud fallback on, or `cloud`) and the token file exists; `false` off (also off for the live use below). |
| `cloudHistoryMaxAgeMin` | int | 10 | When the cloud's live state is no reading (every field null, no SOC, too old or all zero), the history's newest point is the reading while it is at most this many minutes old (design.md §4 AC2b); older is a miss; 0 or less = never. |
| `cloudLiveMaxAgeSec` | int | 120 | The cloud's live state (getDeviceLastAlive) is a reading while its timestamp, the station's local time read in this PC's time zone, is at most this many seconds off the clock either way (design.md §4 AC2); otherwise the history stand-in above is asked; 0 or less = never a live reading. |

## Service

| Key | Type | Default | Meaning |
|---|---|---|---|
| `port` | int | 18940 | HTTP port on 127.0.0.1 (also `--port`). |
| `lockWaitSec` | int | 30 | 0-600: a start waits this long for an older instance to release the port's lock. |
| `dataDir` | string | `%LOCALAPPDATA%\bluetti-watcher` | Database, log, config and kill switch (also `--data-dir`). |
| `rawRetentionDays` | int | 30 | Raw minute rows kept this long, then folded into 5-minute aggregates. |
| `tray` | bool | true | The notification-area icon and the Windows notifications ([design.md](design.md) §12); `false` or `--no-tray` = neither. Shown only in an interactive session. Which notifications show lives in `notifications.json` (below). |
| `trimWorkingSet` | bool | true | After each poll the process's pages leave its working set, so the Task Manager "Memory" column (private working set) drops under 1 MB after each poll (an open page raises it again until the next one). Cosmetic: commit (private bytes) does not change, and the pages fault back in at the next poll (from RAM while Windows keeps them on its standby list). `false` keeps them. |

## Notifications (`notifications.json`)

A file of its own in the data directory, written by the service when a switch changes (`PUT /api/settings/notifications`,
[api.md](api.md)); every group is on while the file is missing. An unreadable file is named in `service.log` and every
group stays on until the next change rewrites it.

| Key | Default | Notifications |
|---|---|---|
| `mains` | true | Mains lost, mains back (the confirmed cut, not a single reading). |
| `modeChanges` | true | Eco switched on by the service, modes lifted when mains is back, a power mode the Auto rules or a choice put in force (`v2`), a page choice dropped. |
| `lowBattery` | true | Battery emergency, stop, the hibernate warning. |
| `failures` | true | A power-mode command or action that failed, a mode choice refused, an admin broker older than the service. |
| `faults` | true | Station faults and warnings, the grid voltage alert. |
| `overTemperature` | true | The station's hottest temperature is above `stationTempHighC` (V2 stations). |

## DTEK module (`dtekWatcher`)

Off by default; it makes no request while off. `null` = off.

| Key | Type | Default | Meaning |
|---|---|---|---|
| `enabled` | bool | false | Turns the module on. |
| `queues` | string[] | none | The house's queues, such as `GPV1.1`. Without them (and with `site` off) the module cannot tell which queue is yours. |
| `group` | string, string[] or null | null | The queue to follow, a hint real cuts can refute; an array = a set whose planned outage is the union (at most 4). |
| `datasetUrl` | string | the Baskerville42/outage-data-ua Kyiv region file | Primary schedule source; must be plain https. |
| `staleAfterMin` | int | 90 | The schedule is stale when its newest collection is older. |
| `minCutMin` | int | 15 | A cut shorter than this many minutes is a blip: it counts in no DTEK statistic (the "usually back N min before/after the plan" median, the queue search, the manual queue's hint); it stays on the chart and in the cut history. 0 = every cut counts; a cut of exactly this length counts. 0 to 1440, else the module is off with the reason. |
| `site` | bool | false | Opt-in. false = the distributor's site is never asked; only the public dataset is read. true = the watcher also reads the distributor's website the way a browser does, which picks up plan edits sooner and, with `city`, `street` and `house`, names the house's queues. Use of the site is subject to its own terms; you turn it on at your own responsibility. |
| `host` | string | `www.dtek-krem.com.ua` | The site's host; with the dataset's, the only hosts a request may go to. |
| `city`, `street`, `house` | string | "" | Optional address, sent to the site only, to learn the house's queues and current outage. |

## Command line

`BluettiWatcher.exe [--config <file>] [--data-dir <dir>] [--port <n>] [--flag <file>] [--power-mode <power-mode.ps1>]
[--dry-run] [--no-tray]` - flags override config.json; `--no-tray` sets `tray` to false. `--flag` sets `powerMode.flagPath`,
`--power-mode` sets `powerMode.script`.

`BluettiWatcher.exe --cloud-login [--data-dir <dir>]` - signs in to the BLUETTI cloud in the browser and writes the
token file, then exits. An answer without a valid `expires_in` is refused and nothing is written. The dashboard's
"Sign in to BLUETTI cloud" does the same without a terminal, inside the running service.

`BluettiWatcher.exe --help` (`-h`, `/?`) prints the usage and `--version` prints `Bluetti Watcher <version>`; both exit 0
and win over every other word on the line (`--cloud-login --help` only prints the usage). A flag that takes a value
refuses a missing value or one that starts with `--`; an unknown flag or word prints a message and exits 2. The exe has no
console of its own: these commands print into the terminal they were started from (nothing, when started by the logon
task). PowerShell does not wait for such an exe, so `$LASTEXITCODE` is not set after a plain `& .\BluettiWatcher.exe`;
pipe it (`& .\BluettiWatcher.exe --help | Out-Host`) or use `Start-Process -NoNewWindow -Wait -PassThru`.

## Environment of the power-mode scripts

`power-mode/*.ps1` read `POWER_MODE_FILE`, `POWER_MODE_MUTEX`, `POWER_MODE_TASK_PREFIX` and `POWER_MODE_EXTRA_READERS`,
with the defaults above (no extra readers). The service sets all four from `powerMode` for every child it starts; set them yourself when you run the scripts by
hand with non-default names.
