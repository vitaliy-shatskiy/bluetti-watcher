# Bluetti Watcher - design

Bluetti Watcher is an always-on local service for a Windows PC that runs from a BLUETTI power station during power cuts.
It reads the station once a minute, stores the readings, serves a dashboard and an HTTP API on `127.0.0.1`, switches
the PC's power mode by mains state and battery level, and hibernates the PC before the battery is empty.

This document states the rules the service follows. Keys and defaults live in [configuration.md](configuration.md),
routes and fields in [api.md](api.md), the station's registers in [bluetti-protocol.md](bluetti-protocol.md), the
development workflow in [CONTRIBUTING.md](../CONTRIBUTING.md). Defaults are quoted here in brackets only where a rule
needs the number.

## 1. Goals and limits

- Small footprint: one process, about 0 % CPU between readings. After each poll: a compacting garbage collection,
  the native heaps compacted (HeapCompact, HeapOptimizeResources), then the working set trimmed unless
  `trimWorkingSet` is false (commit is unchanged; the pages fault back in at the next poll).
- Read only toward the station. The Bluetooth reader builds one kind of frame, a read of holding registers (Modbus
  function 0x03) under register 2000; its only GATT writes are that request and the notification subscription.
- The cloud client is read only too: the live state, the device list, the token refresh and two history endpoints
  (section 4). No control, bind or fulfilment call exists in the code; a test checks every URL against an allow-list.
- Never block or harm the PC on bad data: unknown beats wrong.

## 2. Architecture

1. **Process.** A per-user background process with no window, started at logon by the scheduled task
   `BluettiWatcher` as the user (Medium integrity, interactive session), restarted on failure. It never runs in
   session 0: the power-mode mutex is a `Local\` name shared with the user's own scripts, so a start in session 0
   exits 2.
2. **Host.** One C# NativeAOT exe. `BluettiWatcher.exe` is a console program for commands typed in a terminal
   (`--help`, `--version`, `--cloud-login`, `power-mode ...`, `--broker-allow ...`). The scheduled tasks start its
   twin `BluettiWatcherService.exe`, the same file with the Windows GUI subsystem, so no console window opens.
3. **Bluetooth reader** (`Io/Ble/`, ported from bluetti-bt-lib, MIT, see
   [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)). In-process WinRT GATT for the 29 unencrypted IoT V1 and V2
   models (list: configuration.md, `type`). Rules:
   - Connect, read, disconnect on every poll, so the phone app can connect between polls. Requests go on ff02,
     answers come as notifications on ff01; each answer is reassembled to its full length and CRC-checked.
   - The GATT services and ff01/ff02 come from Windows' cache first; only when the cache fails or lacks them does the
     same open look them up over the air, with its tries.
   - One connection open may take 40 % of `readerTimeoutSec` and is cut there even when the Bluetooth call ignores
     cancellation (a link it hands over later is released). A failed open (GATT unreachable, no ff01/ff02, the cap)
     is tried once more, so two opens leave at least 20 % of the budget for the reads. An open cut at the cap logs
     the step it hung in.
   - The whole read is bounded by `readerTimeoutSec` + 2 s whatever the Bluetooth stack does. When the budget runs
     out the fields already read are kept as a partial reading and the log names what the read waited for.
   - An answer that timed out or came back invalid ends its connection; the next request goes over a new one. An
     answer that an earlier unanswered request could have produced (same byte count) is set aside and the registers
     are asked again, so a late answer never fills another request.
   - One read at a time. While a read abandoned at the bound is still alive, a poll starts no Bluetooth read (a
     miss; `/api/health` `bluetoothStalled`). One still alive after max(4 x the bound, 2 min) is given up and the
     next poll reads again; its late end touches no newer read.
   - A station that opens with the encrypted protocol's key exchange (`**` and a checksum) ends the poll with a miss
     and is not connected to again until the service restarts (`/api/health` `bluetoothEncrypted`).
   - Models with no AC input power field (EP600, EP760, EP800, EP2000) are never connected: a miss, the cloud
     answers. A model with no output-mode register (every V2 model, the EB3A) gets the mode from the powers: AC input
     above 0 W = `BLE_MAINS`, else AC output above 0 W = `INVERTER_OUTPUT`.
   - The AC200PL's read of registers 70-80 also carries the grid voltage (77) and frequency (80), both / 10.
   - A V2 station is asked four optional blocks after its reading (`V2Extras`): pack voltage and current, its own
     time to full and to empty, warnings and faults (decoded into the app's codes), temperatures. An extra never
     opens a connection and is not asked again in the poll. An exception reply marks its block not supported at
     once; a short, garbled or missing answer in 2 polls in a row does too. The mark holds until restart
     (`/api/health` `stationBlocks`). A V1 station is never asked them: its registers 100-138 carry other data.
   - The pack current does not feed the battery-time estimate (it is read unsigned, so it does not tell charge from
     discharge).
4. **HTTP** on `127.0.0.1` only (section 16).
5. **Storage** in SQLite through Windows' own `winsqlite3.dll` (section 15).
6. **Power-mode switch** (`PowerMode/`): `BluettiWatcher.exe power-mode <command>`, the flag file under a named
   mutex with atomic writes, and the end of a timed eco (the end task runs the exe itself). The scripts in
   `power-mode/` (installed to `<data dir>\power-mode\`) forward to it or read the flag; their guide is
   [power-mode/README.md](../power-mode/README.md). Every change of the flag goes through the switch. The service runs
   it in its own process on a thread of its own, only at a transition; a configured `powerMode.script` runs that
   script under `pwsh -NoProfile -NonInteractive -File` instead. The commands the service sends:

   | When | Command |
   |---|---|
   | station eco (legacy engine, section 5) | `eco -Source station` |
   | station emergency (section 6) | `on -Source station` |
   | lift: mains back, the page's Max (legacy engine) | `off -Source station` |
   | battery report while on battery | `battery -Soc <n> -Level ok\|low\|critical\|hibernating -Mains on\|off -HibernateAt <n>` |

   Each command has `powerModeTimeoutSec` to finish; a hung one is killed and counts as failed. A tool of the switch
   that exited but left its output open (a child still running) is waited for 2 s more, then its tree is stopped and
   what it printed is used. The `plan:`, `warn:` and `note:` lines of a command are logged (the battery call excepted).
7. **Live names** come from `powerMode` in config.json: flag file, mutex, scheduled-task prefix, extra readers. The
   service passes them to every power-mode child and every `actions` command as `POWER_MODE_FILE`,
   `POWER_MODE_MUTEX`, `POWER_MODE_TASK_PREFIX` and `POWER_MODE_EXTRA_READERS`, and uses the same mutex when it
   reads the flag. A flag path set while the mutex stays the default is reported at start (log and event
   `config-warning`). Other programs may read the flag to adapt to the power mode.
8. **No station address.** Without `mac` (and with a reading source that needs Bluetooth) the service serves the
   page and `/api/health` with a problem naming `mac` and polls nothing. There is no default address.

Exit codes of the service: 0 stopped normally · 2 did not run (bad arguments, the port taken, session 0, another
instance still holding the port's lock after `lockWaitSec`) · 3 did not run: `winsqlite3.dll` missing or older than
3.24.0, named in `service.log`.

## 3. Readings

- A reading is accepted only when complete (SOC 0-100, AC input W, AC output W, an output mode) and plausible: its
  SOC differs from the last accepted one by at most max(`socJumpFloor`, `socRatePerMin` x elapsed minutes). A
  rejected reading is stored with its reason and never used for a level or a timer.
- A gap over `maxGapSec` between accepted readings resets the decision state (cut, fired levels, level, timers,
  streaks). Plausibility still applies across a gap: the allowance grows with the elapsed time and the last accepted
  reading is kept.
- **Rebase.** Three rejected readings in a row that agree with each other within the rate make the newest the
  accepted baseline, so a wrong baseline cannot hold for long. A rebased reading only moves the baseline: it never
  counts toward or breaks a level's agreement, advances no mains timer and writes no battery report.
- The latest rebase in a cut is remembered until mains is confirmed back, gap resets included; while one is
  remembered, hibernate follows the rebase rule (section 6).
- A reading accepted after a silence whose SOC change is larger than the rate allows over `jumpWindowSec` also counts
  as a rebase for that rule (only the silence made the allowance wide enough). Unlike a rebased reading it still
  counts toward levels and timers.
- Reading times are taken when the reading arrives. Durations come from reading timestamps; a backward clock jump
  counts as a gap. The poll loop runs on the monotonic clock, so a wall-clock step neither delays nor hastens a poll.
- After `silentAfter` misses in a row the station is reported silent. On battery under `fastBelow` % the poll
  interval is `fastPollSec`, else `pollSec`.
- **Mains state:** AC input above 0 = on mains; AC input 0 with output mode `INVERTER_OUTPUT` = on battery;
  anything else = unknown.

## 4. Cloud fallback

When Bluetooth does not answer, the reading may come from the BLUETTI cloud: the live state the official app reads
(`POST https://gw.bluettipower.com/api/bluiotdata/realtime/v1/getDeviceLastAlive`, body `{"deviceSn"}`) with the token
of BLUETTI's official Home Assistant integration, kept in `cloud-token.json`. The station's serial comes from that
integration's device list (`GET .../api/bluiotdata/ha/v2/devices`), read only while no serial is known; the device
list's values are never a reading.

