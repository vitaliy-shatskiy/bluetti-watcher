# Bluetti Watcher - design

Bluetti Watcher is an always-on local service for a Windows PC that runs from a BLUETTI power station during power cuts.
It reads the station once a minute, stores the readings, serves a dashboard and an HTTP API on `127.0.0.1`, switches
the PC's power mode by mains state and battery level, and hibernates the PC before the battery is empty.

This document states the rules the service follows. Keys and defaults live in [configuration.md](configuration.md),
routes and fields in [api.md](api.md), registers in [bluetti-protocol.md](bluetti-protocol.md), the development
workflow in [CONTRIBUTING.md](../CONTRIBUTING.md).

## 1. Goals and limits

- Small footprint: one process, about 0 % CPU between readings. After each poll a compacting garbage collection, the
  native heaps compacted (HeapCompact, HeapOptimizeResources) and the working set trimmed (`trimWorkingSet`).
- Read only toward the station. The Bluetooth reader builds one kind of frame, a read of holding registers (Modbus
  function 0x03) under register 2000; its only GATT writes are that request and the notification subscription.
- The cloud client is read only too (section 4): no control, bind or fulfilment call exists; a test checks every URL
  against an allow-list.
- Never block or harm the PC on bad data: unknown beats wrong.

## 2. Architecture

