# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses [Semantic Versioning](https://semver.org/).
The release notes on GitHub are this file's section for the tag.

## [Unreleased]

## [1.4.2] - 2026-10-28

### Download

| File | What it is |
|---|---|
| `bluetti-watcher-1.4.2-win-x64.zip` | The service, the power-mode scripts, the installer and the docs |
| `bluetti-watcher-1.4.2-win-x64.zip.sha256` | Its SHA-256. Check with `(Get-FileHash bluetti-watcher-1.4.2-win-x64.zip).Hash` |

Upgrade: extract the zip, then `pwsh -NoProfile -File install.ps1` (a dry run) and the same with `-Apply`. Your
config, database and history stay.

### Fixed

- Bluetooth: a late answer is no longer taken for the battery percent (or another field) when a request for the same
  registers was still expected from an earlier poll and a request for other registers of the same size was lost after
  it. Such an answer is now set aside and the registers asked again. Before, after a run of lost answers a field read
  one register at a time (a station refusing a batch) could fill the battery percent, which decides hibernation.

## [1.4.1] - 2026-10-26

### Download

| File | What it is |
|---|---|
| `bluetti-watcher-1.4.1-win-x64.zip` | The service, the power-mode scripts, the installer and the docs |
| `bluetti-watcher-1.4.1-win-x64.zip.sha256` | Its SHA-256. Check with `(Get-FileHash bluetti-watcher-1.4.1-win-x64.zip).Hash` |

Upgrade: extract the zip, then `pwsh -NoProfile -File install.ps1` (a dry run) and the same with `-Apply`. Your
config, database and history stay.

### Fixed

- Eco or Max pressed during a battery emergency now shows why it was refused, right under the mode buttons, in
  English and Ukrainian. Eco is refused at the press, as Max already was (409, code `eco-held`): before, the press was
  accepted, dropped by the service a moment later, and its reason reached the page only at the next minute's refresh.
- Cloud history: the station's serial from a history run is kept in `station.db`, so later runs need no cloud device
  list. The BLUETTI device-list endpoint was seen leaving requests unanswered (the run logged `devices: timed out after
  15 s, again on retry`). A failed device list is still a failed run, named in `service.log`.

## [1.4.0] - 2026-10-26

### Download

| File | What it is |
|---|---|
| `bluetti-watcher-1.4.0-win-x64.zip` | The service, the power-mode scripts, the installer and the docs |
| `bluetti-watcher-1.4.0-win-x64.zip.sha256` | Its SHA-256. Check with `(Get-FileHash bluetti-watcher-1.4.0-win-x64.zip).Hash` |