### Reading source

The source in force is, in order: the page's choice (saved in `reading-source.json`), the `readingSource` key, `bluetooth`
when `cloudEnabled` is false, else `auto`. It is read at every poll, so a change needs no restart; the page's "Use
config" deletes the saved choice.

| Source | Bluetooth | Cloud |
|---|---|---|
| `auto` | first | when Bluetooth misses, if `cloudEnabled` is true, or unset and the token file exists |
| `bluetooth` | only | never: no token needed, no history back-fill |
| `cloud` | never: no `mac` needed, any model the cloud lists | only |

A model Bluetooth does not read is polled through the cloud alone when the cloud is on.

### Cloud sign-in

- `BluettiWatcher.exe --cloud-login` opens BLUETTI's OAuth login in the browser, catches the answer on a localhost
  address, writes the token file and exits.
- The page's sign-in (`POST /api/cloud/sign-in`) runs the same authorization-code flow inside the service: a one-shot
  catcher on `127.0.0.1` at a port the OS assigns, accepting only the `state` it issued, waiting 5 minutes. A second
  press while one waits gets the same address. The token is written under the cloud reader's lock, so a read in
  flight cannot write an older refreshed pair over it, and what the reader held for the old token is dropped.
- Sign-out (`POST /api/cloud/sign-out`) stops a waiting sign-in and deletes the token file.
- No answer and no log line carries a token; the authorize address (with its one-time `state`) goes only to the page.

### Cloud state

`/api/health` and the page show `off` (the source never calls the cloud) · `signed-out` (no token file) · `ok` (the
last read answered, or signed in and not read yet) · `sign-in-needed` (the server refused the refresh token) ·
`error` (the last read failed; the detail says why).

- AC1: when Bluetooth returns a reading, the cloud is not called.
- AC2: a live state is a reading when `allFieldIsNull` is false, `batterySoc` is numeric and `timestamp` is at most
  `cloudLiveMaxAgeSec` off the clock either way. Soc = `batterySoc`, InAc = `powerGridIn`, OutAc = `powerAcOut`,
  InDc = `powerPvIn`, OutDc = `powerDcOut` (numbers or numeric strings; null = unknown), voltages null, `lastSource`
  `cloud`. The `timestamp` is the station's local time, read in this PC's time zone (in a repeated autumn hour, the
  reading nearer now).
- AC2a: the cloud has no output mode, so it is derived: `powerGridIn` > 0 = `CLOUD_MAINS`; `powerGridIn` 0 with
  `acSwitch` 1 and `powerAcOut` > 0 = `INVERTER_OUTPUT`; anything else = no mode, an incomplete reading.
- AC2b: a live state with every field null, no numeric SOC, a timestamp out of range, or all zero (SOC 0 %, 0 W in,
  0 W out) is no live state, so a false 0 % never reaches the levels or a stored row. Then the newest point of today's
  history power curve (section 4a) is the reading, `lastSource` `cloud-history`, while it is at most
  `cloudHistoryMaxAgeMin` old; its mode is derived as in AC2a. At most one history call every 5 minutes, counted in
  the back-fill's daily cap. An older or missing point, a failed call or the cap reached is a miss. The "no live
  state" line is logged once until a live state comes back; the history's outcome once each time it changes.
- AC3: a `msgCode` other than 0, no station of `type` in the device list, an HTTP failure or timeout (15 s; a
  request with no answer at all is sent once more), or a missing or unreadable token file is a miss with one log line.
- AC4: when the access token expires within 3 days or the API answers `msgCode` 805, the reader refreshes once
  (`POST https://sso.bluettipower.com/oauth2/token`, `grant_type=refresh_token`), writes the token file atomically
  and retries once. A failed refresh after 805 is a miss; a failed early refresh keeps the current token and the
  next early refresh waits an hour.
- AC4a: a refreshed pair that cannot be written is used from memory and written again on every poll until it is;
  a pair the server issued is never thrown away. A token file that is not JSON is logged as such.
- AC5: the client only GETs the device list and POSTs the live-state, token and history endpoints (section 1).
- AC6: every accepted reading carries its source (`bluetooth`, `cloud`, `cloud-history`); each accepted cloud
  reading and the first Bluetooth one after it are logged with it; `/api/health` gives `lastSource`.
- AC7: cloud readings go through the same plausibility and agreement rules (the cloud may repeat a cached value).
- AC8: logs and errors never contain the access or refresh token.
- AC9: off mains (InAc 0) with OutAc >= `staleLoadW` and a cloud SOC unchanged for `staleAfterMin` since the cloud
  first gave it, the reading is a miss logged as "cloud reading stale" (a frozen cache while the device shows online).
- AC10: a refresh answered HTTP 400, 401 or 403 is a refused grant: state `sign-in-needed`, the refused refresh token
  is not sent again for 6 hours, and while the access token is known dead (805) no device-list call goes out either.
  A new token (sign-in or `--cloud-login`) ends the hold at once. Other failures (429, 5xx, no answer) are retried.
- AC11: under `auto` a healthy Bluetooth never calls the cloud, so the token could lapse unnoticed. Once an hour,
  while the source allows the cloud and a token file exists, a pair issued a day ago or more, or expiring within 3
  days, is refreshed. A refusal turns the state to `sign-in-needed`; another failure shows as `error`.

### 4a. Cloud history back-fill

The cloud keeps the station's own 5-minute power curve. While the source allows the cloud and the token file exists,
the service fills the gaps of `station.db` from it (`cloudHistory` false = off).

- Read only, enforced in code: `CloudHistoryReader` POSTs only to `getDevicePowerStatistics` and
  `getDeviceEnergyStatistics` under `/api/bluiotdata/dashboard/v2/` on `gw.bluettipower.com`; any other path throws
  before a request is built. The body holds only query fields. Its connection pool is its own and follows no
  redirect (a 3xx is a failed call). Neither the token nor the serial is logged.