1. **Process.** A per-user background process with no window, started at logon by the scheduled task
   `BluettiWatcher` as the user, restarted on failure. Never in session 0 (the power-mode mutex is a `Local\` name
   shared with the user's scripts): a start there exits 2.
2. **Host.** One C# NativeAOT exe. `BluettiWatcher.exe` is a console program for terminal commands (`--help`,
   `--version`, `--cloud-login`, `power-mode ...`, `--broker-allow ...`). The scheduled tasks start its twin
   `BluettiWatcherService.exe`, the same file with the Windows GUI subsystem, so no console window opens.
3. **Bluetooth reader** (`Io/Ble/`, ported from bluetti-bt-lib, MIT, [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)):
   in-process WinRT GATT for the 29 unencrypted IoT V1 and V2 models (configuration.md, `type`).
   - Connect, read, disconnect every poll, so the phone app can connect between polls. Requests on ff02, answers as
     notifications on ff01, each reassembled to full length and CRC-checked.
   - GATT services and ff01/ff02 come from Windows' cache first, over the air only when the cache fails or lacks them.
   - One connection open may take 40 % of `readerTimeoutSec` and is cut there even when the Bluetooth call ignores
     cancellation. A failed open is tried once more, leaving at least 20 % of the budget for the reads; an open cut at
     the cap logs the step it hung in.
   - The whole read is bounded by `readerTimeoutSec` + 2 s. Out of budget, the fields already read are kept as a
     partial reading and the log names what the read waited for.
   - A timed-out or invalid answer ends its connection; the next request opens a new one. An answer that an earlier
     unanswered request could have produced (same byte count) is set aside and asked again, so a late answer never
     fills another request.
   - One read at a time. While a read abandoned at the bound is alive, polls start no Bluetooth read (`/api/health`
     `bluetoothStalled`); after max(4 x the bound, 2 min) it is given up and its late end touches no newer read.
   - A station that opens the encrypted protocol's key exchange (`**`) is a miss and is not connected to again until
     restart (`bluetoothEncrypted`).
   - EP600, EP760, EP800 and EP2000 have no AC input power field: never connected, the cloud answers. A model with no
     output-mode register (every V2 model, the EB3A) gets the mode from the powers: AC in > 0 W = `BLE_MAINS`, else AC
     out > 0 W = `INVERTER_OUTPUT`. The AC200PL's read of 70-80 also gives the grid voltage and frequency.
   - A V2 station is asked four optional blocks after its reading (`V2Extras`: pack voltage and current, time to
     full and empty, warnings and faults, temperatures; registers and codes in bluetti-protocol.md). An extra never opens a connection. An exception reply marks
     its block not supported at once, a bad or missing answer in 2 polls in a row too, until restart (`/api/health`
     `stationBlocks`). V1 stations are never asked: their registers 100-138 carry other data. The pack current
     (read unsigned) does not feed the battery time.
4. **HTTP** on `127.0.0.1` only (section 16). **Storage** in SQLite through `winsqlite3.dll` (section 15).
5. **Power-mode switch** (`PowerMode/`): `BluettiWatcher.exe power-mode <command>`, the flag file under a named mutex
   with atomic writes, and the end of a timed eco. The scripts in `power-mode/` forward to it or read the flag
   ([power-mode/README.md](../power-mode/README.md)). Every flag change goes through the switch. The service runs it
   in-process on its own thread, only at a transition; `powerMode.script` runs that script under `pwsh` instead.

   | When | Command |
   |---|---|
   | station eco (legacy engine) | `eco -Source station` |
   | station emergency | `on -Source station` |
   | lift: mains back, the page's Max (legacy engine) | `off -Source station` |
   | on battery, at level changes and every `batteryRefreshSec` (best effort) | `battery -Soc <n> -Level ok\|low\|critical\|hibernating -Mains on\|off -HibernateAt <n>` |

   Each command has `powerModeTimeoutSec`; a hung one is killed and fails. A tool that exited but left its output
   open is waited for 2 s more, then its tree is stopped. The command's `plan:`, `warn:` and `note:` lines are logged.
6. **Live names** (`powerMode` in config.json: flag file, mutex, task prefix, extra readers) reach every power-mode
   child and `actions` command as `POWER_MODE_FILE`, `POWER_MODE_MUTEX`, `POWER_MODE_TASK_PREFIX`,
   `POWER_MODE_EXTRA_READERS`; the service reads the flag under the same mutex. A flag path set with the default
   mutex is reported at start (event `config-warning`).
7. Without `mac` (and a source that needs Bluetooth) the service serves the page and `/api/health` with the problem
   and polls nothing. There is no default address.

Exit codes: 0 stopped · 2 did not run (bad arguments, port taken, session 0, another instance holding the port's
lock past `lockWaitSec`) · 3 `winsqlite3.dll` missing or older than 3.24.0.

## 3. Readings

- Accepted only when complete (SOC 0-100, AC input W, AC output W, an output mode) and plausible: SOC within
  max(`socJumpFloor`, `socRatePerMin` x elapsed minutes) of the last accepted one. A rejected reading is stored with
  its reason and never used for a level or timer.
- A gap over `maxGapSec` between accepted readings resets the decision state (cut, levels, timers, streaks).
  Plausibility still applies across it: the allowance grows with the elapsed time.
- **Rebase:** three rejected readings in a row that agree within the rate make the newest the baseline. A rebased
  reading only moves the baseline: it never counts toward or breaks a level, advances no mains timer and writes no
  battery report. The latest rebase in a cut is remembered until mains is confirmed back, gap resets included, and
  makes hibernate follow the rebase rule (section 6).
- A reading accepted after a silence whose SOC change exceeds the rate over `jumpWindowSec` also counts as a rebase
  for that rule, but still counts toward levels and timers.
- Reading times are taken on arrival; durations come from them; a backward clock jump counts as a gap. The poll loop
  runs on the monotonic clock.
- `silentAfter` misses in a row = station silent. On battery under `fastBelow` % the interval is `fastPollSec`, else
  `pollSec`.
- **Mains state:** AC input > 0 = on mains; AC input 0 with output mode `INVERTER_OUTPUT` = on battery; else unknown.

## 4. Cloud fallback

When Bluetooth does not answer, the reading may come from the BLUETTI cloud, with the OAuth client and token of
BLUETTI's official Home Assistant integration (token in `cloud-token.json`). Every endpoint the code calls:

| Call | Endpoint | Use |
|---|---|---|
| authorize | `https://sso.bluettipower.com/oauth2/grant` (browser) | sign-in, authorization code |
| token | `POST https://sso.bluettipower.com/oauth2/token` | `authorization_code` at sign-in, `refresh_token` after |
| device list | `GET https://gw.bluettipower.com/api/bluiotdata/ha/v2/devices` | the station's serial, only while none is known; its values are never a reading |
| live state | `POST https://gw.bluettipower.com/api/bluiotdata/realtime/v1/getDeviceLastAlive`, body `{"deviceSn"}` | the reading (AC2) |
| power history | `POST https://gw.bluettipower.com/api/bluiotdata/dashboard/v2/getDevicePowerStatistics` | 5-minute curve: back-fill (4a), stand-in reading (AC2b) |
| energy history | `POST https://gw.bluettipower.com/api/bluiotdata/dashboard/v2/getDeviceEnergyStatistics` | on the history reader's allow-list; no code path calls it |

### Reading source

In force, in order: the page's choice (`reading-source.json`; "Use config" deletes it), `readingSource`, `bluetooth`
when `cloudEnabled` is false, else `auto`. Read every poll; no restart needed.

| Source | Bluetooth | Cloud |
|---|---|---|
| `auto` | first | on a miss, when `cloudEnabled` is true, or unset and the token file exists |
| `bluetooth` | only | never (no token, no back-fill) |
| `cloud` | never (no `mac` needed) | only |

A model Bluetooth does not read is polled through the cloud alone when the cloud is on.

### Cloud sign-in

`BluettiWatcher.exe --cloud-login` and the page's sign-in (`POST /api/cloud/sign-in`) run BLUETTI's OAuth
authorization-code flow with a one-shot catcher on `127.0.0.1` (an OS-assigned port), accepting only the `state` it
issued, for 5 minutes. A second press while one waits gets the same address. The token is written under the cloud
reader's lock, so a read in flight cannot write an older pair over it. Sign-out stops a waiting sign-in and deletes the
file. No answer or log line carries a token.

### Cloud state

`off` (source never calls the cloud) · `signed-out` (no token file) · `ok` · `sign-in-needed` (refresh token refused) ·
`error` (last read failed).

- AC1: a Bluetooth reading means no cloud call.
- AC2: a live state is a reading when `allFieldIsNull` is false, `batterySoc` is numeric and `timestamp` (station
  local time, read in this PC's zone) is within `cloudLiveMaxAgeSec` of the clock. Soc = `batterySoc`, InAc =
  `powerGridIn`, OutAc = `powerAcOut`, InDc = `powerPvIn`, OutDc = `powerDcOut`; `lastSource` `cloud`.
- AC2a: the mode is derived: `powerGridIn` > 0 = `CLOUD_MAINS`; 0 with `acSwitch` 1 and `powerAcOut` > 0 =
  `INVERTER_OUTPUT`; else none (incomplete).
- AC2b: all null, no numeric SOC, too old, or all zero is no live state, so a false 0 % never reaches the levels.
  Then today's newest history point (section 4a) is the reading (`cloud-history`) while at most
  `cloudHistoryMaxAgeMin` old; at most one history call per 5 minutes, counted in the back-fill's daily cap. Else a
  miss.
- AC3: `msgCode` not 0, no station of `type` in the list, HTTP failure or timeout (15 s; no answer at all is retried
  once), or a missing token file = a miss with one log line.
- AC4: a token expiring within 3 days or `msgCode` 805 is refreshed once
  (`POST https://sso.bluettipower.com/oauth2/token`), written atomically and the call retried. A failed refresh after
  805 is a miss; a failed early refresh keeps the token and waits an hour.
- AC4a: a refreshed pair that cannot be written is used from memory and written again each poll; never thrown away.
- AC5: the client only GETs the device list and POSTs the live-state, token and history endpoints.
- AC6: each accepted reading carries its source; `/api/health` gives `lastSource`.
- AC7: cloud readings pass the same plausibility and agreement rules.
- AC8: logs and errors never contain a token.
- AC9: off mains with OutAc >= `staleLoadW` and the cloud SOC unchanged for `staleAfterMin`: a miss ("cloud reading
  stale"), as the cloud may serve a frozen cache.
- AC10: a refresh answered 400, 401 or 403 is refused: `sign-in-needed`, that refresh token is not sent again for 6
  hours, and while the access token is known dead no device-list call goes out. A new token ends the hold. Other
  failures are retried.
- AC11: so a token used only on Bluetooth misses does not lapse, once an hour (cloud allowed, token present) a pair
  issued a day ago or more, or expiring within 3 days, is refreshed.

### 4a. Cloud history back-fill

While the source allows the cloud and the token exists, gaps in `station.db` are filled from the cloud's 5-minute
power curve (`cloudHistory` false = off).

- `CloudHistoryReader` POSTs only to `getDevicePowerStatistics` and `getDeviceEnergyStatistics` under
  `/api/bluiotdata/dashboard/v2/`; any other path throws before a request is built. Its own connection pool, no
  redirects.
- A gap is 10 minutes or more without an accepted reading in the last 30 days, up to 10 minutes ago. Each local day
  touched is asked once, newest first; a day asked an hour after it ended is done (meta `cloudHistoryDone`).
- Each point becomes an accepted row with reason `cloud history` only where no accepted row lies in its 5 minutes;
  rows are only inserted. Points without SOC above 0 are skipped; a day kept only hourly is skipped; clock-change
  hours are mapped, never invented.
- Runs 2 minutes after start, daily, and after a gap of 10 minutes or more ends. At most 4 calls a run and 40 a day
  (meta `cloudHistoryCalls`). On 805 the token is refreshed once a run.
- Its own task: a failure is a log line, never a poll's fault. It shares the cloud reader's lock for the token and
  serial (kept in meta `cloudHistorySerial`); a Bluetooth read never waits for it.

## 5. Mains, eco and manual control

This is the legacy engine (`modes.engine` = `legacy`). Under `v2` the Auto rules of section 12 choose the mode; the
mains confirmation below still defines the cut that those rules and the battery levels read.

- **Mains lost:** no AC input for `mainsLostSec` across accepted readings -> the cut begins, station eco on.
  **Mains back:** AC input for `mainsBackSec` -> every mode lifted (`off`), retried until it exits 0. Misses and
  rejected readings neither advance nor reset these timers. The lift spares a mode the user set after mains was first
  seen back.
- **Manual control:** Auto, Eco, Max (`POST /api/mode`, answered 202; the loop applies it with backoff). A choice
  holds until the next observed mains change or Auto. Max in a cut = the lift. A mains change drops a choice whose
  command has not run (`mode-dropped`); a newer press replaces an unrun one (`mode-superseded`).
- **Emergency hold:** Max never lifts a station emergency. While the flag says `emergency` or the emergency level was
  reached in this cut, Max and Eco are refused (409); a waiting choice re-checks the hold on each attempt and is
  dropped to Auto (`mode-refused`).
- While the flag is unreadable a page lift is not run; it stays pending.
- The last refusal or failure is saved as `modeError` until the next mode change. An applied choice re-reads the flag
  at once. Auto after a pinned mode runs no command; the next reading decides, taken at once.
- `/api/now` `ecoAt` = first-off + `mainsLostSec` while a cut is pending; null when no eco is pending.
- A poll or mode command holding the station gate over 20 s logs a `slow:` line.

## 6. Battery levels and hibernate

| Level | Key | Effect |
|---|---|---|
| emergency | `emergencyAt` | station emergency (`on`); level `low` |
| stop | `stopAt` | level `critical` (event; heard through the battery report) |
| warning | `warnAt` | hibernate warning; level `warning` (flag word `hibernating`) |
| hibernate | `hibernateAt` | `shutdown /h` |

Levels apply always, manual Max included.

- **Raised by the drain.** Each poll raises the levels by the measured drain `d` (section 7, capped at 120 %/h;
  `poll` = max(`pollSec`, `fastPollSec`)): `warnAt` to at least `hibernateAt + d x (hibernateAfterWarnSec + 2
  polls)`, `stopAt` to the warning + `d x 2 polls`, `emergencyAt` to the stop + `d x 2 polls`; rounded up to a tenth,
  at most 10 points over the configured value. `hibernateAt` never moves. No sound drain = configured levels. A
  reading at or under a configured level always counts toward it. `/api/now` `effectiveThresholds`.
- **Agreement:** two accepted readings at least `agreeSec` apart with none between them that contradicts. Misses and
  rejected readings neither count nor break; a gap reset does; a reading with mains unknown breaks every level.
- One new level per poll, each in a later poll. A level fires only once its action succeeded; failed actions back off
  1, 2, 4, 8 minutes, then every 10, each failure an event.
- **Hibernate** only on battery, at or under `hibernateAt`, at least `hibernateAfterWarnSec` after the warning in the
  same cut, and only after a fresh reading taken right before acting agrees. It depends only on readings and time:
  a failed flag write, emergency or stop never holds it back; the warning counts once attempted.
- **Rebase rule:** while a rebase is remembered, hibernate needs `rebaseHibernateConfirmSec` of agreeing readings at
  or under `hibernateAt`, counted from the later of the streak's start and the latest rebase; each reading adds at
  most twice the poll interval. A station that keeps rebasing never hibernates the PC (known cost). One
  `hibernate-delayed` event per rebase; `/api/now` `hibernateDelayedUntil`.
- **Resume:** an accepted reading more than 2 minutes after a hibernate means the PC resumed; the level re-arms with
  all its preconditions.
- **Kill switch:** under `v2` the `autoHibernate` setting (section 12); a `no-hibernate` file found at start is
  migrated into it and renamed `no-hibernate.migrated`. Under `legacy` a `no-hibernate` file in the data directory,
  checked every poll. `--dry-run` logs commands and hibernations instead of running them.

## 7. Battery time

The hours shown inform only; levels read only the measured rate, and only to rise.

- **Measured rate:** the SOC drop over the trailing run of accepted readings off mains within `rateWindowMin` (the run
  ends at mains, a missing SOC or a gap), from its first SOC step to its last; needs `rateMinDrop` points over
  `rateMinSpanMin`. Hours = (SOC - target) / rate x mean output / current output, at most 30 days.
- **Fallback:** capacity x SOC x efficiency / load. The efficiency is learned from clean windows (no DC input, no SOC
  step over `socJumpFloor`): first taken whole, then a quarter of the way, bounded 0.4-0.95 and at most `efficiency`
  + 0.05 (meta `runtimeEfficiency`).
- **Fitted model** (`fittedRuntime`): drain = P0 + (1 + k) x output, least squares over clean stretches of every
  stored cut, with a prior worth two hours of discharge at `efficiency`; used first once it rests on 2 hours of clean
  data and while there is no DC input. Range P10-P90 from leave-one-cut-out forecasts, published from 30 forecasts
  over 5 cuts. Refitted from the newest 20 000 rows at most every 15 minutes while `/api/now` is asked, never on the
  poll (meta `runtimeModel`).
- **Eco profile:** from cuts with an `eco-on` event the model learns the delay to eco and the load ratio after it,
  and counts the coming saving until this cut's `eco-on`.
- `/api/now` `runtimeSource` = `model`, `rate`, `learned` or `config`. The method was chosen by a backtest
  (`tests/BluettiWatcher.Tests/Backtest`, on request with `BW_BACKTEST_DB` and `BW_BACKTEST_OUT`).

## 8. Wake during a cut

A PC hibernated in a cut stays asleep after mains returns. The service can wake it through the scheduled task
`wakeTaskName`; off by default (`wakeEveryMinutes` 0) until the user has seen this PC wake from hibernation by a timer.

- a. Just before a hibernation it arms the task `wakeEveryMinutes` ahead (`wake-armed`). At or under
  `wakeFloorPercent` (unset: `hibernateAt` - 5) no wake is armed and one ahead is turned off (`wake-not-armed`).
- b. A failed arm (`wake-arm-failed`, 15 s at most) never holds a hibernation back; the turn-off is then owed on mains.
- c. An accepted reading on mains turns the wake off (`wake-disarmed`).
- d. Woken by the timer and still in a cut, the first reading on battery at or under `hibernateAt` hibernates again
  at once after the usual fresh reading; the next wake is armed.
- e. The timer woke the PC when the first poll more than 2 minutes after the hibernation falls 2 minutes before to 10
  minutes after the armed time. Then, with no accepted reading for `wakeReadSec`, the PC hibernates again without a
  fresh reading. (d) and (e) are the only hibernations outside section 6; a PC woken by hand is never hibernated
  without a reading. The kill switch and dry run hold both.
- f. The first reading on mains after a start turns the wake off whatever was saved (meta `wake`).
- g. A failed turn-off is retried with the action backoff (`wake-disarm-failed`); `/api/now` `wake` shows it.
- h. Operations: pwsh `Set-ScheduledTask` (a task without WakeToRun is refused) and `Disable-ScheduledTask`, under
  `powerModeTimeoutSec`.
- i. `tools/wake-task.ps1` registers the task from XML through COM: disabled, WakeToRun, a no-op action (System32
  `conhost --headless cmd /d /c exit 0`) as the user, least privilege, full control for Administrators, SYSTEM and the
  user so the unelevated service may set it. It reads the task back and checks every DACL entry against a whitelist;
  anything else refuses the task. An existing task of the name is replaced only when it reads back as one of ours.
  `install.ps1 -RemoveWake -Apply` removes it.

## 9. Actions

Config `actions` maps a state to a command line: `eco`, `emergency`, `stop`, `hibernate-warn`, `normal`, and the
alert `grid-voltage-high`.

- An executable and its arguments, started directly and windowless (no shell: use `cmd /c` for pipes), with the
  `POWER_MODE_*` environment and `actionTimeoutSec`. Exit code and the first 300 characters of output are stored as an
  event. A missing, failing or hanging command never changes a decision.
- Once per entry into a state per cut; the memory resets when mains returns. The page's Max and Auto-lift fire
  `normal`, its Eco fires `eco`.
- Under `v2`, `actions.eco` and `actions.normal` become the named commands `eco` and `normal`, run by the built-in Eco
  mode's command pair; the station's eco and mains-back do not fire them.
- `grid-voltage-high`: AC200PL grid voltage above 253 V, re-armed under 250 V; saved with the state.
- A V2 station's new warning or fault, a cleared code and a temperature over `stationTempHighC` (re-armed 5 °C under)
  write `station-warning`, `station-fault`, `station-alarm-cleared`, `station-hot`.

## 10. Page and times

- One HTML file embedded in the exe, no external libraries; charts from 1 hour to 30 days; fits a phone, Full HD and
  2K.
- The DTEK banner checks the plan against the last reading (at most 10 minutes old): back before the planned end =
  green, an unplanned outage = amber. Past hours show what happened, hatched over the plan.
- English and Ukrainian (the browser's language, `uk*` = Ukrainian; the header switch is remembered). Prose comes with
  a stable code the page translates by (`{code, text, args}`); an unknown code shows the English text.
- People-facing times use the PC's time zone; API times are ISO 8601 with the local offset; DTEK math runs in
  Europe/Kyiv, as the source publishes Kyiv wall time.

## 11. DTEK module (planned outages, Kyiv region)

An optional module (`DtekWatcher/`) that reads the distributor's planned-outage schedule for the user's address and
shows the next outage, the expected return and whether the battery lasts. The host knows it only through
`IDtekWatcher` (`RunAsync`, `AfterPoll`, `View`). The code cites the rule numbers below: keep them stable.

1. It informs; it controls nothing.
2. The schedule never influences eco, max or hibernate; no code in `Decision/` reads it (a test pins it).
3. **Queue search:** each real cut is compared with the house's queues' planned windows; two counted cuts in a row
   for one queue fix it, until then all are shown. A later cut that contradicts reopens it. Unplanned cuts say
   nothing. One queue needs no matching.
4. **Requests:** one `HttpClient`, HTTPS, default port, no proxy, two hosts only (the dataset's, `dtekWatcher.host`).
   Redirects only within the asked host, at most 3. 30 s per request, bodies over 4 MB refused.
5. **Cadence:** dataset every 30 minutes; site per rules 17 and 23.
6. **On failure** the saved schedule stays shown until its days end. A shield challenge is `blocked`, a misfit answer
   `format`; neither is read as "no outages".
7. During a cut the site's outage kind and return are shown only when it answered during this cut.
8. **Config** (`dtekWatcher`): off by default (no client, no request). `site` false = the dataset only. An invalid
   dataset URL or host turns the module off with the reason.
9. **Matching:** both ends of a cut within 20 minutes of a window's borders. Only a cut matching exactly one queue
   counts; not one without a saved schedule, or with an end in a reading gap over 5 minutes. "Possible" cells never
   count.
10. **Errors:** per source one `dtek-watcher-failed` per streak (class `network`, `blocked`, `format`, `failed`) and
    one `dtek-watcher-ok` on recovery.
11. **Battery lasts:** hours left at the current load against the expected return; `unknown` when either is unknown.
12. **Stored** (meta `dtekWatcher`): the schedule in use, validator, last address answer, match counters, fixed queue,
    the hint (rule 21), the running cut, per-source read state. No history.
13. It never talks to the station.
14. **Data:** today and tomorrow (Kyiv) of queues `GPV1.1` .. `GPV6.2`, 24 cells each (hour N = (N-1):00-N:00):
    `yes`, `no`, `first` / `second` (half hours), `maybe` / `mfirst` / `msecond` (possible), else unknown. Touching
    windows merge, across midnight too; clock-change days are 23 or 25 hours. Fields in api.md; site texts escaped.
15. **Dataset:** `https://raw.githubusercontent.com/Baskerville42/outage-data-ua/main/data/kyiv-region.json` (MIT),
    the site's `DisconSchedule.fact` unchanged plus `lastUpdated`. Read with `If-None-Match`; 304 keeps all.
16. **Freshness:** both times shown (distributor update, collection) with the source; the newest update wins, ties by
    collection; `stale` past `staleAfterMin`; `dtek-watcher-changed` when a shown queue's windows change.
17. **Site:** one attempt an hour, every 15 minutes in a cut; challenges set `siteState` only; after 24 in a row, once
    every 6 hours.
18. **House queues:** `dtekWatcher.queues`, else what the site's address answer named; neither = state `none`.
19. **Containment:** a total guard keeps every exception of the module from the poll loop and `/api/now` (logged once
    per message an hour, one `dtek-watcher-failed` per streak, view `state: none`). Damaged saved state starts fresh.
    Bounds, refused whole as `format`: at most 4 days, 24 queues, names `GPV<1-2 digits>.<1-2 digits>`, hours 1-24,
    day keys at a Kyiv day start within 2 days back and 7 ahead, update times at most 24 hours ahead; texts cut to 40
    and 120 characters. `dtek-watcher-changed` at most one per queue and day in 10 minutes. A cut whose plan changed
    while it ran neither counts nor contradicts.
20. **Manual queue** (`dtekWatcher.group`) is a hint: followed from the start, compared with every counted cut. Two
    cuts in a row matching one other queue refute it (`dtek-watcher-queue-changed`); the detected queue is followed
    from then on.
21. The hint is stored tied to its config value: the same value stays refuted after a restart, an edited one is fresh.
22. `/api/now` gives `queueOrigin` (`manual`, `detected`, `manual-refuted`, "") and `queueContradictions`.
23. Outside a cut the site is tried at most once in 60 minutes, no retry.
24. **Overdue:** a cut's window is the first planned window holding its start (from 20 minutes before), stretched over
    later windows that begin by now; across queues the latest end. With no window covering now and that window ended,
    the cut is overdue; `return` is then the site's or null. It survives a restart. The battery time runs to
    `hibernateAt`, at most 30 days.
25. **Lateness:** each finished cut in a planned window, while one queue is shown, records actual minus planned for
    start and end; the usual lateness is the median end delta, from 3 cuts. Meta `dtekWatcherLateness`, last 60.
26. **Plan edits** during a cut that move a shown queue's planned-off half hours are counted with the cut.
27. `/api/now` gives `overdue`, `overdueMin`, `plannedReturn`, `batteryUntil`, `planChanges`, `lateBackMin`,
    `lateCuts`.
28. **Queue sets:** `group` may be an array (at most 4 names, held as `A+B`); its plan is the union of the members'
    off half hours and everything that reads the followed queue reads the union. `/api/now` adds `queueSet`,
    `nextQueues`, `intervals`.
29. **Short cuts:** a cut shorter than `minCutMin` (0-1440; 0 = all count) adds no lateness and does not count for the
    queue search or the hint; it applies when the cut ends.

## 12. Power modes (`modes.engine` = `v2`)

The default engine (`legacy` keeps section 5). Code in `Modes/`; routes in [api.md](api.md).

1. **Mode:** a named list of actions (`{ typeId, enabled, enforce, settings }`) and the flag word it maps to
   (`normal`, `eco`, `emergency`). One mode is active. Built-ins `normal`, `eco`, `max` are editable, not deletable,
   re-added when missing. Ids are 1-32 of `a-z 0-9 -`; `auto` is reserved.
2. **Action module** (`IActionModule`): a descriptor (type id, JSON Schema, UI hints, discovery lists, restoration
   class, needs-admin, defaults) and an adapter that discovers choices, validates settings, expands them into
   resource intents and reads, writes and reads back one resource. Registered by hand in `ModuleCatalog` (no
   reflection under NativeAOT); the registry refuses duplicate ids and defaults that fail their own schema.

   | Type id | Restoration | Sets |
   |---|---|---|
   | `windows.powerPlan` | reversible | the active power plan |
   | `windows.cpuPolicy` | reversible | max processor state and boost mode of a plan, mains and battery |
   | `display.offTimeout` | reversible | the active plan's display-off timeout |
   | `gpu.powerLimit` (broker) | reversible | NVIDIA board power limit: `percent`, `watts` or `limit: "min"` |
   | `display.refreshRate` | reversible | monitor refresh rate (may crash a browser's GPU process) |
   | `display.brightness` | reversible | DDC/CI brightness |
   | `nvidia.profile` (broker) | compensatable | a Profile Inspector `.nip`, customised profiles exported first and restored on leaving |
   | `service.state` (broker) | reversible | a service's running state and start type |
   | `scheduledTask.state` | reversible | a task enabled or disabled (through the broker when it refuses the user) |
   | `process.close` | irreversible | apps closed (killed after a grace only with `kill`); system processes and this service never |
   | `windows.phoneLink` | irreversible | Phone Link closed |
   | `command.pair` | compensatable | a named command (`modes.commands`) on entering, another on leaving |
   | `rgb.lighting` | compensatable | RGB off through OpenRGB's SDK server (127.0.0.1:6742); leaving loads a named profile |

   `command.pair` and `rgb.lighting` cannot read what they changed: their read is their memory, recalled from the
   journal after a restart (`IRecallsWrites`). `rgb.lighting` starts a headless `OpenRGB.exe --server
   --noautoconnect` on a write when no server answers and leaves it running; OpenRGB.exe comes from
   `modes.openRgbPath` or the install folders, never from a mode.
3. **Selection:** a manual pick holds until Auto. Auto takes the first rule whose condition holds: `mainsOffFor` /
   `mainsOnFor` (confirmed mains), `socBelow`, `cutWithin` (the DTEK module's next outage; never when the module is
   off), `timeWindow` (local, may wrap midnight). A rule in force keeps matching within its hysteresis; with none
   matching, the last mode stays; unknown inputs match nothing. Defaults reproduce section 5.
4. **Battery emergency** overrides: the mode's actions stay and the engine never writes over the flag's `emergency`
   while the emergency level holds in the current cut.
5. **Reconciler:** each poll and after each API change, per resource: read (a failed read writes and claims nothing);
   equal = `satisfied`; else journal `Prepared(before, target)`, write, read back, journal `Applied`, own it.
   - Ours only while it reads what we wrote. A person's change is let go (`relinquished`, `mode-drift`) unless the
     action has `enforce`.
   - Leaving restores owned resources the next mode does not set to their baseline, only while still ours; a failed
     restore is retried 3 times, then reported. Irreversible actions are never undone.
   - One failure never stops the others. Failed writes retry after 1, 2, 4 ... minutes, at most 30, alerted once
     (`action-failed`).
   - An API write waits at most half the HTTP request limit; then it answers 202 and the transition runs on.
6. **Journal** `modes-journal.jsonl`: each record flushed before its step. Replayed at start before any write: an open
   `Prepared` is settled by reading; a token resource from before the last boot is let go; a torn last line is cut.
   Any other bad line moves the journal aside (`.corrupt-<unix>`), restores nothing and holds `normal` until Auto
   (`modes-problem`). Compacted at start and past 5000 records, keeping owned state, the last mode and 200 records.
7. **Settings** `modes.json`, versioned, replaced atomically. A first start after the switch was used migrates
   `returnPlan` and `gpuCap`; a fresh install gets Normal = Balanced, Eco = Power saver with its GPU limit (`min`), a
   command pair, services and Phone Link listed but off, and Max = High performance. Defaults never rewrite a saved
   mode. An action of a retired type (`gpu.idleWatcher`) is dropped at start with one log line, and an owned resource
   of it is closed as dropped, never restored. An unparseable file is moved aside; one from a newer build is left alone
   and defaults run in memory.
8. **Flag:** the active mode's word goes through the switch's own flag code, so flag readers are unchanged. The
   station's eco and mains-back run no switch command under `v2`; the emergency and the battery report still do.
9. **Policy warnings:** a saved mode answers with the entries the broker policy does not allow and the elevated
   command that allows each.
10. **Page:** the header switch picks Auto or a mode. The Power modes panel (only under `v2`; `/#modes`) shows each
    mode as plain lines worded by the page from its actions, the usual Auto pair as one sentence with the minutes
    editable (any other rule set as a list), and only what failed. The mode editor opens in place, its forms generated
    from the catalogue; an action type with an editor of its own (the GPU limit: the card's lowest limit or watts of
    one's own) is registered outside the panel, which names no module. A block paints only once the catalogue is in,
    and the page asks for it at load beside the first view. The Settings card holds the hibernate setting, the
    notification switches and the test notification.
11. **Catalogue:** built once at start; an ask gets the last build at once and one rebuild runs behind it.

## 13. Admin broker (power modes)

The service never runs elevated. Actions that need it ask the SYSTEM task `BluettiWatcherBroker` (no trigger),
started on demand (`schtasks /run`), and wait up to 10 s for the answer.

- **Folders:** `%ProgramFiles%\BluettiWatcher\broker` holds what SYSTEM trusts (`bin\` with its own exe copy,
  `policy.json`, `profiles\`, `exports\`, `broker.log`); `%ProgramData%\BluettiWatcherBroker` holds `requests\` and
  `responses\`. Both are owned by Administrators with inheritance cut; the user reads, and may only add files in
  `requests\`. SYSTEM never runs an exe the user can replace.
- **Install** (`install.ps1 -Broker -Apply`, elevated): refused when an ancestor of either root is owned by the user,
  lets the user delete, rename or re-permission it, or cannot be read; ACLs are read back after setting. The policy's
  user is the logon task's user. The switch's GPU cap tasks are removed once the broker is registered.
- **Request:** `requests\<id>.json` (`{ v, id, op, args, issued }`, 32 hex digits, written as `.tmp` and renamed);
  answer `responses\<id>.json`, pruned after 10 minutes.
- **Default deny:** `policy.json` must be a plain file owned by Administrators or SYSTEM, writable by no one else,
  parsing strictly; the request a plain file owned by the policy's user or an administrator, opened without following
  links, under 4 KB, issued within 30 s, with a known operation and exactly its arguments.
- **Operations:** `service.query|start|stop|setStartType` and `task.enable|disable` for policy-listed names;
  `gpu.query`, `gpu.setPowerLimit` within the card's own range; `nvidia.exportProfiles` and `nvidia.applyProfile` for
  listed or exported files; Profile Inspector only under Program Files or the broker folder. Nothing from a request
  reaches a shell. `BluettiWatcher.exe --broker-allow service|task|profile|inspector <name>` (elevated) edits the
  policy.
- **Version:** after an upgrade the broker's exe copy is stale until the elevated step runs again. `/api/health`
  `broker` says `missing`, `stale` or `current`. A stale broker at start is re-checked 10 minutes later and only then
  told (event `broker-stale`, a tray notice), once per service version (marker `broker-stale-noticed`).

## 14. Tray and notifications

1. **Icon:** an STA thread in the service with a hidden window and a blocking `GetMessage` loop; Win32 through
   source-generated P/Invoke, no WinForms or WPF. Only in an interactive session; `tray` false or `--no-tray` = off.
2. After each poll and choice the service pushes a snapshot (charge, mains, watts, mode, time left, cut). A reading
   older than `maxGapSec` is stale: grey icon, no watts.
3. The icon is drawn at the taskbar's DPI size: the charge in a 3x5 pixel font and a bar, green on mains, amber on
   battery, grey unknown; rebuilt only when it changes.
4. **Menu:** Auto and the modes as one radio group (under `legacy` Auto / Eco / Max), "Open panel", "Exit tray" (the
   service runs on). Explorer restarts (`TaskbarCreated`) add the icon again.
5. **Toasts** under the app id `BluettiWatcher`, set on the Start-menu shortcut by the installer; a click opens the
   page by protocol activation. Without that shortcut, or when a toast fails, a shell balloon (`NIF_INFO`) instead.
   With the icon closed there are no notices.
6. **Groups** (`notifications.json`, `PUT /api/settings/notifications`):

   | Group | Source |
   |---|---|
   | `mains` | the confirmed cut starting or ending (never on the first snapshot) |
   | `modeChanges` | `mode-changed`, `mode-dropped`; `eco-on`, `mains-back` under `legacy`. A switch the user made (`mode-chosen`) shows none. |
   | `lowBattery` | `emergency-on`, `stop`, `hibernate-warning` |
   | `failures` | `action-failed`, `mode-refused`, `broker-stale` |
   | `faults` | `grid-voltage-high`, station faults and warnings |
   | `overTemperature` | station temperature over `stationTempHighC` |

   `enabled` false silences every group and keeps each group's value. Events come through a hook on the store, so
   every writer counts. Station alarms notify when they appear.

## 15. Storage and files

- `station.db` (SQLite via `winsqlite3.dll` 3.24.0+): `reading` (every reading: accepted, missed or rejected with its
  reason), `agg5` (5-minute aggregates), `event`, `meta` (decision and module state). Raw rows kept
  `rawRetentionDays`, then folded into aggregates kept forever.
- A new file: 1 KB pages, WAL, `synchronous=NORMAL`, WAL cut back to 1 MB after each checkpoint. An existing file
  keeps its page size and journal mode (no rebuild or VACUUM: rebuilding in place corrupts a file whose `-wal`
  another process holds open).
- `service.log` rolls to `service.log.1` past 1 MB. Identical lines fold into `repeated N times since HH:mm:ss:
  <line>`, written before the next different line, at most 15 minutes late, and at a clean stop.
- Data directory (default `%LOCALAPPDATA%\bluetti-watcher`): `config.json` (the user's), `station.db`, `service.log`,
  `cloud-token.json`, `reading-source.json`, `modes.json`, `modes-journal.jsonl`, `notifications.json`,
  `no-hibernate`, `broker-stale-noticed`, `power-mode\` (switch scripts), `bin\` (section 17). The flag defaults to
  `%ProgramData%\BluettiWatcher\power-mode.json`.
- The token, reading-source, modes and notification files are written to a temp file and moved over the old one.

## 16. HTTP and its security model

- A minimal HTTP/1.1 server on a `TcpListener` bound to `127.0.0.1` (`port`): no URL ACL, no admin rights, no TLS.
  One request per connection; headers up to 16 KB (431), bodies up to 4 KB, 64 KB on the power-modes routes (413);
  the head within 5 s, 10 s per connection, at most 32 connections.
- **Every request:** `Host` must be exactly `127.0.0.1:<port>` or `localhost:<port>` (403 otherwise; stops DNS
  rebinding).
- **Every write route** (mode, modes, return plan, reading source, cloud sign-in and sign-out, notification settings
  and test): header `X-Station: 1` (a foreign page cannot send it without a CORS preflight, never allowed) and an
  `Origin` absent or the service's own; else 403.
- Fixed headers: `nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, a CSP of `default-src 'self'`
  with `frame-ancestors 'none'`. The page is served with an ETag.
- There is no authentication: the guards protect against browsers, not against other software on the PC.

## 17. Upgrade and swap

`install.ps1` is a dry run unless `-Apply`. The swap is `tools/swap.ps1` (tested by `tools/test-swap.ps1`).

- The new exe and its service twin go to `<data dir>\bin.new` while the service runs; the live `bin` is never a build
  output. A failure there changes nothing.
- The Start-menu shortcut is written before the swap, so the new service's first notices are toasts.
- **Swap**, the only downtime: the task is disabled, then stopped (its restart triggers would start the old exe),
  a bounded wait (30 s) until nothing runs from or holds `bin`, then `bin` -> `bin.old`, `bin.new` -> `bin`. The task
  is registered and started; `/api/health` must answer within 30 s, then `bin.old` is removed. On failure `bin.old`
  comes back, the task is enabled and started, exit 1.
- A `bin` that has not passed the health check carries `.unverified`; the next run puts `bin.old` back first. A task
  the installer disabled carries the marker `installer-disabled-task`, so a run after a power loss enables it first.
- Only after the new exe answered are the `power-mode\*.ps1` scripts copied. Then the wake task and, elevated, the
  broker; not elevated, the broker step is named.
- `config.json` is written only when there is none (`-Mac` and `-CapacityWh` required then).

Installer exit codes: 0 done · 1 a step failed or the dry run found a blocker · 2 could not run.