Upgrade: extract the zip, then `pwsh -NoProfile -File install.ps1` (a dry run) and the same with `-Apply`. Your
config, database and history stay. Requirements and details: [README](https://github.com/vitaliy-shatskiy/bluetti-watcher#quick-start).

### Added

- Cloud history back-fill: with the cloud fallback on, gaps of 10 minutes or more in the last 30 days (the PC was off,
  no source answered) are filled from the station's own 5-minute power curve in the BLUETTI cloud. The filled rows
  carry the reason `cloud history`, and a live reading is never changed. It reads only two statistics endpoints, and
  any other path is refused in code before a request exists. It runs after start, once a day and after a gap ends,
  with at most 4 calls a run and 40 a day, each named in `service.log`. A day the cloud keeps only hourly is skipped.
  `"cloudHistory": false` turns it off ([design.md §4a](docs/design.md#4a-cloud-history-back-fill)).

### Changed

- `install.ps1` requires `-CapacityWh` for a new install and writes it to `config.json`: the battery time no longer
  assumes the 2304 Wh of an AC200PL on another station. An upgrade keeps the existing `config.json`, and the dry run
  names a config without `capacityWh`.

## [1.3.3] - 2026-10-16

### Download

| File | What it is |
|---|---|
| `bluetti-watcher-1.3.3-win-x64.zip` | The service, the power-mode scripts, the installer and the docs |
| `bluetti-watcher-1.3.3-win-x64.zip.sha256` | Its SHA-256. Check with `(Get-FileHash bluetti-watcher-1.3.3-win-x64.zip).Hash` |

Upgrade: extract the zip, then `pwsh -NoProfile -File install.ps1` (a dry run) and the same with `-Apply`. Your
config, database and history stay. Requirements and details: [README](https://github.com/vitaliy-shatskiy/bluetti-watcher#quick-start).

### Fixed

- Bluetooth: after a restart of the PC, Windows did not find a station it had not paired with until something scanned
  for it, so every poll fell back to the cloud. When the station is not found at its address, the watcher now scans for
  it for up to 8 s and opens it again. A station still not found names whether the scan saw it.
- Bluetooth: a reading no longer loses its battery % and AC values when Windows reports a disconnect while the
  connection is set up. That report is ignored until the connection is ready, and a request whose connection dropped,
  timed out or answered garbled is asked once more over a new connection.
- Cloud fallback: a slow cloud answer no longer leaves a grey gap on the chart. Each cloud request may take 15 s
  (was 10 s), and a device list request that got no answer at all (timed out, connection failed) is sent once more
  in the same poll. An answer is never sent again, nor is a token refresh. The log names a second-try answer, and
  a miss names both tries.

## [1.3.2] - 2026-10-16

### Download

| File | What it is |
|---|---|
| `bluetti-watcher-1.3.2-win-x64.zip` | The service, the power-mode scripts, the installer and the docs |
| `bluetti-watcher-1.3.2-win-x64.zip.sha256` | Its SHA-256. Check with `(Get-FileHash bluetti-watcher-1.3.2-win-x64.zip).Hash` |

Upgrade: extract the zip, then `pwsh -NoProfile -File install.ps1` (a dry run) and the same with `-Apply`. Your
config, database and history stay. Requirements and details: [README](https://github.com/vitaliy-shatskiy/bluetti-watcher#quick-start).

### Added

- Dashboard, DTEK hour grid: the hours of today already past show what the station really saw, hatched over the plan,
  minute by minute: green = power was on, red = power was off, grey = no reading. A tooltip lists each phase with its
  clock span and length.

## [1.3.1] - 2026-10-16

### Download

| File | What it is |
|---|---|
| `bluetti-watcher-1.3.1-win-x64.zip` | The service, the power-mode scripts, the installer and the docs |
| `bluetti-watcher-1.3.1-win-x64.zip.sha256` | Its SHA-256. Check with `(Get-FileHash bluetti-watcher-1.3.1-win-x64.zip).Hash` |

Upgrade: extract the zip, then `pwsh -NoProfile -File install.ps1` (a dry run) and the same with `-Apply`. Your
config, database and history stay. Requirements and details: [README](https://github.com/vitaliy-shatskiy/bluetti-watcher#quick-start).

### Fixed

- Dashboard: on screens 2200 px and wider (the page is scaled up there) the tooltips of the chart, the battery ring
  and the tap tooltips on phones appeared far right of the cursor. They now sit next to it.

## [1.3.0] - 2026-10-16

### Download

| File | What it is |
|---|---|
| `bluetti-watcher-1.3.0-win-x64.zip` | The service, the power-mode scripts, the installer and the docs |
| `bluetti-watcher-1.3.0-win-x64.zip.sha256` | Its SHA-256. Check with `(Get-FileHash bluetti-watcher-1.3.0-win-x64.zip).Hash` |

Upgrade: extract the zip, then `pwsh -NoProfile -File install.ps1` (a dry run) and the same with `-Apply`. Your
config, database and history stay. Requirements and details: [README](https://github.com/vitaliy-shatskiy/bluetti-watcher#quick-start).

### Added

- `fittedRuntime` (default `true`): the battery time comes from a loss model (idle draw plus a proportional loss)
  fitted to the station's own discharges, shrunk toward the configured `efficiency` while data are few, with a
  P10-P90 range: `/api/now` adds `hoursLeftLow`, `hoursLeftHigh` and the `runtimeSource` value `model`. Chosen by a
  leave-one-cut-out backtest that ships with the tests; `false` keeps the measured rate and the efficiency formula.
  Early in a cut the model counts the load saving eco mode is about to bring, learned from the station's past eco
  switches (none logged = no change).
- Dashboard: chart ranges 7 and 30 days, a battery card for the last 30 days, and the DTEK schedule as an hour grid
  for today and tomorrow. The DTEK banner says when power came back before the plan or went out outside it.

### Changed

- Dashboard redesign: the battery ring is coloured by the configured levels with a tooltip per zone, charts draw at
  their real width, the layout fits a phone, Full HD and 2K, and texts are shorter. Still one HTML file, no external
  libraries.

## [1.2.0] - 2026-10-14

### Download

| File | What it is |
|---|---|
| `bluetti-watcher-1.2.0-win-x64.zip` | The service, the power-mode scripts, the installer and the docs |
| `bluetti-watcher-1.2.0-win-x64.zip.sha256` | Its SHA-256; check with `(Get-FileHash bluetti-watcher-1.2.0-win-x64.zip).Hash` |

Upgrade: extract the zip, then `pwsh -NoProfile -File install.ps1` (a dry run) and the same with `-Apply`; your
config, database and history stay. Requirements and details: [README](https://github.com/vitaliy-shatskiy/bluetti-watcher#quick-start).

### Added

- `trimWorkingSet` (default `true`): after each poll the process's pages leave its working set, so the Task Manager
  "Memory" column (private working set) drops under 1 MB (0.5 MB idle in `tools/bench.ps1`). Cosmetic: commit
  (private bytes) does not change, and the pages fault back in at the next poll; `false` keeps them.

### Changed

- SQLite is Windows' own `winsqlite3.dll` (System32): the release ships `BluettiWatcher.exe` alone, without
  `e_sqlite3.dll` (1.9 MB less). The SQLite version now follows Windows Update; an existing `station.db` opens as
  before. It needs SQLite 3.24.0 or later in Windows: with an older one (or none) the service does not start, logs
  one `DID NOT RUN` line with the version found and exits with code 3.
- Memory: after each poll the native heaps' free memory is
  decommitted (HeapCompact, HeapOptimizeResources); the data and flag folders come from `%LOCALAPPDATA%` and
  `%ProgramData%` (the shell call only when a variable is missing or not an absolute path), so reading them loads no
  shell32. Private bytes in `tools/bench.ps1`: 4.9 MB idle and 8.3 MB after a page load on a 30-day database (5.2 MB
  and 9.1 MB for 1.1.1 on the same bench). The bench also sends 32 concurrent 30-day `/api/history` requests, three
  times, and fails if the process exits.

## [1.1.1] - 2026-10-14

### Download

| File | What it is |
|---|---|
| `bluetti-watcher-1.1.1-win-x64.zip` | The service, the power-mode scripts, the installer and the docs |
| `bluetti-watcher-1.1.1-win-x64.zip.sha256` | Its SHA-256; check with `(Get-FileHash bluetti-watcher-1.1.1-win-x64.zip).Hash` |

Upgrade: extract the zip, then `pwsh -NoProfile -File install.ps1` (a dry run) and the same with `-Apply`; your
config, database and history stay. Requirements and details: [README](https://github.com/vitaliy-shatskiy/bluetti-watcher#quick-start).

### Added

- `/api/health` `bluetoothEncrypted`: true once the station opened the encrypted Bluetooth handshake (it is read
  through the cloud fallback until the service restarts). Information only: `ok` stays as it is.
- `/api/health` `bluetoothStalled`: true while a Bluetooth read abandoned at its time limit is still running.
  Information only: `ok` stays as it is.

### Fixed

- Bluetooth, one read at a time: a read abandoned at `readerTimeoutSec` plus 2 s (a Bluetooth call that ignored its
  timeout) could still hold its connection when the next poll opened another. While it is alive, a poll starts no
  Bluetooth read (a miss; the line is not repeated while nothing else is logged in between; the cloud fallback
  answers), and the abandoned read releases its connection when it ends. One still running after max(4 x that limit,
  2 minutes) is given up with a log line and the next poll reads again; its late end changes nothing for newer reads.
- Bluetooth, late answers: a reconnect may reuse the radio link, so the answer to a request that timed out could
  still arrive on the next connection, also in the next poll; for one-register reads (the per-field fallback, and the
  battery % of V2 models) every answer has the same byte count, so another register's value (for example AC input or
  DC output W) could be taken as the battery percent. An answer an earlier unanswered request could have sent is now
  set aside and the same registers are asked again, each with its own 5 s wait (still the one read request,
  function 0x03).

## [1.1.0] - 2026-10-14

### Download

| File | What it is |
|---|---|
| `bluetti-watcher-1.1.0-win-x64.zip` | The service, the power-mode scripts, the installer and the docs |
| `bluetti-watcher-1.1.0-win-x64.zip.sha256` | Its SHA-256; check with `(Get-FileHash bluetti-watcher-1.1.0-win-x64.zip).Hash` |

Upgrade: extract the zip, then `pwsh -NoProfile -File install.ps1` (a dry run) and the same with `-Apply`; your
config, database and history stay. Requirements and details: [README](https://github.com/vitaliy-shatskiy/bluetti-watcher#quick-start).

### Added

- THIRD-PARTY-NOTICES.md: the register maps, frame layout and poll flow are ported from bluetti-bt-lib (MIT); a
  release without it fails.
- The page and its API are served compressed (Brotli, else gzip, by `Accept-Encoding`) with an `ETag`: a reload with
  the tag is a `304` with no body.
- `service.log` folds identical repeated lines into one ("repeated N times since HH:mm:ss: ...", at least every
  15 minutes while the line keeps repeating).

### Changed

- The page stops requesting while its tab is hidden, and when seen again loads the history since its last row (a full
  26 hours every 30 minutes) instead of the whole span every minute.
- Footprint: aggressive garbage collection after each poll, and unused runtime features trimmed out of the NativeAOT
  exe; private memory is about 5 MB idle and about 9 MB after a page load on a 30-day database (`tools/bench.ps1`).
- New databases use 1 KB pages (an existing database keeps its page size); the WAL file is capped at 1 MB.
- `service.log`: a missed Bluetooth poll is logged at the first miss, at the turn to "station silent" and every 15th
  after (each miss is still an event row); a power-mode switch logs `power-mode <mode> -> exit N in X ms`.

- DTEK module: a cut shorter than 15 minutes no longer counts in the "usually back N min before/after the plan" median,
  the queue search or the manual queue's hint; it stays on the chart and in the cut history. New `dtekWatcher.minCutMin`
  (default 15, 0 to 1440; 0 = every cut counts as before).
- Bluetooth is read by a built-in reader through the Windows Bluetooth LE stack; Python is no longer needed. Read
  only: Modbus function 0x03 under register 2000 is all it can send, and its only GATT writes are that request and the
  notification subscription; each answer's length and CRC are checked. One connection per poll, the reading fields in
  batched reads (two requests for the AC200PL); a timed-out or invalid answer ends its connection, so a late answer
  never fills another request; the whole read is bounded by `readerTimeoutSec` plus 2 s.
- Bluetooth reads 29 models: the IoT V1 models AC200L, AC200M, AC200PL, AC300, AC500, EB3A, EP500, EP500P and the IoT V2
  models AC2A, AC2P, AC50B, AC60, AC60P, AC70, AC70P, AC180, AC180T, AC180P, AP300, EL10, EL30V2, EL100V2, EP600, EP760,
  EP800, EP2000, Handsfree 1, PR30V2, PR100V2 (the EP600, EP760, EP800 and EP2000 report the battery level only, which cannot tell mains from battery: no Bluetooth reading,
  the cloud fallback answers). A model with no output-mode register (every V2 model, the EB3A) gets the mode from the AC
  powers as the cloud reading does (`BLE_MAINS`, `INVERTER_OUTPUT`). A station that encrypts its Bluetooth link (also when the
  handshake arrives split over notifications) ends the poll with no reading, is not connected to again until the service
  restarts, and the cloud fallback answers. A failed write or a dropped link reconnects like a timeout. A cloud token made
  while the service runs works without a restart. A repeated miss that differs only in its byte counts is one log line.
- The target framework is `net10.0-windows10.0.19041.0` (the WinRT Bluetooth API); NativeAOT stays warning-free.
- The Claude Code hook is part of the exe: `BluettiWatcher.exe hook` replaces `node integrations/claude-code/power-mode.js`
  with the same input, output, flag and state files, and needs no Node.js. Its four rules texts are built into the exe.
  Replace the three hook commands in `settings.json`: [integrations/claude-code](integrations/claude-code/README.md).
- The power-mode switch is part of the exe: `BluettiWatcher.exe power-mode <command>` takes the same commands,
  options, exit codes and output as `power-mode.ps1`, writes the same flag file under the same mutex and task names.
  The service runs it in its own process: no PowerShell and no process start per eco/max switch (a configured
  `powerMode.script` still runs that script through `pwshPath`).
- `power-mode.ps1` and `power-mode-end.ps1` forward to the exe (Windows PowerShell 5.1 or PowerShell 7), so scripts
  and end tasks that call them keep working; `power-mode-flag.ps1` stays the PowerShell reader of the flag.
- The eco end task runs `BluettiWatcher.exe power-mode end` directly (as the user, least privilege, no window): no
  `conhost.exe`, no Windows PowerShell. The GPU tasks are unchanged (System32 `nvidia-smi` only).
- `install.ps1` copies the power-mode scripts after the new exe passed its health check.

### Removed

- Python: the `bluetti-read` venv, `requirements.lock`, `tools/build-venv.ps1`, the writer guard and install.ps1's
  Python 3.12-3.14 requirement. An old config.json still loads: `bleBackend`, `readerPath` and the flag `--reader`
  are ignored and named once in `service.log`. An earlier install's `.venv\` folder in the data directory is no
  longer used; `install.ps1 -Apply` deletes it once the new exe answers.
- `integrations/claude-code/power-mode.js` and its Node test (ported to the .NET tests).

### Fixed

- Read only, forever: 1.0.0's Python library wrote the AC300's battery-pack selector (register 3006, Modbus function
  0x06) on every read. The built-in reader never writes; the AC300 gives its full reading (battery, AC and DC power,
  output mode, inverter voltage and frequency), without its battery packs.

## [1.0.0] - 2026-10-14

First public release: a Windows service that keeps a PC running through power cuts on a BLUETTI power station and
hibernates it safely before the battery runs out.

### Download

| File | What it is |
|---|---|
| `bluetti-watcher-1.0.0-win-x64.zip` | The service, the power-mode scripts, the installer and the docs |
| `bluetti-watcher-1.0.0-win-x64.zip.sha256` | Its SHA-256; check with `(Get-FileHash bluetti-watcher-1.0.0-win-x64.zip).Hash` |

Install: extract the zip, then `pwsh -NoProfile -File install.ps1 -Mac <station address>` (a dry run) and the same
with `-Apply`. Requirements and details: [README](https://github.com/vitaliy-shatskiy/bluetti-watcher#quick-start).

### Added

- Reads the station once a minute over Bluetooth LE, read only; the library's writer is deleted from the venv and the
  service refuses to start while any piece of it is on disk.
- Power cut detected after 3 minutes without mains: eco (Windows Power saver, optional GPU power cap on NVIDIA cards);
  emergency at 20 %, stop at 10 %, hibernate warning at 7 %, hibernation at 5 %; mains back for 3 minutes lifts every
  mode. All thresholds configurable.
- Guards against bad readings: implausible jumps rejected, two agreeing readings before any level acts, a fresh reading
  right before a hibernation, a longer confirmation after the station recalibrates.
- Dashboard on `127.0.0.1` in English and Ukrainian: battery, time left at the current load, power cuts, history
  chart, events; manual control Auto / Eco / Max.
- The power-mode switch (`power-mode/`) and its flag file, readable by other programs.
- Optional `actions`: your own command when eco, emergency, stop, hibernate warning or normal begins.
- Optional wake timer: a PC hibernated in a cut wakes periodically to check whether mains is back.
- Optional DTEK module (Kyiv region): planned outages for your queue, the expected return, and whether the battery
  lasts until then.
- Optional cloud fallback (unofficial, read only) with `BluettiWatcher.exe --cloud-login`.
- Optional hook for AI coding sessions (`integrations/`) that tells running sessions the power mode.
- Installer with a dry run by default; in-place upgrade with a staged exe and a rollback when the health check fails.

### Known issues

- Only the AC200PL is tested; other BLUETTI models may work through `type`.
- A station that keeps recalibrating for a whole cut never lets the PC hibernate: every recalibration restarts the
  long confirmation. The page shows a `hibernate-delayed` note when this happens.
- The optional wake timer (`wakeEveryMinutes`, off by default): if Bluetooth never answers after a timer wake, the PC
  goes wake, hibernate, wake every interval until the station is empty. Verify that the PC wakes from hibernation on
  the RTC timer first (the README explains how) and leave the option off if you cannot watch the first cycles.
- The cloud fallback uses an unofficial API and may stop working if BLUETTI changes it.
- The release is not code-signed; check the `.sha256` before running it.

Source at this tag: [v1.0.0](https://github.com/vitaliy-shatskiy/bluetti-watcher/tree/v1.0.0)

[Unreleased]: https://github.com/vitaliy-shatskiy/bluetti-watcher/compare/v1.4.2...HEAD
[1.4.2]: https://github.com/vitaliy-shatskiy/bluetti-watcher/compare/v1.4.1...v1.4.2
[1.4.1]: https://github.com/vitaliy-shatskiy/bluetti-watcher/compare/v1.4.0...v1.4.1
[1.4.0]: https://github.com/vitaliy-shatskiy/bluetti-watcher/compare/v1.3.3...v1.4.0
[1.3.3]: https://github.com/vitaliy-shatskiy/bluetti-watcher/compare/v1.3.2...v1.3.3
[1.3.2]: https://github.com/vitaliy-shatskiy/bluetti-watcher/compare/v1.3.1...v1.3.2
[1.3.1]: https://github.com/vitaliy-shatskiy/bluetti-watcher/compare/v1.3.0...v1.3.1
[1.3.0]: https://github.com/vitaliy-shatskiy/bluetti-watcher/compare/v1.2.0...v1.3.0
[1.2.0]: https://github.com/vitaliy-shatskiy/bluetti-watcher/compare/v1.1.1...v1.2.0
[1.1.1]: https://github.com/vitaliy-shatskiy/bluetti-watcher/compare/v1.1.0...v1.1.1
[1.1.0]: https://github.com/vitaliy-shatskiy/bluetti-watcher/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/vitaliy-shatskiy/bluetti-watcher/releases/tag/v1.0.0