- A gap is 10 minutes or more with no accepted reading in the last 30 days, from the oldest stored row on, up to 10
  minutes ago. Each local day touched by a gap is asked once, newest first.
- Each 5-minute point becomes one accepted row with reason `cloud history` (SOC, AC in and out, DC in and out;
  voltage, frequency and mode null), only where no accepted row lies in its 5 minutes. Rows are only inserted, never
  changed. A point with no SOC above 0 is skipped. Once raw rows fold into 5-minute aggregates (section 15) the mark
  is gone.
- A day the cloud keeps only hourly is skipped (the first two readable labels are not 5 minutes apart). On the
  autumn clock change the repeated hour's labels are the summer hour first, then the winter hour; labels in the
  skipped spring hour are dropped. No point is invented.
- A day asked an hour or more after it ended is done (meta `cloudHistoryDone`); today is asked again while its gap
  stays.
- Runs 2 minutes after start, then once a day, and after an accepted reading ends a gap of 10 minutes or more. At
  most 4 calls a run and 40 a local day (meta `cloudHistoryCalls`, counted before each call).
- On `msgCode` 805 the token is refreshed once a run and the day asked once more within the caps; a second refusal
  ends the run.
- It runs on its own task: a failed call is a log line, never a poll's fault. It shares the cloud reader's lock for
  the token and the serial (the serial it finds is kept in meta `cloudHistorySerial`), so a run that reads the device
  list or refreshes the token can hold a cloud read for those calls. A Bluetooth read never waits for it.

## 5. Mains, eco and manual control

This section is the legacy engine (`modes.engine` = `legacy`). Under the default `v2` engine the Auto rules of
section 12 choose the mode; the mains timers below still confirm the cut, which those rules and the battery levels
read.

- **Mains lost:** no AC input for `mainsLostSec` in a row across accepted readings -> the cut begins and station eco
  is switched on. **Mains back:** AC input held for `mainsBackSec` -> every mode is lifted (`off`). Misses,
  incomplete and implausible readings neither advance nor reset these timers.
- The lift on mains back lifts only modes set before mains was first seen back (flag `since` earlier); a mode the
  user sets during the confirmation or later is not lifted until the next mains change. The lift is retried until
  `off` exits 0.
- **Manual control:** Auto, Eco and Max on the page (`POST /api/mode`). A choice holds until the next observed mains
  change or Auto; a gap reset that finds mains unchanged is no change. Max during a cut = the lift. The POST saves the
  choice and answers 202; the service loop applies it with `powerModeTimeoutSec` and the action backoff (section 6).
- An observed mains change drops a page choice whose command has not run yet (event `mode-dropped`, saved as
  `modeError`).
- **Emergency hold:** Max never lifts a station emergency. A hold = the flag says `emergency`, or the emergency level
  was reached in this cut. Max or Eco pressed during a hold is refused (409). A choice waiting in the loop re-checks
  the hold on every attempt; when it holds, the command is dropped, the choice falls back to Auto and event
  `mode-refused` is stored.
- While the flag is unreadable a page lift (Max, or Auto lifting the page's Eco) is not run; it stays pending with
  the normal backoff.
- The latest press wins: a press that replaces one not yet run stores `mode-superseded`. The last refusal or failure
  (`modeError`) is saved with the state and shown until the next mode change.
- An applied page choice reads the flag again at once, so `/api/now` shows what the command wrote.
- Auto after a pinned Max or Eco runs no command: the battery rules decide at the next reading, which the loop takes
  at once.
- While mains is off and the cut has not begun, `/api/now` `ecoAt` gives first-off + `mainsLostSec`. It is null when
  no eco is pending: mains on or unknown, the cut begun, the station silent, the flag not normal, Max chosen in this
  outage, an emergency-level streak running, or the last reading older than `maxGapSec` or ahead of the clock.
- A poll or mode command that holds the station gate for more than 20 s logs a `slow:` line. Flag reads (under the
  mutex, up to 5 s) run on their own thread.

## 6. Battery levels and hibernate

| Level | Key | Effect |
|---|---|---|
| emergency | `emergencyAt` | station emergency (`on`); level `low` |
| stop | `stopAt` | level `critical` (an event; the flag hears it through the battery report) |
| warning | `warnAt` | hibernate warning; level `warning` (flag word `hibernating`, saved name `Hibernating`) |
| hibernate | `hibernateAt` | `shutdown /h` |

The levels apply always, manual Max included. On battery the reading goes into the flag (`battery`) at each level
change and at least every `batteryRefreshSec`, best effort.

- **Raised by the drain.** At a high drain the battery could pass the warning and `hibernateAt` within a few polls,
  so each poll raises the levels by the measured drain `d` (section 7, capped at 120 %/h; `poll` = the larger of
  `pollSec` and `fastPollSec`): `warnAt` to at least `hibernateAt + d x (hibernateAfterWarnSec + 2 polls)`, `stopAt`
  to at least the warning + `d x 2 polls`, `emergencyAt` to at least the stop + `d x 2 polls`, rounded up to a tenth,
  at most 10 points over the configured value and at most 100. `hibernateAt` never moves. No measured drain (on
  mains, too little data, stale or not a positive number) = the configured levels. A reading at or under a
  configured level always counts toward it. `/api/now` `effectiveThresholds` gives the levels in force.
- **Agreement:** two accepted readings at least `agreeSec` apart with no accepted reading between them that
  contradicts the level. Misses and rejected readings neither break nor count; only a gap reset does. An accepted
  reading with mains unknown is not on battery: it breaks every level's agreement.
- One new level per poll, each in a later poll than the one before.
- A level counts as fired only after its action succeeded; a failed action is proposed again on the next qualifying
  poll with every precondition re-checked. A failing action backs off 1, 2, 4, 8 minutes, then every 10 minutes,
  each failure an event.
- **Hibernate** only on battery, at or under `hibernateAt`, at least `hibernateAfterWarnSec` after the warning in the
  same cut, and only after a fresh reading taken right before acting agrees. No fresh reading, no hibernate.
- Hibernation depends only on readings and time, never on a flag write. The warning counts as given when attempted;
  emergency and stop failing hold back neither the warning nor the hibernation.
- **Rebase rule.** While a rebase is remembered in the cut, hibernate needs `rebaseHibernateConfirmSec` of agreeing
  accepted readings at or under `hibernateAt`, counted from the later of the streak's start and the latest rebase. A
  streak from before a rebase does not keep the normal rule. Each reading adds the time since the one before it, at
  most twice the poll interval, so a forward clock step or a run of misses never makes it earlier. A backward clock
  step moves a remembered rebase to the new now. Known cost: a station that keeps rebasing never hibernates the PC.
- While the rebase rule holds hibernate back, one `hibernate-delayed` event is stored per rebase with the earliest
  allowed time; `/api/now` `hibernateDelayedUntil` and a note show it, and the warning says the PC hibernates only
  after the confirmation.
- **Resume:** an accepted reading more than 2 minutes after a hibernate action means the PC resumed; the hibernate
  level re-arms with all its preconditions. Still at or under the level on battery, the PC hibernates again.
- **Kill switch:** under `v2` the `autoHibernate` setting (section 12); a `no-hibernate` file found at start is carried
  into it (`autoHibernate` false) and renamed `no-hibernate.migrated`. Under `legacy` a `no-hibernate` file in the
  data directory turns hibernation off, checked on every poll. `--dry-run` logs every command and hibernation instead
  of running it.

## 7. Battery time

The hours shown (`hoursLeft` to empty; the DTEK module's battery time to `hibernateAt`) inform only. The levels read
one number beside them, the measured rate, and only to rise (section 6).

- **Measured rate:** the SOC drop over the trailing run of accepted readings off mains within `rateWindowMin`. The
  run ends at a reading on mains, one without SOC, or a gap over `maxGapSec`; the span runs from its first SOC step to
  its last. It needs at least `rateMinDrop` points over `rateMinSpanMin`.
- **Formula fallback:** with less data, a SOC rise in the run, or a rate that is not positive: capacity x SOC x
  efficiency / current load (`capacityWh`, the learned efficiency, else `efficiency`).
- With a rate: hours = (SOC - target) / rate x mean output over the span / current output, so a changed load
  changes the time at once. At most 30 days.
- **Learned efficiency:** a clean window (no DC input, no SOC step over `socJumpFloor`) measures mean output / (rate x
  capacity); the first is taken whole, each later one moves the value a quarter of the way, at most once per
  `rateWindowMin`; bounded 0.4-0.95 and at most `efficiency` + 0.05; kept in meta `runtimeEfficiency`.
- **Fitted model** (`fittedRuntime`), used first once it rests on at least 2 hours of clean data and while there is
  no DC input: drain = P0 + (1 + k) x output, fitted by least squares over the clean stretches of every stored cut.
  The prior (no idle draw, `efficiency` for every load) weighs two hours of discharge, so a new station starts at its
  configured efficiency and its own data take over. A negative idle draw refits with none. Hours = (SOC - target) /
  drain at the current load.
- **Range:** P10-P90 of the drain error over leave-one-cut-out forecasts at 120 minutes ahead (else 60, else 30),
  published only with at least 30 of them from at least 5 cuts; `hoursLeftLow` / `hoursLeftHigh`. Refitted from the
  newest 20 000 rows at most every 15 minutes while `/api/now` is asked, never on the poll; kept in meta
  `runtimeModel`.
- **Eco profile:** from the cuts with an `eco-on` event the model learns the delay to eco and the load ratio after it
  (shrunk toward 1 with two cuts of prior weight). In a new cut where eco has not come yet, the load from the median
  delay on is taken as current x (1 + share x (ratio - 1)). Once the cut's `eco-on` is logged, or with no eco
  history, the current load stands.
- `/api/now` gives `runtimeSource` (`model`, `rate`, `learned`, `config`), `ratePerHour`, the range and
  `learnedEfficiency`. The method was chosen by a backtest over stored cuts (`tests/BluettiWatcher.Tests/Backtest`,
  run on request with `BW_BACKTEST_DB` and `BW_BACKTEST_OUT`).

## 8. Wake during a cut

A PC hibernated in a cut stays asleep after mains returns until someone wakes it. The service can wake it through the
scheduled task `wakeTaskName`. Off by default (`wakeEveryMinutes` 0) until the user has seen this PC wake from
hibernation by a timer (README).

- a. **Arm:** with `wakeEveryMinutes` set (10-1440), just before the service hibernates the PC it arms the wake task
  that many minutes ahead (`wake-armed`). At or under `wakeFloorPercent` (unset: `hibernateAt` - 5, at least 0; set:
  bounded to 0..`hibernateAt`; 0 = no floor) no wake is armed and one still ahead is turned off (`wake-not-armed`),
  so the wakes do not drain the station.
- b. A wake that cannot be armed never holds a hibernation back (`wake-arm-failed`); arming waits 15 s at most. An arm
  that failed may still have landed: the turn-off is then owed at the next reading on mains.
- c. Any accepted reading on mains turns the wake off (`wake-disarmed`); the PC stays awake.
- d. **Woken by the timer** (e) and still in a cut: the first accepted reading on battery at or under `hibernateAt`
  hibernates the PC again at once, after the usual fresh reading. A missing, rejected, higher or on-mains fresh
  reading refuses it; one on mains ends what the wake owed. The resume rule of section 6 does not apply here. The
  next wake is armed then.
- e. The timer woke the PC when the first poll more than 2 minutes after the hibernation falls between 2 minutes
  before and 10 minutes after the armed time; from then until a reading on mains a hibernation is owed. With no
  accepted reading for `wakeReadSec` (60-3600), the PC hibernates again without a fresh reading and the next wake is
  armed. This and (d) are the only hibernations outside section 6's rules. A PC woken by hand is never hibernated
  without a reading. The kill switch and a dry run hold both.
- f. The first accepted reading on mains after a service start turns the wake off whatever was saved; the armed
  state is saved in meta `wake`.
- g. A failed turn-off is retried with the action backoff, each failure a `wake-disarm-failed` event; `/api/now`
  `wake` and a note show it until it succeeds.
- h. The task operations are pwsh `Set-ScheduledTask` (time, enabled; a task without WakeToRun is refused) and
  `Disable-ScheduledTask`, windowless, under `powerModeTimeoutSec`; a dry run logs them only.
- i. The installer registers the task (`tools/wake-task.ps1`) from XML through the Task Scheduler COM API: disabled,
  WakeToRun, one time trigger, a no-op action (System32 `conhost --headless cmd /d /c exit 0`) as the user,
  interactive token, least privilege; Administrators, SYSTEM and the user have full control so the unelevated service
  may set it. It reads the task back and checks every DACL entry against a whitelist (allow entries only; full
  control for those three on the task itself; any other account read only); anything else refuses the task, naming
  the entry. The name is 1-100 characters of letters, digits, space, `.`, `_`, `-`, never `BluettiWatcher`; an
  existing task of that name is replaced only when it reads back as one this installer registers.
  `install.ps1 -RemoveWake -Apply` removes it.

## 9. Actions

Config `actions` maps a state to a command line: `eco`, `emergency`, `stop`, `hibernate-warn`, `normal`, and the
alert `grid-voltage-high`. The command runs when the service enters that state.

- The line is an executable and its arguments, started directly and windowless: no shell, so a pipe, a redirect or a
  built-in needs `cmd /c ...`. Its environment carries the four `POWER_MODE_*` values.
- Timeout `actionTimeoutSec`. The exit code and the first 300 characters of output are stored as an event. A missing,
  failing or hanging command never blocks or changes a decision.
- An action fires once per entry into a state per cut; the memory resets when mains returns. `normal` fires once per
  lift. The page's Max and Auto-lift fire `normal`, the page's Eco fires `eco`.
- Under `v2`, `actions.eco` and `actions.normal` become the named commands `eco` and `normal`, run by the built-in Eco
  mode's command pair on entering and leaving it (section 12); the station's own eco and mains-back no longer fire
  them. The other keys fire as above.
- `grid-voltage-high` fires when the grid input voltage (AC200PL over Bluetooth) goes above 253 V, with an event of
  the same kind, and re-arms only under 250 V. The memory is saved with the state.
- A V2 station's new warning or fault, a code that clears, and a temperature above `stationTempHighC` (re-armed 5 °C
  under it) each write an event (`station-warning`, `station-fault`, `station-alarm-cleared`, `station-hot`).
- A dry run logs the actions and their environment instead of running them.

## 10. Page and times

- One HTML file embedded in the exe, no external libraries. The battery ring takes its colours from the levels in
  force. Charts span 1 hour to 30 days. A battery card sums the last 30 days of cuts. The layout fits a phone, Full HD
  and 2K.
- The DTEK banner checks the plan against the last reading (at most 10 minutes old): power back before the planned
  end shows green, an outage the schedule does not have shows amber. Past hours of today show what happened, hatched
  over the plan: each minute takes the state of the last accepted reading while the next comes within 10 minutes,
  else unknown.
- English and Ukrainian: the browser's language picks (`uk*` = Ukrainian), the header switch remembers the choice in
  the browser. Prose the page shows comes with a stable code it translates by (`notes`, `ecoNote`, `modeErrorNote`,
  `dtekWatcher.notes` as `{code, text, args}`; `modeCode`; the mode POST's `code`); an unknown code shows the
  server's English text. Logs stay English.
- The rejected-reading note stands only while the newest reading is the rejected one, at most 6 hours.
- Times shown to people use the PC's local time zone; API times are ISO 8601 with the local offset. DTEK schedule
  math runs in Europe/Kyiv time, because the source publishes Kyiv wall time.

## 11. DTEK module (planned outages, Kyiv region)

An optional module that reads the distributor's planned-outage schedule for the user's address and shows the next
outage, the expected return and whether the battery lasts until then. Code in `DtekWatcher/`; the host knows it only
through `IDtekWatcher`: `RunAsync`, `AfterPoll(state)` (a cut began, mains is back) and `View(...)` (the `/api/now`
object). The rule numbers below are referenced from the code; keep them stable.

1. The module informs; it controls nothing.
2. The schedule never influences eco, max or hibernate. No code in `Decision/` reads schedule data or the module's
   config; a test pins it. (The `cutWithin` Auto rule of section 12 reads only the module's next outage.)
3. **Queue search.** Each real cut (first reading off mains to first reading back on) is compared with the planned
   windows of the house's queues. Two counted cuts in a row for one queue fix it; until then all the house's queues
   are shown. A later cut that matches other queues and not the fixed one opens the question again. An unplanned cut
   neither counts nor contradicts. A house with one queue needs no matching. A fixed queue no longer among the
   house's queues is dropped.
4. **Requests:** one `HttpClient`, HTTPS on the default port, no proxy, exactly two hosts: the dataset's (rule 15)
   and the site's (`dtekWatcher.host`). Redirects only within the asked host, at most 3; anything else is refused
   before a request leaves. Platform TLS validation. 30 s per request; a body over 4 MB is refused. The site's page
   is read line by line and only its token and schedule line are kept.
5. **Cadence:** the dataset every 30 minutes; the site per rules 17 and 23. The address request rides on a site read
   (it needs the page's token and cookies).
6. **On failure** the schedule saved on disk stays shown with both its times (rule 16) until the days it covers end,
   then "no schedule". A challenge page of the site's shield is `blocked`, an answer that does not fit is `format`;
   neither is ever read as "no outages".
7. During a cut the page shows the outage kind and expected return the site gave, only when the site answered during
   this cut.
8. **Config** (`dtekWatcher` in config.json; keys in configuration.md): off by default, creating no client and making
   no request. `site` false (default) = the dataset only; true opts in to reading the distributor's website, which is
   subject to its terms. A dataset address that is not plain https or a host that is not a plain host name turns the
   module off with the reason in the log and the note.
9. **Matching:** a cut matches a queue's window when both its start and end fall within 20 minutes of the window's
   borders. Only a cut that matches exactly one of the house's queues counts. Cuts with no saved schedule for their
   time do not count, nor do cuts whose start or end fell in a gap of readings over 5 minutes. "Possible" cells are
   never planned cuts for matching.
10. **Read errors:** per source one `dtek-watcher-failed` event per failure streak (source, class `network`,
    `blocked`, `format` or `failed`, detail), one `dtek-watcher-ok` on recovery, and a note. Retries follow the
    source's normal cadence.
11. **Battery lasts:** the hours-left estimate at the current load against the expected return (while the queue is
    unknown, the latest return among the house's queues); `unknown` when either side is unknown.
12. **Stored** (meta `dtekWatcher`): the schedule in use (today and tomorrow, source, both times), the dataset's
    validator, the site's last address answer, the match counters, the fixed queue, the manual queue's hint
    (rule 21), the running cut's start, and per source the last read time and failure. No schedule history.
13. The module never talks to the station.
14. **Data.** Days (today and tomorrow, Kyiv time) of queues `GPV1.1` .. `GPV6.2`, 24 hour cells each; hour N runs
    from (N-1):00 to N:00. Cells: `yes` on, `no` off, `first` / `second` off in that half hour, `maybe`, `mfirst`,
    `msecond` possible; anything else or a missing hour is unknown. Touching windows are one outage, across midnight
    too. Cell times are Kyiv wall time turned into instants by the zone's rules (a clock-change day is 23 or 25 hours;
    the skipped hour has no time, the repeated hour is off both times). `/api/now` carries one `dtekWatcher` object
    (fields in api.md); the page block is hidden while the module is off, and every text from the site is escaped.
15. **Primary source:** the public dataset
    `https://raw.githubusercontent.com/Baskerville42/outage-data-ua/main/data/kyiv-region.json` (MIT; collected from
    the distributor's sites). Its `fact` is the site's `DisconSchedule.fact` unchanged; its `lastUpdated` is when it
    was collected. Read with `If-None-Match` while a schedule is held; 304 keeps the schedule and its collection
    time.
16. **Freshness.** (a) The page and `/api/now` show both times (the distributor's update time of the schedule in use
    and its collection time) and the source in use. (b) The schedule in use has the newest distributor update time
    among what the sources returned; the same update collected again only moves the collection time; with unreadable
    update times the later collection wins. (c) State `stale` when the newest collection is older than
    `staleAfterMin`, with a note; the covered days stay shown. (d) A `dtek-watcher-changed` event when a shown
    queue's windows change between two schedules on a day both have.
17. **Site, best effort:** one attempt per hour on mains, one per 15 minutes during a cut. A challenge answer is
    state for that source only (`siteState`), raises `dtek-watcher-failed` at most once per streak and never changes
    the module's `state`. After 24 challenges in a row the site is tried once every 6 hours until it answers
    anything else.
18. **The house's queues:** `dtekWatcher.queues` when set; else the ones the site's address answer ever named
    (saved); with neither, state `none` and a note asking for `queues`.
19. **Containment.** The module can never hurt the host, and nothing untrusted is stored or used unbounded.
    - A total guard: no exception from building it, loading its state, `AfterPoll`, the view or its run reaches the
      poll loop or `/api/now`. A fault is logged once per distinct message per hour and raises one
      `dtek-watcher-failed` (`module: <type>`) per streak; the view is then `state: none` with a note. A failed save
      keeps the state in memory. A saved state that is damaged, wrong-typed or out of bounds starts the module fresh.
    - Every outcome of a read, an unexpected exception included, records the attempt and obeys the cadence. The
      dataset's validator is stored only for a body that was taken in.
    - Bounds (an answer outside them is refused whole as `format`, the held schedule stays): at most 4 days and 24
      queues a day; queue names `GPV<1-2 digits>.<1-2 digits>`; hours 1-24; no invalid string; a day key that is not
      a Kyiv day start (within 2 hours) or lies more than 2 days back or 7 ahead is refused; an update time more than
      24 hours ahead is refused. A collection time more than 10 minutes ahead is taken as now. The `update` text is
      cut to 40 characters, the site's outage texts to 120, control characters removed.
    - An unreadable update time against a held readable one: the candidate is taken only when its content differs
      and it was collected later; the note says so.
    - `dtek-watcher-changed` only when a shown queue's planned-off half hours differ (never for a text, an order or a
      possible cell), at most one per queue and day in 10 minutes; `dtek-watcher-failed` / `-ok` at most one pair
      per source an hour.
    - A finished cut is matched against the schedule held when it began; when the plan changed during the cut, the
      cut neither counts nor contradicts.
20. **Manual queue** (`dtekWatcher.group`) is a hint, never a permanent override (the distributor moves houses
    between queues). It is followed from the start; the queue search keeps running. Every counted cut is also
    compared with it: a cut that matches exactly one other queue contradicts it; two such cuts in a row for the same
    queue refute it (one `dtek-watcher-queue-changed` event) and the detected queue is followed from then on. A cut
    that matches the manual queue ends the row, as does one that fits several queues without the one counted; an
    unplanned cut leaves the row. A manual queue that is not among the house's queues is followed all the same and
    the note says so.
21. The hint is stored with the module's state, tied to the config value: the same value after a restart stays
    refuted; an edited value is a fresh hint. A row begun before a restart goes on after it.
22. `/api/now` `dtekWatcher` gives `queueOrigin` (`manual`, `detected`, `manual-refuted`, or "") and
    `queueContradictions`; the page shows them beside the queue.
23. Outside a cut the site is tried at most once in 60 minutes on its own clock, whatever the last attempt ended
    with; no retry. During a cut rule 17 stands.
24. **Overdue.** The window a cut belongs to is, among the shown queues, the first planned window that holds the
    cut's start (from 20 minutes before it), stretched over that queue's later windows that begin (less 20 minutes)
    by now or by the cut's end; across queues, the latest end. It is read in the schedule in use, with days it no
    longer has taken from the schedule held when the cut began.
    - During a cut, when no planned window covers now and the cut's window has ended, the cut is overdue from that
      end; `return` is then the site's end or null. A window that covers now ends the overdue state.
    - A cut that began in no planned window is never overdue. Overdue is computed from the saved running cut on each
      view, so it survives a restart.
    - The battery time is now plus the hours the battery lasts at the current load down to `hibernateAt` (none when
      the estimate is missing or not positive; at most 30 days).
25. **Lateness.** Each finished cut whose ends are both known, taken while one queue is shown and belonging to a
    planned window, is recorded: start, end, queue, the window's Kyiv date, and actual minus planned (whole minutes)
    for start and end, against the window as it stood at the cut's end and, when there was one, at its start. The
    usual lateness is the median of the end deltas, given from 3 cuts. Stored in meta `dtekWatcherLateness`: the last
    60 cuts; damaged lists are emptied and out-of-bounds entries dropped.
26. **Plan edits during a cut:** each schedule taken in during a cut that moves a shown queue's planned-off half hours
    counts once; the count lives with the running cut.
27. `/api/now` `dtekWatcher` gives `overdue`, `overdueMin`, `plannedReturn`, `batteryUntil`, `planChanges`,
    `lateBackMin`, `lateCuts`; the page shows the struck-through planned return and "overdue N min", the battery
    time, the plan changes, and from 3 cuts the usual lateness.
28. **Queue sets.** `dtekWatcher.group` may be an array: held as one name joined with `+` in the configured order,
    blanks and repeats dropped; at most 4 queues of at most 16 characters, else the module is off. A changed order is
    a fresh hint. The set's plan is the union: a half hour is off when any member's is, else unknown, else possible,
    else on; a day missing a member has no union. Everything that reads the followed queue reads the union.
    `/api/now` adds `queueSet`, `nextQueues` and per window `intervals`. The hint and lateness work on the union
    (lateness entries say `union: true`). The page shows `1.1 + 2.1`.
29. **Short cuts.** A cut shorter than `minCutMin` (0-1440, else the module is off; 0 = every cut counts) is a blip: no
    lateness entry, not counted by the queue search or the hint. It stays in the history and on the chart, and a
    running cut is overdue as usual: the minimum applies when the cut ends.

## 12. Power modes (`modes.engine` = `v2`)

The default engine (`legacy` keeps section 5). Code in `Modes/`; routes in [api.md](api.md).

1. **Mode:** a named list of actions (`{ typeId, enabled, enforce, settings }`) plus the flag word it maps to
   (`normal`, `eco`, `emergency`). Exactly one mode is active. Built-ins `normal`, `eco`, `max` are editable, never
   deletable; a missing one is re-added at start. A mode id is 1-32 of `a-z 0-9 -`; `auto` is reserved.
2. **Action module** (`IActionModule`): one capability with a descriptor (type id, settings JSON Schema, UI hints,
   discovery lists, restoration class, needs-admin, defaults) and an adapter that discovers choices, validates
   settings, expands them into resource intents (one per field, e.g. `gpu/0/powerLimit`), and reads, compares and
   writes one resource, answering with the value read back. Modules are registered by hand in `ModuleCatalog` (no
   reflection under NativeAOT); the registry refuses a duplicate type id or defaults that fail their own schema.
   Settings are checked against the schema and the module's own checks before they are saved.

   | Type id | Restoration | What it sets |
   |---|---|---|
   | `windows.powerPlan` | reversible | the active power plan |
   | `windows.cpuPolicy` | reversible | max processor state and boost mode of a plan (a GUID or `active`), on mains and on battery |
   | `display.offTimeout` | reversible | the active plan's display-off timeout, on mains and on battery |
   | `gpu.powerLimit` (broker) | reversible | an NVIDIA board power limit: `percent` of default, `watts`, or `limit: "min"`; the broker re-checks min..max |
   | `display.refreshRate` | reversible | a monitor's refresh rate (the form warns it can crash a browser's GPU process) |
   | `display.brightness` | reversible | DDC/CI monitor brightness |
   | `nvidia.profile` (broker) | compensatable | a Profile Inspector `.nip` imported; customised profiles exported first and imported back on leaving |
   | `gpu.idleWatcher` | token | writes nothing; logs the processes holding a GPU in P0 at high clock with almost no load, once per idle stretch |
   | `service.state` (broker) | reversible | a service's running state and start type |
   | `scheduledTask.state` | reversible | a task enabled or disabled; a task that refuses the user goes through the broker |
   | `process.close` | irreversible | apps closed (windows asked to close, killed after a grace only when `kill` is set); system processes and this service are never closed |
   | `windows.phoneLink` | irreversible | Phone Link closed |
   | `command.pair` | compensatable | a named command (`modes.commands`) on entering, another on leaving |
   | `rgb.lighting` | compensatable | RGB lighting off through OpenRGB's SDK server (127.0.0.1:6742) |

   - `nvidia.profile` reads through a broker export, so it is read and written on entering and leaving, not every
     poll.
   - `command.pair` and `rgb.lighting` cannot read what they changed: their read is their memory of what they ran,
     recalled from the journal after a restart (`IRecallsWrites`), so leaving still runs the leave step.
   - `rgb.lighting` turns the listed devices (none = all) off and reads their colours back; leaving loads the OpenRGB
     profile the settings name, or leaves the lighting off. When no server answers, a write starts one headless,
     windowless `OpenRGB.exe --server --noautoconnect` under the user and leaves it running; the catalogue, the page
     and a disabled action never start it. OpenRGB.exe comes from `modes.openRgbPath` or the install folders, never
     from a mode. No admin rights (RAM lighting over SMBus is not covered).
3. **Selection.** A manual pick holds until Auto. Auto takes the first rule, in order, whose condition holds:
   `mainsOffFor` / `mainsOnFor` N s (mains as the decision confirmed it), `socBelow` X %, `cutWithin` N min (the DTEK
   module's next planned outage; 0 during a cut; never with the module off or failing), `timeWindow` from-to local
   time (may wrap midnight). A rule in force keeps matching within its hysteresis; when none matches, the last mode
   stays. Unknown inputs match nothing. The default rules reproduce section 5: `eco` after `mainsLostSec` off,
   `normal` after `mainsBackSec` on.
4. **Battery emergency** (section 6) overrides selection: the mode's actions stay, and the engine never writes over
   the flag's `emergency` while the emergency level holds in the current cut; once mains is back the engine writes
   the mode's word over it.
5. **Reconciler.** Each poll, and at once after an API change, for every resource of the active mode: read; a failed
   read writes nothing and claims nothing (`failed`); equal to the target = `satisfied`. Otherwise
   `Prepared(before, target)` is journaled, the module writes and reads back, `Applied(written)` is journaled and the
   resource is owned.
   - A resource is ours only while it reads what we wrote. One the person changed is let go (`relinquished`, event
     `mode-drift`) and not touched again while the mode stays active, unless its action has `enforce`.
   - Leaving a mode restores each owned resource the new mode does not set to its baseline (the value before the
     first write), only while it is still ours. A failed restore is retried on later polls, at most 3 times, then
     reported (`restore-failed`, event `action-failed`). A resource the next mode also sets keeps its first baseline.
     Irreversible actions are never undone.
   - One action's failure never stops the others. A failed write is retried after 1, 2, 4 ... minutes, at most 30
     apart, alerted once per resource and mode entry (`action-failed`).
   - An API write waits for its transition at most half the HTTP request limit; past it the answer is 202 with the
     view as it stands and the transition runs on.
6. **Journal** `modes-journal.jsonl`: one JSON line per record, flushed before the step it records goes on. Replayed
   at start before any write: an open `Prepared` is settled by reading (target = owned, baseline = dropped, anything
   else = relinquished; an unreadable resource stays open and is never written over); a token resource written
   before the last boot is let go. A torn last line is cut off. Any other unreadable line moves the journal aside
   (`.corrupt-<unix>`), restores nothing, holds `normal` as a manual pick until Auto, and shows the problem in
   `/api/modes` and as event `modes-problem`. Compacted at each start and past 5000 records, keeping what rebuilds
   each owned resource, the last mode entered and the last 200 records; written to a temp file and moved over.
7. **Settings** `modes.json`, versioned, replaced atomically. First start: an install that used the switch before
   (its flag or `power-mode.config.json` exists) is migrated (`returnPlan` becomes Normal's plan, `previous` = no plan
   action; `gpuCap` turns Eco's GPU limit on or off); a fresh install gets the defaults (Eco = Power saver, with its GPU
   limit (`limit: "min"`), a command pair, services, Phone Link and the idle watcher listed but off; Max = High
   performance).
   A file this build cannot parse is moved aside and replaced; one from a newer build is left alone and the defaults
   run in memory.
8. **Flag.** The active mode's word is written through the switch's own flag code (same lock, same format; the
   battery fields and anything an older eco still owes are carried over), so flag readers are unchanged. Under `v2`
   the station's eco and mains-back run no switch command (their events carry the detail `modes v2: the Auto rules
   choose the mode`); the station emergency and the battery report still do.
9. **Broker policy warnings:** a saved mode answers with `warnings` naming its entries the broker policy does not
   allow, each with the elevated command that allows it; the active mode's are listed in `/api/modes` problems.
10. **Page.** A Power modes panel, shown only under `v2` and asking nothing while closed (open: the catalogue once,
    `/api/modes` each minute; `/#modes` opens it): Auto or a mode, the reason in force, a chip per action state, the
    hibernate setting, the mode editor with settings forms generated from the catalogue, and the Auto rules with
    ordering and a per-kind editor. A refused save shows each problem at its field and keeps the edit.

## 13. Admin broker (power modes)

Some actions need administrator rights (a service's state or start type, an admin-owned scheduled task, a GPU power
limit, an NVIDIA profile). The service never runs elevated: it asks the SYSTEM scheduled task `BluettiWatcherBroker`
(no trigger), starts it on demand (`schtasks /run`) and waits up to 10 s for the answer.

- **Folders.** What SYSTEM trusts lives in `%ProgramFiles%\BluettiWatcher\broker`: `bin\` (the copy of the exe the
  task runs), `policy.json`, `profiles\`, `exports\`, `broker.log`. Requests and answers live in their own root,
  `%ProgramData%\BluettiWatcherBroker` (`requests\`, `responses\`), never inside the flag's folder. Both roots are
  owned by Administrators with inheritance cut: Administrators and SYSTEM full control, the user read and traverse;
  in `requests\` the user may also add files (never folders) and read, write and delete its own files. SYSTEM never
  runs an exe the user can replace.
- **Install** (`install.ps1 -Broker -Apply`, elevated; the normal `-Apply` runs it too when elevated, else names it):
  refuses (exit 1) when an existing ancestor of either root is owned by the user or grants the user delete,
  delete-child, change-permissions, take-ownership, add-file or add-folder, or cannot be read; after setting the ACLs
  it reads them back and fails on any difference. The policy's user is the one the logon task runs as, never the
  elevated caller. A broker left under `%ProgramData%\BluettiWatcher\broker` is carried out (policy and profiles only
  when Administrators own them) and removed. Once the broker is registered, the switch's GPU cap tasks
  (`<prefix>GpuCap`, `<prefix>GpuRestore`) are removed; a failed registration keeps them.
- **Request:** `requests\<id>.json` (`{ v, id, op, args, issued }`, id = 32 hex digits, written as `.tmp` and renamed);
  answer `responses\<id>.json` (`{ id, ok, error, value }`), pruned after 10 minutes. Every request is logged to
  `broker.log`.
- **Default deny.** A request runs only when `policy.json` is a plain file owned by Administrators or SYSTEM that no
  other account may write and that parses strictly (any unknown key, wrong type or bad name refuses everything); the
  request file is a plain file (no link, one name) owned by the policy's user or an administrator, opened without
  following links and deleted through the same handle, under 4 KB, well-formed, issued within the last 30 s; its
  operation is known and its arguments are exactly that operation's.
- **Operations:** `service.query|start|stop|setStartType` and `task.enable|disable` for names the policy lists;
  `gpu.query`; `gpu.setPowerLimit` within the GPU's own min..max read at the time; `nvidia.exportProfiles` (the file
  moved into `exports\` and answered with its SHA-256) and `nvidia.applyProfile` for a listed `.nip` in `profiles\` or
  a file in `exports\`. Profile Inspector must sit under Program Files or the broker folder. Nothing from a request
  reaches a shell: names go by their policy spelling, numbers are re-formatted, files are names joined to a fixed
  folder.
- `BluettiWatcher.exe --broker-allow service|task|profile|inspector <name>` (elevated) adds to the policy.
- **Version.** After an upgrade the broker's copy of the exe is stale until the elevated step runs again.
  `/api/health` `broker` names it `missing`, `stale` or `current` all along. A stale broker found at start is checked
  again 10 minutes later (an upgrade starts the service before the elevated step) and only then told: event
  `broker-stale` (a tray notice), once per service version (marker file `broker-stale-noticed` in the data
  directory).

## 14. Tray and notifications

1. **Icon.** One more thread (STA) in the service process with a hidden window and a blocking `GetMessage` loop, no
   timer. Win32 through source-generated P/Invoke (`Shell_NotifyIconW` with `NOTIFYICON_VERSION_4`,
   `TrackPopupMenuEx`, `CreateIconIndirect`), no WinForms or WPF. On by default (`tray`), only in an interactive
   session; `--no-tray` turns it off.
2. The service pushes a snapshot after each poll and each page or tray choice: charge, mains, watts, mode, time left,
   the confirmed cut. A reading older than `maxGapSec` is stale: grey icon, a tooltip with its time and no watts.
3. The icon is drawn at run time at the taskbar's size for the system DPI: a dark tile, the rounded charge in a 3x5
   pixel font, a bar filled to the charge, green on mains, amber on battery, grey unknown. It is rebuilt only when
   digits, colour, staleness or size change.
4. **Menu:** Auto and the modes as one radio group, "Open panel" (the page through the shell), "Exit tray" (the icon
   and its notifications go until the next start; the service runs on). Under `v2` the modes of `modes.json` in
   order, held until Auto; under `legacy` the page's Auto / Eco / Max. A choice runs off the tray thread.
5. Explorer's restart (`TaskbarCreated`) adds the icon again, as does a taskbar that appears after the service
   started.
6. **Toasts.** WinRT toasts under the app id `BluettiWatcher`, which the installer sets on the Start-menu shortcut
   (`tools/shortcut.ps1`); Windows keeps them in the notification centre. A click opens the page by protocol
   activation (no COM activation server). Windows silently drops a toast for an app id no shortcut names, so each
   notice first looks for `shell:AppsFolder\BluettiWatcher`; without it, or when the toast fails, the notice is a
   shell balloon (`NIF_INFO`). Each change of the way is logged once. With the icon closed there are no notices.
7. **Sources and groups** (switched in `notifications.json`, `PUT /api/settings/notifications`, all or nothing,
   applied to the next notice):

   | Group | Source |
   |---|---|
   | `mains` | the confirmed cut starting or ending between two snapshots (never the first after a start) |
   | `modeChanges` | events `eco-on`, `mains-back` (legacy engine only), `mode-changed`, `mode-dropped` |
   | `lowBattery` | events `emergency-on`, `stop`, `hibernate-warning` |
   | `failures` | events `action-failed`, `mode-refused`, `broker-stale` |
   | `faults` | event `grid-voltage-high`; station faults and warnings |
   | `overTemperature` | station temperature above `stationTempHighC` |

   Events reach the tray through a hook on the store, so every writer's events count. Station alarms come as the
   whole active list after each poll; an alarm notifies when it appears, not while it stays.

## 15. Storage and files

- **Database** `station.db` in the data directory, through Windows' `winsqlite3.dll` (3.24.0 or newer, else exit 3).
  Tables: `reading` (one row per reading, accepted, missed or rejected with its reason), `agg5` (5-minute
  aggregates), `event`, `meta` (the decision state and module state). Raw rows are kept `rawRetentionDays`, then
  folded into 5-minute aggregates kept forever. Times are Unix seconds.
- A new file is created with 1 KB pages in WAL with `synchronous=NORMAL`: commits are not flushed, checkpoints are,
  and the WAL file is cut back to 1 MB after each. An existing file keeps its page size and journal mode (no rebuild
  or VACUUM: a rebuild in place corrupts a file whose `-wal` another process holds open). Opening waits on a busy
  file.
- **Log** `service.log`, rolled to `service.log.1` past 1 MB. A line identical to the one before is folded into
  `repeated N times since HH:mm:ss: <line>`, written before the next different line, by a timer at most 15 minutes
  after the run began, and at a clean stop. A distinct line is never lost.
- **Data directory files** (default `%LOCALAPPDATA%\bluetti-watcher`):

  | File | Owner |
  |---|---|
  | `config.json` | the user; read at start |
  | `station.db`, `service.log` | the service |
  | `cloud-token.json` | sign-in and the cloud reader (`cloudTokenPath` may move it) |
  | `reading-source.json` | the page's reading source choice |
  | `modes.json`, `modes-journal.jsonl` | the modes engine |
  | `notifications.json` | the notification switches |
  | `no-hibernate` | legacy kill switch (migrated under `v2`) |
  | `broker-stale-noticed` | the service version a stale broker was told for |
  | `power-mode\` | the installed switch scripts |
  | `bin\`, `bin.new\`, `bin.old\` | the installer (section 17) |

- The flag file defaults to `%ProgramData%\BluettiWatcher\power-mode.json`; the broker's folders are in section 13.
- `cloud-token.json`, `reading-source.json`, `modes.json` and `notifications.json` are written to a temp file and
  moved over the old one; the journal is appended and flushed (section 12).

## 16. HTTP and its security model

- A minimal HTTP/1.1 server on a `TcpListener` bound to `127.0.0.1` only (`port`): no URL ACL, no admin rights, no
  TLS. One request per connection; headers up to 16 KB (else 431); a body up to 4 KB, 64 KB on the power-modes routes
  (else 413); the request head within 5 s; 10 s per connection; at most 32 connections at a time. A handler exception
  answers 400 (bad input) or 500 and is logged.
- **Host check on every request:** `Host` must be exactly `127.0.0.1:<port>` or `localhost:<port>`, else 403. This
  stops DNS rebinding.
- **Write guard on every non-GET route** (mode, modes, return plan, reading source, cloud sign-in and sign-out,
  notification settings and test): the header `X-Station: 1` (a custom header a foreign page cannot send without a
  CORS preflight, which is never allowed) and an `Origin` that is absent or the service's own; else 403.
- Fixed response headers: `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`
  and a Content-Security-Policy of `default-src 'self'` with inline script and style allowed and
  `frame-ancestors 'none'`.
- The page is embedded in the exe and served with an ETag (304 on a reload).
- Any local process can read the API; the guards protect against browsers, not against software on the PC.

## 17. Upgrade and swap

`install.ps1` is a dry run by default and changes nothing without `-Apply`. The swap logic is `tools/swap.ps1`
(tested by `tools/test-swap.ps1` with fakes).

- The live `bin` is never a build output: the new exe is copied or published into `<data dir>\bin.new` while the
  service runs, with its service twin written beside it. A failure there changes nothing.
- The Start-menu shortcut (and its app id) is written before the swap, so the new service's first notices are toasts.
- **Swap**, the only downtime: the logon task is disabled before it is stopped (its restart-on-failure and 5-minute
  trigger would start the old exe mid-swap), then a bounded wait (30 s) until no process runs from `bin` and none of
  its files is held, then two directory renames on one volume: `bin` -> `bin.old`, `bin.new` -> `bin`.
- The task is registered (the user, interactive, limited, hidden, no time limit, restart every minute on failure, a
  second trigger every 5 minutes, one instance) and started; `/api/health` must answer within 30 s. Then `bin.old` is
  removed. A failed swap or no health answer puts `bin.old` back, enables and starts the task, and exits 1.
- A `bin` that has not passed the health check carries the file `.unverified`; `bin.old` is never deleted while
  `bin` carries it, and the next run puts it back first.
- A task the installer disabled carries the marker `installer-disabled-task` next to `bin`, so a run after a hard
  kill or power loss enables and starts it first. A task disabled by anything else is left as it is.
- Only after the new exe answered are the `power-mode\*.ps1` scripts copied (they forward to the new exe).
- Then the wake task (section 8) and, in an elevated shell, the admin broker (section 13). Not elevated: the broker
  step is named, and a registered broker stays stale until it runs.
- `config.json` is written only when there is none (then `-Mac` and `-CapacityWh` are required); an existing one is
  never changed.

Installer exit codes: 0 done (or the dry run found nothing in the way) · 1 a step failed, or the dry run found
something in the way · 2 could not run.
