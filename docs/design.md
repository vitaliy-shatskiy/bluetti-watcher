# Bluetti Watcher - design

Bluetti Watcher is an always-on local service for a Windows PC that runs from a BLUETTI power station during power cuts.
It reads the station once a minute, stores the readings compactly, serves a dashboard and a small HTTP API on
`127.0.0.1`, and switches the PC's power mode (eco, emergency, normal, hibernate) by mains state and battery level.
This document states the rules the service follows. Every key named here is described in
[configuration.md](configuration.md); every endpoint in [api.md](api.md). The registers the official app reads, for
read-only features to come, are mapped in [bluetti-protocol.md](bluetti-protocol.md).

## 1. Goals and limits

- Tiny footprint: about 20 MB working set at most and about 0 % CPU between readings; one process, one file (SQLite is
  Windows' own `winsqlite3.dll`). After each poll: a compacting garbage collection, the native heaps' free memory
  decommitted (HeapCompact, HeapOptimizeResources), then the working set trimmed unless `trimWorkingSet` is false (the
  Task Manager "Memory" column; commit is unchanged, the pages fault back in at the next poll). Known folders come from
  `%LOCALAPPDATA%` and `%ProgramData%`, so reading them loads no shell32 (the shell call stays as the fallback).
- Read only toward the station, forever. The Bluetooth reader can build one kind of frame, a read of holding
  registers (Modbus function 0x03) under register 2000, and its only GATT writes are that request and the
  notification subscription.
- The BLUETTI cloud fallback (section 4) is read only the same way: it reads the station's live state and the device
  list and refreshes its token, and its history back-fill (section 4a) reads two statistics endpoints, nothing else.
- Never blocks or harms the PC on bad data: unknown beats wrong.

## 2. Architecture

1. A per-user background process, no window (the notification-area icon of §14 is its only UI besides the page), started at logon by a scheduled task as the user (Medium
   integrity, "run only when the user is logged on"), restarted on failure; nothing before logon. It must never run in
   session 0: the power-mode mutex is a `Local\` name shared with the user's own scripts. A start in session 0 exits 2.
2. Host: one C# NativeAOT exe (`BluettiWatcher.exe`), a console program: a terminal waits for `--help`, `--version` or
   `power-mode status` and gets its exit code. The scheduled tasks (the logon task, the eco end task) start its twin
   `BluettiWatcherService.exe`, the same file with the PE subsystem set to Windows GUI, which install.ps1 writes beside
   it: a task that starts a console program opens a console window, a GUI-subsystem exe opens none. Bluetooth LE goes in-process through WinRT GATT, the 29 IoT V1 and
   V2 models (AC200L, AC200M, AC200PL, AC300, AC500, EB3A, EP500, EP500P; AC2A, AC2P, AC50B, AC60, AC60P, AC70, AC70P,
   AC180, AC180T, AC180P, AP300, EL10, EL30V2, EL100V2, EP600, EP760, EP800, EP2000, Handsfree 1, PR30V2, PR100V2):
   connect, read, disconnect per poll, so the phone
   app can connect between reads; the reading fields in batched function-0x03 reads (notifications on ff01, requests
   on ff02), each answer reassembled to its full length and CRC-checked. The AC200PL's read of 70-80 also carries the
   grid input: register 77 voltage and 80 frequency, both / 10 (as the official app reads them, not in the library);
   no other request follows the reading on a V1 station. A V2 station is asked four optional blocks after its reading
   (`V2Extras`, docs/bluetti-protocol.md sections 2.3, 2.4 and 3): pack voltage and current (100-101), its own time to
   full and to empty (104-105, minutes likely, not confirmed), warnings and faults (122-138, decoded into the app's codes:
   prefix E, or B for a high-power inverter, warnings from 113 or 97, faults from 1) and temperatures (1151-1153,
   u16 - 40 °C). An extra never opens a connection and is never asked again in the poll; an exception reply marks its block "not supported" at once,
   a short or garbled answer or no answer in two polls in a row does too (one lost answer may be the radio); the mark
   holds until the service restarts (logged once,
   `/api/health` `stationBlocks`), so it never fails or slows a later poll. A V1 station (the AC200PL) is never asked them:
   its registers 100-138 carry other data (105-120 are cell voltages). The station's values are stored per reading and
   shown on the page; the pack current does not feed the runtime estimate (the app reads it unsigned, so it does not say
   charge from discharge, and no V2 station was available to validate it). A new warning or fault, a code that clears
   and a temperature above `stationTempHighC` (re-armed 5 °C under it) each write an event row and run the matching
   `actions` command. Each connection open is capped at 40 % of `readerTimeoutSec` (12 s at the default 30 s) and cut
   there even when the Bluetooth call ignores its cancellation (a link it hands over later is released), so two opens
   leave at least 20 % of the budget for the register reads. When opening the connection fails (GATT Unreachable, no
   ff01/ff02, the cap), the poll opens once more before it gives up; when `readerTimeoutSec` runs out, the fields already read are kept as a partial reading and the log
   names what the read was waiting for. An answer that timed out or came back invalid
   ends its connection and the next request goes over a new one; as a reconnect may reuse the radio link, an answer
   that an earlier unanswered request (of this poll or an earlier one) could have sent (same byte count, so any
   one-register read) is set aside and the registers are asked again, so a late answer never fills another request; the
   whole read is bounded by `readerTimeoutSec` plus 2 s whatever the Bluetooth stack does, and one read runs at a time:
   while a read abandoned at that bound is still alive, a poll starts no Bluetooth read (a miss, the cloud answers;
   `/api/health` `bluetoothStalled`) and the abandoned read releases its connection when it ends; one still alive after
   max(4 x that bound, 2 minutes) is given up (logged) and the next poll reads again, its late end touching no newer
   read. A station it cannot reach
   is a missed reading, so the cloud fallback answers; so does a model with no AC input power field (EP600, EP760, EP800,
   EP2000: battery % only, no mains state; no connection is made); a model with no output-mode register (every V2 model,
   the EB3A) gets the mode derived as in the AC2a rule below (`BLE_MAINS` for AC input above 0 W); so does a station that encrypts its Bluetooth link (it opens with
   a key-exchange message, "**" and a checksum, which no Modbus answer can be: the poll ends at once, no reading, and it is not connected to again until the service restarts; `/api/health`
   `bluetoothEncrypted` says so); an
   unknown model is read through the cloud alone when the cloud fallback is on. Ported from bluetti-bt-lib 0.1.8 (MIT, [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)).
3. HTTP on `127.0.0.1` only (port 18940 by default): the dashboard and the API ([api.md](api.md)). The installer adds
   a Desktop and Start menu shortcut that opens the page as a Chrome or Edge app window.
4. Storage: SQLite through Windows' `winsqlite3.dll` (`station.db` in the data directory), one row per minute (and one row per missed reading, marked);
   raw rows are kept `rawRetentionDays` (30), then 5-minute aggregates forever. A new file is created with 1 KB pages
   in WAL with `synchronous=NORMAL`: a poll's commits are not flushed, only the checkpoint every ~1000 pages is, and
   the WAL file is cut back to 1 MB after it. An existing file is opened as it is: its page size and journal mode are
   never changed (no rebuild or VACUUM; a rebuild in place corrupts the file when another process holds the `-wal`
   open), so a database made with 4 KB pages keeps them. Opening waits on a busy file and never throws for a reader.
   The log `service.log` rolls to `service.log.1` past 1 MB; a line identical to the one before is folded into one
   `repeated N times since HH:mm:ss: <line>` line, written before the next different line. The first occurrence of a
   line is written at once; a pending count is written by a timer at most 15 minutes after it began (and when the
   service stops cleanly), so a kill loses at most the count of the last 15 minutes of identical repeats, never a
   distinct line.
5. The power-mode switch is `BluettiWatcher.exe power-mode` (`src/BluettiWatcher/PowerMode/`): the commands, the flag
   file under a named mutex with atomic writes, and the end of a timed eco (the end task runs the exe itself). The
   scripts under `power-mode/`, installed at `<data dir>\power-mode\`, forward to it (`power-mode.ps1`,
   `power-mode-end.ps1`) or read the flag for other scripts (`power-mode-flag.ps1`). Every change of power mode goes
   through the switch. The service runs it in its own process (`power-mode <command> ... -Path <flag>` on a thread of
   its own, the live names as its environment), only at a transition; a configured `powerMode.script` runs that
   script as `pwsh -NoProfile -NonInteractive -File <script> <command> ... -Path <flag>` instead:
   - station eco: `eco -Source station` (Windows Power saver plan, GPU power cap where available; no end time, lasts
     until `off`)
   - station emergency: `on -Source station`
   - lift (mains back, and the page's Max): `off -Source station` (every mode lifted, the configured return plan set)
   - battery report: `battery -Soc <n> -Level ok|low|critical|hibernating -Mains on|off -HibernateAt <n>`

   Each power-mode command has `powerModeTimeoutSec` (60, clamped to 1..600) to finish; a hung one is killed and counts
   as failed. A flag path set while `powerMode.mutex` stays the default is a mistake the service reports at start (log
   and event `config-warning`).
6. Live object names are configuration (`powerMode` in config.json), with neutral defaults: the flag file
   `%ProgramData%\BluettiWatcher\power-mode.json`, the mutex `Local\BluettiWatcherPowerMode`, the scheduled-task
   prefix `BluettiWatcherEco` (tasks `<prefix>End`, `<prefix>GpuCap`, `<prefix>GpuRestore`). The service passes them
   to every power-mode child it starts in the environment variables `POWER_MODE_FILE`, `POWER_MODE_MUTEX` and
   `POWER_MODE_TASK_PREFIX`, and uses the same mutex when it reads the flag itself. Other programs on the PC may read
   the flag to adapt to the power mode (see the README's integrations).
7. Without `mac` in config.json the service starts, serves the page and `/api/health` with a problem naming the
   missing `mac`, and polls nothing. There is no default station address.

Exit codes: 0 stopped normally; 2 did not run (the port taken, session 0, another instance still holding the port's
lock after `lockWaitSec`); 3 did not run: Windows' SQLite (`winsqlite3.dll`) is missing or older than 3.24.0 (the
store's upserts need it), named in one `service.log` line with the version found.

## 3. Readings

- A reading is accepted only when complete (SOC, AC input W, AC output W, output mode present and parseable) and
  plausible: a SOC change larger than `socRatePerMin` points per elapsed minute (default 5, floor `socJumpFloor`, 3)
  against the last accepted reading is rejected, stored as rejected and never used for a level or a timer.
- A gap over `maxGapSec` (600 s) between accepted readings resets the whole decision state (cut, fired levels, level,
  timers, streaks).
- Plausibility always applies, gaps included: the allowed SOC change grows with the elapsed time, and a gap never
  resets the last accepted reading.
- Rebase: if three readings in a row are rejected but agree with each other within the allowed rate,
  the newest becomes the accepted baseline, so a wrong baseline cannot hold for long. A rebased reading only moves the
  baseline: it never counts toward a battery level (it neither starts, continues nor breaks a level's agreement),
  advances no mains timer and writes no battery reading to the flag.
- The latest rebase in a cut is remembered until mains is confirmed back, gap resets included; while one is
  remembered, hibernate follows the rebase rule of section 6.
- A reading accepted outright after a silence counts as a rebase for that rule when its SOC change is larger than the
  rate allows over at most `jumpWindowSec` (120 s, so 10 points): only the silence made the allowance that wide (8
  minutes of misses, then 0 % after a real 40 %). Unlike a rebased reading it still counts toward the levels and the
  timers. A real drain seen again after a silence stays under that bound and keeps the normal rule.
- The lower levels (emergency, stop, warning) may fire on readings that follow a rebase; that costs only a power mode.
- Reading times are taken when the reading arrives, before any lock. Durations come from reading timestamps; a
  backward clock jump counts as a gap. The poll loop runs on the monotonic clock: a wall-clock step, backward or
  forward, neither delays a poll nor brings one early.
- After `silentAfter` (3) misses in a row the station is reported silent. While on battery under `fastBelow` (25 %)
  the poll interval is `fastPollSec`, else `pollSec` (both 60 s).
- Mains state: AC input above 0 = on mains; no AC input with output mode `INVERTER_OUTPUT` = on battery; anything else
  is unknown.

## 4. Cloud fallback

When Bluetooth does not answer, the reading may come from the BLUETTI cloud: the live state the official app reads
(`POST https://gw.bluettipower.com/api/bluiotdata/realtime/v1/getDeviceLastAlive`, body `{"deviceSn"}`), with the token
of BLUETTI's official Home Assistant integration in `cloud-token.json` (`cloudTokenPath`, default the data directory).
The station's serial comes from that integration's device list (`GET .../api/bluiotdata/ha/v2/devices`), read only
while no serial is known (none saved, or the history refused it); its values are never a reading. It is opt-in:
`cloudEnabled` not set = on when the token file exists (checked at each poll); `true` = on; `false` = off.

### Reading source

`readingSource` picks where readings come from; the page's Reading source buttons set the same choice:

- `auto` (the default): Bluetooth first, the cloud when Bluetooth misses, as above (`cloudEnabled` applies).
- `bluetooth`: Bluetooth only. The cloud is never called, no token is needed, and the cloud history (§4a) does not run.
- `cloud`: the cloud only. Bluetooth is never opened, so no `mac` is needed and any model the cloud lists is read.

Not set = `auto`, or `bluetooth` when `cloudEnabled` is `false` (the key that switched the cloud off before
`readingSource` existed). The page's choice is saved in `reading-source.json` in the data directory and wins over the
key from the next poll on, no restart. The page says when the source was set there and offers "Use config", which
deletes that file so the key applies again. The source in force is read at each
poll, by the reader and by the history back-fill alike.

### Cloud sign-in

`BluettiWatcher.exe --cloud-login` signs in once: it opens BLUETTI's OAuth login in the browser, catches the answer on
a localhost address, writes the token file and exits. It never prints the token.

The page's "Sign in to BLUETTI cloud" runs the same authorization-code flow inside the running service
(`POST /api/cloud/sign-in`): the service starts a one-shot catcher on `127.0.0.1` at a port the OS assigns (never the
dashboard's), answers the authorize address, and the page opens it in a new tab. The catcher accepts only the `state`
it issued; it waits 5 minutes. The token is written under the cloud reader's lock, so no read in flight writes an
older refreshed pair over it, and what the reader held for the old token (a refusal, an unsaved pair) is dropped.
A second press while one sign-in waits gets the same address. "Sign out" (`POST /api/cloud/sign-out`) stops a waiting
sign-in and deletes the token file. Both routes sit behind the write guard of `/api/mode`; no answer and no log line
carries a token, and the authorize address (its one-time `state`) goes only to the page.

### Cloud state

The page and `/api/health` show the cloud's state: `off` (the source never calls the cloud) · `signed-out` (no token
file) · `ok` (the last read answered, or signed in and not read yet; the detail names the access token's expiry) ·
`sign-in-needed` (the server refused the refresh token) · `error` (the last read failed; the detail names why).

- AC10: a refresh answered HTTP 400, 401 or 403 is a refused grant, not a hiccup: the state turns `sign-in-needed`, the
  refused refresh token is not sent again for 6 hours, and while the access token is known dead (it answered 805)
  no device-list call goes out either: each poll's miss names "sign-in needed" without a request. A new token (the
  page's sign-in, or `--cloud-login` writing the file) ends the hold at once. Any other failed refresh (429, 5xx, no
  answer) is retried as before.
- AC11: under the auto source a healthy Bluetooth never calls the cloud, so a token used only by reads would lapse
  unnoticed. Once an hour the service checks the token while the source allows the cloud and a token file exists: a
  pair issued a day ago or more, or expiring within 3 days, is refreshed (logged, never the token). A refused refresh
  turns the state to `sign-in-needed` (AC10); another failure shows as `error` and waits an hour.

- AC1: when Bluetooth returns a reading, the cloud is not called.
- AC2: when Bluetooth returns nothing and getDeviceLastAlive answers `allFieldIsNull` false, a numeric `batterySoc`
  and a `timestamp` at most `cloudLiveMaxAgeSec` (120) seconds off the clock either way, the reading is Soc =
  `batterySoc`, InAc = `powerGridIn`, OutAc = `powerAcOut`, InDc = `powerPvIn`, OutDc = `powerDcOut` (numbers as
  strings or numbers; null = unknown), the voltage fields null, `lastSource` `cloud`. The `timestamp` is the station's
  local time, read in this machine's time zone (the station and the machine are taken to share one zone; in the hour
  a clock change repeats, the reading nearer now).
- AC2a: the cloud has no output mode, and a reading without one is incomplete (section 3). The mode is derived:
  `powerGridIn` > 0 = `CLOUD_MAINS` (mains is on whatever the mode); `powerGridIn` 0 with `acSwitch` 1 and
  `powerAcOut` > 0 = `INVERTER_OUTPUT` (the AC load runs from the battery); anything else = no mode, so the reading is
  incomplete.
- AC2b: the HA device list answered an all-zero state (SOC 0 %, 0 W in, 0 W out) for an AC200PL running on Bluetooth
  at a real charge, while getDeviceLastAlive answered its real state. A live state with every field null
  (`allFieldIsNull` true), no numeric SOC, a timestamp outside `cloudLiveMaxAgeSec`, or all zero is no live state:
  never a reading, so a false 0 % never reaches the stop and hibernate levels nor a stored row. The newest point of today on the history's
  power curve (§4a, the same read-only call) is the reading instead, `lastSource` `cloud-history`, while it is at most
  `cloudHistoryMaxAgeMin` (10) minutes old; its mode is derived as in AC2a with the AC output taken as on when it feeds
  a load, and the decision's checks (AC9 stale included) apply as to any cloud reading. At most one history call each
  5 minutes (the last answer serves between them), counted in the back-fill's daily cap of 40 calls. An older or
  missing point, a failed call or the cap reached is a plain miss (no reading). "cloud: no live state from
  getDeviceLastAlive (<reason>)" is logged once until it answers a live state again; the history's outcome once each
  time it changes.
- AC3: when the answer is not `msgCode` 0, the device list holds no station of `type` (while the serial is looked
  up), HTTP fails or times out (15 s cap; a live-state or device-list request with no answer at all is sent once
  more), or the token file is missing or unreadable, the result is a miss with one log line naming why, never the
  token. An offline station answers an old timestamp: no live state (AC2b).
- AC4: when the access token expires within 3 days (`created_at` + `expires_in`) or the API answers `msgCode` 805,
  the reader refreshes once (`POST https://sso.bluettipower.com/oauth2/token`, `grant_type=refresh_token`), writes the
  token file atomically (temp file flushed to disk, then replace) and retries once; a failed refresh after 805 is a
  miss, logged. A failed early refresh (token not yet expired) is logged and the current token is used; the next
  early refresh waits an hour.
- AC4a: when a refresh succeeded but the token file cannot be written, the new pair is used from memory, logged, and
  written again on every poll until it is written; a pair the server issued is never thrown away. A token file that
  is not JSON (torn, zeroed) is logged as such.
- AC5: the cloud client only GETs the devices endpoint and POSTs the live-state and token endpoints (and the history's,
  section 4a); no control, fulfilment or bind call exists in the code (a test checks the client's URLs against an
  allow-list).
- AC6: each accepted reading carries its source (`bluetooth` or `cloud`): every accepted cloud reading and the first
  Bluetooth one after it are logged with it (unchanged repeats folded into a count, section 2), a rejected reading's
  log line names it, and `/api/health` gives
  `lastSource` (null before the first accepted reading).
- AC7: cloud readings that repeat (the cloud may cache) go through the same plausibility and agreement rules.
- AC8: logs and errors never contain the access or refresh token.
- AC9: when mains is off in the cloud answer (InAc 0), OutAc >= `staleLoadW` (100) and the cloud SOC has not changed
  for `staleAfterMin` (30; 0 = no check) since the cloud first gave that value, the reading is a miss logged as
  "cloud reading stale": the cloud may serve a frozen cache while the device shows online.

### 4a. Cloud history back-fill

The cloud keeps the station's own power curve. With the cloud fallback on and its token file present, the watcher
fills the gaps of `station.db` from it (`cloudHistory` false = off).

- Read only, enforced in code: `CloudHistoryReader` POSTs only to
  `/api/bluiotdata/dashboard/v2/getDevicePowerStatistics` and `/api/bluiotdata/dashboard/v2/getDeviceEnergyStatistics`
  on `gw.bluettipower.com`. Any other path throws before a request is built (a test proves nothing is sent). The
  body holds only the read query fields (`deviceSn`, `startDate`, `remoteFlag` false, `installerFlag` false, and
  `typeFlag` "pv" with `queryType` 1, or the energy `flag` 0 to 2). The token, headers and serial (`sn` of the device
  list) are the cloud fallback's. Neither the token nor the serial is logged. Its connection pool is its own and
  follows no redirect: a 3xx answer is a failed call.
- A gap is at least 10 minutes with no accepted reading (a raw row covers its minute, a 5-minute aggregate its five)
  in the last 30 days, from the oldest stored row on, up to 10 minutes ago. Each local day touched by a gap is asked
  once, newest first.
- Each 5-minute point (`chart_x` local labels, `chart_y` series soc, grid = AC in, ac = AC out, pv = DC in, dc = DC
  out) becomes one accepted row with reason `cloud history`, only where no accepted row lies in its 5 minutes.
  Rows are only inserted, a live row is never changed. Voltage, frequency and mode stay null. A point with no SOC
  above 0 is skipped (later points of today, hours the station sent nothing). The mark lives on the raw row only:
  after the raw retention (`rawRetentionDays`, 30) the row folds into its 5-minute aggregate like any accepted row, and the aggregate
  does not say which of its readings came from the cloud. The history and the battery-model refit read the rows
  without the reason either way.
- A day the cloud keeps only hourly (24 points) is skipped: the first two adjacent readable labels are not 5 minutes
  apart. On the fall-back day the repeated hour's labels are the
  summer hour the first time and the winter hour the second. Labels in the skipped spring hour are dropped. No point is
  invented.
- A day asked an hour or more after it ended is not asked again (meta `cloudHistoryDone`). Today is asked again
  while its gap stays.
- Runs 2 minutes after start, then once a day, and after an accepted reading ends a gap of 10 minutes or more. At most
  4 calls a run and 40 a local day (meta `cloudHistoryCalls`, counted before each call). One log line a call.
- msgCode 805 (expired token): the token is refreshed once a run (unless a cloud read already replaced it) and the
  same day asked once more, within the caps. A second refusal ends the run, the next run asks again.
- Its own task: a failed call is a log line, never a poll's fault. It shares the cloud reader's lock for the token
  and the serial: the first run (one device-list read; the serial it gives is kept in `station.db` (`cloudHistorySerial`), so later starts skip it) or a run that refreshes the token holds a cloud fallback read
  for those calls, at most about 75 s. A Bluetooth read never waits for it.

## 5. Mains, eco and manual control

- Mains lost: no AC input for `mainsLostSec` (180 s) in a row across accepted readings -> the cut begins and station
  eco is switched on. Mains back: AC input held for `mainsBackSec` (180 s) -> every mode is lifted (`off`).
  Unanswered, incomplete or implausible readings neither advance nor reset these timers.
- The owed lift on mains back lifts only modes set before mains was first seen back (the first accepted reading with
  input; flag `since` earlier than that time). A mode the user sets during the confirmation, or later, is never lifted
  until the next mains change. The lift is retried until `off` exits 0, then cleared.
- Manual control: buttons Auto, Eco and Max on the page (`POST /api/mode`, [api.md](api.md)). A manual choice holds
  until the next observed mains change (lost -> back or back -> lost) or Auto. A gap reset that finds mains in the
  same state as before is no change. Max during a cut = the lift (`off`): the return plan, no eco, while the page shows
  the hours of battery left at the current load. The POST saves the choice and returns 202 at once; the service loop
  applies it with `powerModeTimeoutSec` and the backoff.
- An observed mains change drops any page choice whose command has not run yet (it failed and waits in its backoff):
  the command never runs after the change. A `mode-dropped` event is stored with the reason, which is also the saved
  `modeError` ("Eco dropped: mains came back before it ran"). A cut found again after a gap is no change and keeps it.
- Max never lifts a station emergency. A hold = the flag says `emergency`, or the emergency level was reached in the
  current cut. Max or Eco pressed during a hold is refused (409). A page choice waiting in the service loop (Max, Auto
  lifting the page's Eco, and Eco, which `power-mode.ps1` refuses under an emergency) re-checks the hold on every
  attempt, retries after a failure and backoff included; when it holds, the command is dropped, never run and never
  retried, the choice falls back to Auto, a `mode-refused` event is stored and the page says why ("Max held back:
  battery emergency", "Eco held back: battery emergency").
- Unknown refuses: while the flag is unreadable a page lift (Max, or Auto lifting the page's Eco) is not run. It stays
  pending, is tried again with the normal backoff, and the page shows why (`modeError`).
- The last refusal or failure of a page choice (`modeError`) is saved with the state: a restart keeps it shown until
  the next mode change. `/api/now` carries it in `modeError` only, never repeated in `note`. A gap reset keeps a page
  choice that is still pending.
- The latest press wins: a press that replaces one whose command has not run yet stores a `mode-superseded` event
  ("eco replaced by max before it ran") and logs it, so no press vanishes unseen.
- An applied page choice reads the flag again right after its command: `/api/now` (`powerMode`, the eco chip) shows
  what the command wrote at once, never the poll's flag of up to a minute ago.
- What holds the mode is said under the buttons (`modeHold`): Max or Eco pinned until mains returns (in a cut) or goes
  (on mains), or Auto. Auto after a pinned Max or Eco runs no command of its own: the battery rules decide at the next
  reading, which the service loop takes at once instead of at its minute (`auto-next` until that reading; the page's
  settle read waits for it).
- The power-mode command's `plan:` lines (the plan recorded on eco and the one returned to) are logged with its
  `warn:` and `note:` lines, the battery call excepted.
- A stall is visible: a poll or a mode command that holds the station gate for more than 20 s logs
  `slow: the poll held the station gate for <n> s` (or `the mode change`). The flag reads (under the writers' named
  mutex, up to 5 s) run on their own thread, never on a thread-pool thread. A tool of the in-process switch that has
  exited but whose output stays open (a child it left running) is waited for at most 2 s more; then the call stops
  waiting, tries to stop the tool's tree and uses what it printed.
- While mains is off and the cut has not begun, `/api/now` gives `ecoAt`: the first-off time plus `mainsLostSec`, when
  the cut begins with eco if mains stays off (eco itself comes with the first accepted reading at or after it). The
  page counts down to it. `ecoAt` is null when no eco is pending: mains on or unknown, the cut already begun, the
  station silent, the flag not normal (eco or emergency on, unreadable), Max chosen during this outage, a streak at
  the emergency level running (the cut may bring the emergency instead), or the last accepted reading older than
  `maxGapSec` or ahead of the clock.

## 6. Battery levels and hibernate

Levels (defaults): `emergencyAt` 20 % -> station emergency (`on`); `stopAt` 10 % -> stop (the flag's level says
`critical`); `warnAt` 7 % -> hibernate warning; `hibernateAt` 5 % -> the PC hibernates (`shutdown /h`). They apply
always, manual Max included. While on battery the reading goes into the flag (`battery`) at each level change and at
least every `batteryRefreshSec` (600 s); this write is best effort.

- The level after the warning is `warning` (`/api/now`): the warning was given, the PC hibernates at `hibernateAt`.
  The flag's word for it stays `hibernating` (the hook's `the PC hibernates at N %: save now`). A state saved by an
  earlier version under the old name `Hibernating` loads as `warning`.
- Levels raised by the drain. At a high drain the battery can pass the warning and `hibernateAt` within a few polls,
  and the hibernate, which waits `hibernateAfterWarnSec` after the warning, would come far under `hibernateAt`. So each
  poll applies the configured levels raised by the measured drain `d` (the measured rate at the current load, section
  7; `poll` = the larger of `pollSec` and `fastPollSec`): `warnAt` at least `hibernateAt + d x (hibernateAfterWarnSec +
  2 polls)`, `stopAt` at least the warning plus `d x 2 polls`, `emergencyAt` at least the stop plus `d x 2 polls` (one
  poll for the ladder's own step, one as the margin; rounded up to a tenth, at most 100). The hibernate then comes at
  the first poll its own agreement allows at or under `hibernateAt`. `hibernateAt` never moves (the fresh-reading guard
  and the wake read it), a level never goes under its configured value nor more than 10 points over it (a long
  `pollSec` or `hibernateAfterWarnSec` would ask for more: the raise stops there), and a low drain leaves them as
  configured. `d` is capped at 120 %/h (above what a station of this class drains at its rated output). No measured drain (on mains, too little data, the newest stored reading older than
  `maxGapSec`, a failed history read, a value not finite or not above 0) = the configured levels. A raised level only
  brings its step earlier: a reading at or under the configured level always counts toward it, so the configured
  hibernate is never delayed. `/api/now` gives the levels in force as `effectiveThresholds`.
- Agreement for a level = two accepted readings at least `agreeSec` (90 s) apart with no accepted reading between
  them that contradicts the level. Misses and rejected readings between them neither break nor count toward it; only
  a gap reset does. An accepted reading whose mains state is unknown is not on battery: it breaks every level's
  agreement and leaves the mains timers as they are. A rebased reading neither counts nor breaks.
- Each level fires in a later poll than the previous one; one level per poll.
- A level counts as fired only after its action succeeded; a refused or failed action is retried on the next
  qualifying poll with every precondition re-checked.
- Hibernate only on battery, at or under `hibernateAt`, at least `hibernateAfterWarnSec` (180 s) after the warning in
  the same cut, and only after a fresh reading taken right before acting agrees. No fresh reading, no hibernate.
- Hibernation depends only on readings and time, never on a flag write or on an earlier level's action succeeding.
  The warning counts as given when it was attempted; emergency and stop failing do not hold back the warning or the
  hibernation.
- Rebase rule. Any rebase in a cut makes hibernate wait the long confirmation: while a rebase is remembered in the cut,
  hibernate needs `rebaseHibernateConfirmSec` (600 s) of agreeing accepted readings at or under `hibernateAt`, counted
  from the later of the streak's start and the latest rebase, every other precondition unchanged. A streak from before
  a rebase does not keep the normal rule: one garbage reading accepted before a rebase must not anchor a hibernation.
  A station recalibrating from 22 % to 4 % hibernates the PC 10 minutes after the first 4 % that counts, not before;
  sustained identical readings of 0 % for 10 minutes are treated as real.
- The confirmation is counted over the accepted readings that continue the streak: each adds the time since the
  accepted reading before it, at most twice the poll interval. A forward clock step counts as one such interval, never
  as minutes of agreeing readings; a run of misses makes the confirmation later, never earlier.
- Known cost: a station that keeps rebasing for the whole cut never hibernates the PC, since every rebase starts the
  confirmation over. The `hibernate-delayed` event and the page note make that visible.
- While the rebase rule holds hibernate back, one `hibernate-delayed` event is stored per rebase (remembered with the
  rebase through a gap reset) naming the earliest time hibernate becomes allowed; `/api/now` gives it as
  `hibernateDelayedUntil` with a note. Any accepted reading that does not hold hibernate back clears it. While a rebase
  may delay hibernate, the warning says the PC hibernates only after the confirmation.
- A backward clock step, which resets the decision state as a gap does, moves a remembered rebase to the new now and
  starts the confirmation over.
- After a hibernate action, an accepted reading more than 2 minutes after it means the PC resumed: the hibernate level
  re-arms with all its preconditions (agreement, warning 3 minutes earlier, fresh reading), whatever the gap length.
  Still at or under the level on battery, the PC hibernates again once they hold; this is intended.
- A failing action backs off: 1, 2, 4, 8 minutes, then every 10 minutes, each failure an event shown on the page; no
  retry loop at the poll rate.
- Kill switch: under the modes v2 the `autoHibernate` setting (§12); the file `no-hibernate` it replaces is carried
  into it at start (`autoHibernate` false) and renamed `no-hibernate.migrated`, so turning the setting on hibernates
  again. On the legacy switch path a file `no-hibernate` in the data directory turns hibernation off. It is checked on every poll and only
  its `hibernate-refused` event backs off, so removing the file is heard at the next poll. A dry run (`--dry-run`)
  logs every command and hibernation instead of running it; its `hibernate-skipped` attempt backs off whole.

## 7. Battery time

The hours of battery shown (`hoursLeft`, to empty; the DTEK module's battery time, to `hibernateAt`) inform only:
battery levels and hibernate never read the estimate. The levels read one number beside it, the measured rate at the
current load, and only to rise (section 6).

- Measured rate: the SOC drop over the trailing run of accepted readings off mains within `rateWindowMin` (60). The
  run ends at a reading on mains, a reading without SOC or a gap over `maxGapSec`; the span runs from the run's first
  SOC step to its last, so a plateau does not dilute it. It needs a drop of at least `rateMinDrop` points (3) over at
  least `rateMinSpanMin` minutes (20).
- With less, a SOC rise inside the run, or a rate that is not a positive number, the formula capacity x SOC x
  efficiency / current load stands (`capacityWh`, `efficiency`).
- Hours = (SOC - target) / rate x mean output over the span / current output, so a changed load changes the time at
  once; at most 30 days. The efficiency in the formula is the learned one, else `efficiency`.
- Learning: a clean window (no DC input, no single SOC step over `socJumpFloor`) measures mean output / (rate x
  capacity); the first is taken whole, each later one moves the learned value a quarter of the way, at most once per
  `rateWindowMin`; bounded 0.4-0.95 and never over `efficiency` + 0.05; kept in the database (meta
  `runtimeEfficiency`). A store failure while answering `/api/now` is logged once per kind and the answer goes on.
- Fitted model (`fittedRuntime`, on by default), used before the measured rate once it rests on at least 2 hours of
  clean data (as much as the prior weighs) and while there is no DC input (it was fitted without any; the measured
  drop includes what the panels give): battery
  drain = P0 + (1 + k) x output, in SOC points an hour = idle + per-Wh x output, fitted by least squares over the
  clean stretches of every stored cut (from one SOC step to a later one, at least `rateMinDrop` points over
  `rateMinSpanMin` minutes, no gap over `maxGapSec`, no DC input, no SOC rise, no step over `socJumpFloor`). The prior
  (no idle draw, `efficiency` for every load) enters as two hours of discharge at half and one and a half times the
  mean load, in pieces as long as the mean clean stretch (a stretch weighs its length squared in least squares, so
  this makes the prior weigh exactly two hours of the station's own discharge); a new station starts at its configured
  efficiency and its own data take over as they grow. A negative
  idle draw refits with none; k outside 0-1 at `capacityWh` is rejected for the prior. SOC drops fix only capacity /
  (1 + k), so P0 and k are read at the configured capacity. Hours = (SOC - target) / drain at the current load.
- Its range: the P10-P90 of the drain error (real minus forecast, points an hour) over leave-one-cut-out forecasts
  at 120 minutes ahead (else 60, else 30), published only with at least 30 of them from at least 5 cuts (a minute's
  error is close to the next one; the cut is the independent unit); `hoursLeftLow` = SOC / (drain + P90),
  `hoursLeftHigh` = SOC / (drain + P10), open when that drain may reach 0. Refitted from the newest 20 000 stored rows
  (about 14 days of one-minute readings; older history is not read) at most every 15 minutes while `/api/now` is
  asked, never on the poll; kept in meta `runtimeModel`. The refit reads the rows as stored (no bucketing) and the
  `eco-on` events.
- Eco mode: the model also learns what eco does to the load from the cuts whose `eco-on` event it finds. Per eco cut,
  the delay from the cut's first reading and the mean load in the 15 minutes after eco over the mean load before. The
  ratio is their geometric mean shrunk toward 1, the share is the eco cuts over the cuts long enough to have seen eco,
  both with two cuts of prior weight, so one or two eco cuts move them only part of the way. In a new cut where eco has
  not come on yet and the latest delay seen has not passed, the load is taken as current x (1 + share x (ratio - 1))
  from the median delay on: the time left already counts the saving eco is about to bring. Once the cut's `eco-on` is
  logged, or with no eco history at all, nothing changes: the loss model at the current load. The latest delay is
  capped at `rateWindowMin` (the service sees the cut's start only that far back). The profile is kept after the
  model in meta `runtimeModel`. A damaged one is dropped alone.
- Chosen by a backtest (`tests/BluettiWatcher.Tests/Backtest`, run on request with `BW_BACKTEST_DB` and
  `BW_BACKTEST_OUT`): every cut replayed, the SOC forecast 30, 60 and 120 minutes ahead scored on held-out cuts.
  An exponentially weighted load and an hour-of-day profile were scored too and did not earn their place over the
  current load. The eco profile below did, and ships.
- `/api/now` gives `runtimeSource` (`model`, `rate`, `learned` or `config`; null with no estimate), `ratePerHour`
  (the measured rate whenever the window yields one, with `rate` or `model`), `hoursLeftLow` / `hoursLeftHigh` (with
  `model`) and `learnedEfficiency`. The page shows the range as a clock interval and a cone on the chart, and names the
  method in the time-left tooltip.

## 8. Wake during a cut

A PC hibernated in a cut stays asleep after mains returns until someone wakes it. The service can wake it with a
scheduled task (`wakeTaskName`, default `BluettiWatcherWake`). Off by default (`wakeEveryMinutes` 0) until the user has
seen this PC wake from hibernation by a timer (README).

- a. `wakeEveryMinutes` (10-1440; 0 = off): just before the service hibernates the PC it arms the wake task for that
  many minutes later (`wake-armed`). At or under `wakeFloorPercent` (not set: `hibernateAt` - 5, at least 0; set:
  bounded to 0..`hibernateAt`; 0 = no floor) no wake is armed and one still ahead is turned off (`wake-not-armed`):
  the wakes must not drain the station. The battery is the reading the hibernation acted on (with none, the last
  accepted one).
- b. A wake that cannot be armed never holds a hibernation back: `wake-arm-failed`, and the PC hibernates; arming
  holds it 15 s at most. An arm that failed may have landed before its timeout: the turn-off is then owed at the next
  reading on mains.
- c. Any accepted reading on mains turns the wake off (`wake-disarmed`); the PC stays awake.
- d. Woken by the timer (e) and still in a cut: the first accepted reading of the wake that is on battery at or under
  `hibernateAt` hibernates the PC again at once, after the fresh reading every hibernation takes: one that is missing,
  rejected, over the level or on mains refuses it, and one on mains ends what the wake owed and turns it off. A fresh
  reading taken by a hibernation in the same poll is the one the wake hears; a rejected one says nothing and the
  poll's reading stands. The resume rule of section 6 is for a wake by hand and does not apply here. The next wake is
  armed then.
- e. The timer woke the PC when the first poll more than 2 minutes after the hibernation falls between 2 minutes
  before and 10 minutes after the armed wake time; from then until a reading on mains a hibernation is owed. With no
  accepted reading for `wakeReadSec` (300, bounded to 60..3600), counted from the last accepted one (from the resume
  before the first), the PC hibernates again without a fresh reading (event `hibernate`), the next wake armed. This
  and (d) are the only hibernations outside section 6's rules: the service itself put this PC to sleep in this cut,
  and an awake PC that cannot read the station would drain it. A PC woken by hand outside that window is never
  hibernated without a reading. The kill switch and a dry run hold both.
- f. The first accepted reading on mains after a service start turns the wake off whatever was saved (a crash between
  arming and saving must not leave it on); the armed state is saved in the database (meta `wake`).
- g. A failed turn-off is retried with the action backoff, each failure a `wake-disarm-failed` event; `/api/now`
  `wake` (`everyMinutes`, `floorPercent`, `armed`, `wakeAt`, `disarmOwed`, `error`) and a note show it until it
  succeeds.
- h. The task operations are pwsh `Set-ScheduledTask` (the trigger's time, enabled; a task without WakeToRun is
  refused) and `Disable-ScheduledTask`, windowless, with `powerModeTimeoutSec` (not `actionTimeoutSec`); a dry run
  logs them only.
- i. The installer registers the task (`tools/wake-task.ps1`): disabled, WakeToRun, one time trigger, a no-op action
  (System32 `conhost --headless cmd /d /c exit 0`) as the user, interactive token, least privilege; security:
  Administrators, SYSTEM and the user full control, so the unelevated service may set it. The name comes from
  `wakeTaskName`: 1 to 100 characters of letters, digits, space, `.`, `_` and `-`, never the service's own task
  `BluettiWatcher`; an existing task of that name is replaced or removed only when it reads back as the one this
  installer registers, otherwise the installer stops and names it. It is registered from XML
  through the Task Scheduler COM API and read back: the XML and the access list, a whitelist over every DACL entry
  (type `A` only; full control for Administrators, SYSTEM and the user, each on the task itself; any other account
  reading only; any deny, other type, other flag, unknown right or wider grant refuses the task, naming the entry).
  `install.ps1 -RemoveWake -Apply` removes it.

## 9. Actions

Config `actions` (optional) maps a state to a command line: `eco`, `emergency`, `stop`, `hibernate-warn`, `normal`.
The command runs when the service enters that state, windowless, with a timeout of `actionTimeoutSec` (30 s, clamped
to 1..600; `powerModeTimeoutSec` is a separate limit for the power-mode and wake-task commands). Its exit code and the
first 300 characters of its output are logged as an event. A missing, failing or hanging command never blocks or
changes a decision. Use it to stop a heavy program when eco starts, or to notify someone.

- The line is an executable followed by its arguments, started directly: there is no shell, so a pipe, a redirect or a
  built-in needs `cmd /c ...`. The environment carries the four `POWER_MODE_*` values (`POWER_MODE_FILE`,
  `POWER_MODE_MUTEX`, `POWER_MODE_TASK_PREFIX`, `POWER_MODE_EXTRA_READERS`).
- An action fires once per entry into a state per power cut. The memory resets when mains returns, so the next cut
  fires again; `normal` fires once per lift.
- The page's Max and Auto-lift fire `normal`; the page's Eco fires `eco`.
- `grid-voltage-high` is an alert, not a state: it fires when the grid input voltage (AC200PL over Bluetooth) goes above
  253 V (230 V + 10 %), with an event of the same kind, and again only after the voltage was under 250 V. The
  memory is saved with the state, so a restart does not repeat it.
- A dry run logs the actions (and their environment) instead of running them.

## 10. Page and times

- One HTML file with no external libraries. The battery ring takes its colour from the configured levels, with the
  emergency and stop zones marked and a tooltip per zone. Charts span 1 hour to 30 days (7 and 30 days at 5 and 15
  minute steps). A battery card sums the last 30 days: cuts, longest and average cut, energy from the battery, deepest
  discharge, load on battery. The layout fits a phone (tooltips on tap), Full HD and 2K (scaled up from 2200 px).
- The DTEK banner checks the plan against the last reading (at most 10 minutes old): power back before the planned end
  shows green, an outage the schedule does not have shows amber.
- The hours of today already past carry what happened, hatched over the plan: each minute takes the state of the last
  accepted reading (mains or battery) while the next one comes within 10 minutes, else it is unknown. Runs of one state
  are drawn as phases in clock order on one stripe layer, and the cell tooltip lists each phase with its span.
- The page speaks English and Ukrainian. The default is the browser's language (`uk*` -> Ukrainian, else English); the
  switch in the header remembers the choice in the browser.
- Prose the page shows also comes with a stable code it translates by (`notes`, `ecoNote`, `modeErrorNote`,
  `dtekWatcher.notes`: `{code, text, args}`; `modeCode`, the station's output mode; the mode POST's `code`); an unknown
  code shows the server's text. A rejected reading's reason has its own codes (`reading-rejected-incomplete` with the
  missing fields as value codes, `reading-rejected-implausible` with the two levels and the seconds); logs and the
  note's `text` stay English.
- The rejected-reading note stands only while the newest reading is the rejected one (at most 6 hours): a reading
  accepted after it ends the note; the log keeps the rejection.
- Times shown to people (page, notes, log texts) use the PC's local time zone. API times are ISO 8601 with the local
  offset. DTEK schedule math stays in Europe/Kyiv time, because the source publishes Kyiv wall time.

## 11. DTEK module (planned outages, Kyiv region)

A module that reads the distributor's planned-outage schedule for the user's address and shows the next outage, the
expected return and whether the battery lasts until then. Its code is in `src/BluettiWatcher/DtekWatcher/`; the host
knows it only through `IDtekWatcher`: `RunAsync` (start, stopped by the service's token), `AfterPoll(state)` (the hook
for "a cut began" and "mains is back") and `View(...)` (the object for `/api/now`). Removing the module is dropping
those three calls and the `dtekWatcher` config property.

1. Purpose: show the next planned outage and the return of power on the page and in `/api/now`, and during a cut say
   whether the battery lasts until the return. The module informs; it controls nothing.
2. The schedule never influences eco, max or hibernate. No code in `Decision/` reads schedule data or the module's
   config; a test pins it.
3. The module finds the house's queue itself. It compares each real cut (from the first reading off mains to the
   first reading back on) with the planned windows of the house's queues. Two counted cuts in a row for one queue fix
   it; until then all the house's queues are shown. A later cut that matches other queues of the house and not the
   fixed one opens the question again. An unplanned cut (it matches no queue) neither counts nor contradicts. A house
   with a single queue needs no matching. A fixed queue that is no longer among the house's queues is dropped.
4. Requests: one `HttpClient`, HTTPS only, on the default port, no proxy, to exactly two hosts: the dataset's (rule
   15) and the site's (`dtekWatcher.host`, default `www.dtek-krem.com.ua`). A redirect is followed only inside the
   host that was asked (at most 3); one to another host, scheme or port is refused before any request leaves. TLS
   validation is the platform's. Each request has 30 s. A body over 4 MB is refused. The site's page is read line by
   line and only its token and schedule line are kept. A refused redirect and an oversized body are the reason class
   `failed`, each with its own text.
5. Cadence: the dataset every 30 minutes (rule 15); the site per rules 17 and 23. The address request needs the
   page's token and cookies, so it rides on a site read.
6. On failure (a source down, blocked, format changed, no internet during a cut) the schedule saved on disk stays
   shown with both its times (rule 16); after the end of the days it covers: "no schedule". A challenge page of the
   site's shield is `blocked` and an answer that does not fit is `format`: neither is ever read as "no outages".
7. During a cut the page shows the outage kind and the expected return the site gives (as written when a date cannot
   be read), and only when the site answered during this cut.
8. Configuration lives in `config.json` in the data directory, never in the repository: `dtekWatcher.enabled`
   (default false: the module is off, creates no client and makes no request), `queues`, `group`, `datasetUrl`,
   `staleAfterMin` (90), `minCutMin` (15, rule 29), `site` (default false: the dataset only; true opts in to reading the distributor's website, which is subject to its terms), `host`, and the optional address for the site only: `city`, `street`,
   `house`. Enabled with a dataset address that is not plain https or a host that is not a plain host name: off, with
   the reason in the log and the note.
9. A cut matches a queue's window when both its start and its end fall within 20 minutes of the window's borders
   (half-hour cells give borders on :00 and :30). Only a cut that matches exactly one of the house's queues counts:
   queues whose windows are the same that day do not tell each other apart. Cuts with no schedule saved for their time
   do not count, nor do cuts whose start or end fell in a gap of readings longer than 5 minutes (a hibernated PC, a
   service started inside a cut). "Possible" cells are never planned cuts for matching.
10. Read errors: per source (`dataset`, `site`) one event per failure streak (`dtek-watcher-failed`, with the source,
    the reason class `network`, `blocked`, `format` or `failed`, and the detail) and one on recovery
    (`dtek-watcher-ok`), and a note on the page. Retries come on the source's normal cadence, never faster.
11. "Battery lasts": the hours-left estimate at the current load against the expected return; while the queue is
    unknown, the latest return among the house's queues; `unknown` when either side is unknown.
12. Stored (meta key `dtekWatcher`): only the schedule in use (today and tomorrow, its source and both times), the
    dataset's validator, the site's last address answer, the match counters, the fixed queue, the manual queue's hint
    (rule 21), the running cut's start, and per source the last read time and failure. No schedule history.
13. The module never talks to the station.
14. Data and `/api/now`.
    - Days (today and tomorrow, Kyiv time) of queues `GPV1.1` .. `GPV6.2`, each with 24 hour cells; hour N runs from
      (N-1):00 to N:00. Cells: `yes` on, `no` off, `first` / `second` off in the first / second half hour, `maybe`,
      `mfirst`, `msecond` a possible outage (shown as "possible"), anything else or a missing hour unknown (never
      guessed). Windows that touch are one outage, across midnight too.
    - Cell times are Kyiv wall time turned into instants by the zone's rules. On a clock-change day the windows stay
      where the source shows them and the day is 23 or 25 hours long; the hour the clock skips has no time and the hour
      that comes twice is off both times.
    - `/api/now` carries one object `dtekWatcher`; its fields are listed in [api.md](api.md). The page shows one block,
      hidden while the module is off; every text from the site is escaped.
15. Primary source: the public dataset
    `https://raw.githubusercontent.com/Baskerville42/outage-data-ua/main/data/kyiv-region.json` (MIT; that project
    collects it from the distributor's sites), address in `dtekWatcher.datasetUrl`. Its `fact` is the site's
    `DisconSchedule.fact` unchanged (`data` -> Unix day -> queue -> hour -> cell, `update`, `today`); its top-level
    `lastUpdated` (ISO, UTC) is when the dataset was collected. Read every 30 minutes with the host's validator
    (`ETag` / `If-None-Match`), sent only while a schedule is held. 304 means the dataset was not collected anew: the
    schedule and its collection time stay, so the age shown is always the dataset's `lastUpdated`.
16. The distributor changes schedules during the day, so the dataset can lag. (a) The page and `/api/now` show both
    times - the distributor's own update time of the schedule in use and when it was collected - and the source in
    use. (b) The schedule in use is the one with the newest distributor update time among what the sources returned;
    the same update collected again only moves the collection time (and the source to the later collector); with
    update times that cannot be read, the later collection wins. (c) State `stale` when the newest collection is older
    than `staleAfterMin`, with a note, while the covered days are still shown. (d) A `dtek-watcher-changed` event when
    the windows of a shown queue change between two schedules on a day both have, with both texts, once per change.
17. The site is best effort: one attempt per hour on mains and one per 15 minutes during a cut. A challenge answer is
    state for that source only (`siteState`), raises `dtek-watcher-failed` at most once per streak, and never changes
    the module's `state`. After 24 challenge answers in a row the site is tried once every 6 hours, in a cut too,
    until it answers anything else. `dtekWatcher.site: false` turns the site off.
18. The house's queues: `dtekWatcher.queues` when present; otherwise the ones the site's address answer named when it
    has ever succeeded (saved); with neither, the state is `none` and the note asks for `queues`.
19. The module can never hurt the host, and nothing untrusted is stored or used unbounded.
    - Boundary: the host reaches the module only through a total guard: no exception from building it, loading its
      state, `AfterPoll`, the view or its run reaches the poll loop or `/api/now`. A fault is logged once per distinct
      message per hour and raises one `dtek-watcher-failed` event per streak (`module: <type>`); the view is then
      `state: none` with a note, and `/api/now` still answers 200. A store that fails on a save keeps the state in
      memory and the next change saves it; a store that fails on an event loses that event only.
      `"dtekWatcher": null` is the default (off). A saved state that is damaged, wrong-typed or out of bounds starts the
      module fresh.
    - Reads: every outcome of a read, an unexpected exception included, records the attempt, sets that source to
      `failed` and obeys the cadence. The dataset's validator is stored only for a body that was taken in.
    - Bounds; an answer outside them is refused whole as `format` and the held schedule stays: at most 4 days and 24
      queues a day; queue names `GPV<1-2 digits>.<1-2 digits>` only; hours 1 to 24 only; no invalid string (a lone
      surrogate) anywhere; a day key that is not a Kyiv day start (within 2 hours, for clock changes) or lies more than
      2 days back or 7 ahead is refused; an update time more than 24 hours ahead of now is refused. A collection time
      more than 10 minutes ahead of the clock is taken as now; a negative age counts as 0. The `update` text is cut to
      40 characters, the site's outage texts to 120, control characters removed; the site's queue names of any other
      form are dropped.
    - An update time that cannot be read, against a held one that can: the candidate is taken only when its content
      differs and it was collected later, and the note says the update time cannot be read.
    - Events: `dtek-watcher-changed` only when the planned-off half hours of a shown queue differ (never for a text,
      an order or a "possible" cell), at most one per queue and day in 10 minutes; `dtek-watcher-failed` / `-ok` at
      most one pair per source in an hour.
    - Matching: a finished cut is compared with the house's queues of the schedule held when the cut began. When their
      planned windows differ from the schedule in use at the cut's end, the plan changed during the cut and the cut
      neither counts nor contradicts.
20. `dtekWatcher.group` is a hint, never a permanent override: the distributor moves houses between queues.
    - It is the queue followed from the start. The queue search of rule 3 keeps running under it.
    - Every real cut that counts by rule 9 is also compared with the manual queue. A cut that matches exactly one
      queue, and not the manual one, contradicts it. Two such cuts in a row for the same queue refute it: one
      `dtek-watcher-queue-changed` event (`<manual> -> <detected>: ...`), and the detected queue is followed from then
      on as the search's fixed queue (a later contradiction opens the question again; the manual queue does not come
      back).
    - A cut that matches the manual queue (alone or with others) ends the row. So does a cut that fits several queues
      without the one being counted; one that fits several with it leaves the row as it is. An unplanned cut says
      nothing: the row stays. One contradiction alone changes nothing.
    - A manual queue that is not among the house's queues is followed all the same and compared with the cuts as one
      more queue; the note says it is not among them.
21. The hint is stored with the module's state: the config value it is about, the queue and count of the running row,
    and the queue that refuted it. It belongs to that value only: the same value after a restart is still refuted; a
    value edited to another one is a fresh hint, and what was counted against the old one is dropped. A row begun
    before a restart goes on after it. A hint that is missing, damaged or out of bounds is a fresh one.
22. `/api/now` `dtekWatcher` gives `queueOrigin` (`manual` the manual queue stands; `detected` no manual queue, the
    queue is the matched or the only one; `manual-refuted` the manual queue was refuted, `queue` is the detected one
    or null while the question is open; "" no manual queue and no queue found) and `queueContradictions` (cuts in the
    running row against the manual queue). The page shows the origin, and a running row, beside the queue; a refuted
    manual queue is also in the note.
23. Outside a cut the site is tried at most once in 60 minutes, on its own clock. Whatever the attempt ended with,
    the next one waits for the next hour; there is no retry. During a cut rule 17 stands. A challenge is recorded as
    `blocked` and nothing is done about it.
24. Overdue. The window a cut belongs to is, among the shown queues, the first planned window of a queue that holds
    the cut's start (from 20 minutes before the window), stretched over that queue's later windows that begin, less 20
    minutes, by now (or by the cut's end); among queues, the latest end. It is read in the schedule in use, with the
    days it no longer has taken from the schedule held when the cut began (a cut across midnight).
    - During a cut, when no planned window covers now and the cut's window has ended, the cut is overdue from that
      window's end on. The plan then gives no return: `return` is the site's end or null.
    - A window that covers now, new or edited, is followed again, and the cut is no longer overdue.
    - A cut that began in no planned window is never overdue. Overdue is computed from the saved running cut and the
      schedules on each view, so a restart during a cut shows it again.
    - The battery time is now plus the hours the battery lasts at the current load down to `hibernateAt`, not to
      empty; an estimate that is missing, zero, negative or not a number gives none; one above 30 days counts as 30
      days.
25. Lateness. Each finished real cut whose start and end are both known (rule 9), taken while one queue is shown,
    and that belongs to a planned window (rule 24, read at its end) is recorded: its start and end, the queue, the
    window's Kyiv date, and actual minus planned in whole minutes for start and end, against the window as it stood
    when the cut ended and, when the schedule held at its start had one, against the window seen then.
    - The usual lateness is the median of the end deltas of the recorded cuts, a half rounding away from zero, given
      only from 3 cuts.
    - Stored under meta key `dtekWatcherLateness`: the last 60 cuts, one entry per cut start. A list that is damaged,
      wrong-typed or of an older form is empty; entries out of bounds (a queue name over 16 characters, a set of more
      than 4, an end not after the start, a delta over 14 days) are dropped.
    - A cut shorter than `minCutMin` (rule 29) is not recorded.
26. Plan edits during a cut: every schedule taken in while a cut runs that moves a shown queue's planned-off half hours
    counts once; the count is saved with the running cut and ends with it.
27. `/api/now` `dtekWatcher` gives `overdue`, `overdueMin`, `plannedReturn`, `batteryUntil`, `planChanges`,
    `lateBackMin` and `lateCuts`. While overdue the page shows the planned return struck through and "overdue N
    min", and the time the battery lasts until (hibernate at N %); during a cut with plan edits, "plan changed N
    times during this cut"; from 3 cuts, "usually back N min after (or before) the plan".
28. The house's queue can be a set.
    - a. `dtekWatcher.group` takes a queue name or an array of them; an array is held as one name, its members joined
      with `+` in the configured order, blanks and repeats dropped (`["GPV1.1", "GPV2.1"]` = `GPV1.1+GPV2.1`). At
      most 4 queues of at most 16 characters, else the module is off with the reason. A changed member order is an
      edited value: a fresh hint.
    - b. The set's planned outage is the union of its members' planned-off half hours: a half hour is off when any
      member's is, else unknown when any is, else possible when any is, else on. A day that lacks one member has no
      union that day.
    - c. Everything that reads the followed queue reads the union: the next outage, `return`, `batteryLasts`,
      overdue and `plannedReturn`, the windows shown, the plan-change events and counts. `/api/now` adds `queueSet`,
      `nextQueues` and, per `windows` entry, `intervals`.
    - d. A cut is overdue once it outlasts the union's window it belongs to, not one member's.
    - e. The hint holds for a set as for one queue: a cut that fits the union ends the row; a cut that fits exactly one
      other queue (a member alone included) contradicts it; two such in a row for one queue refute the set.
    - f. Lateness is recorded with a set too: the entry names the set and says `union: true`; its deltas are against
      the union's window.
    - g. The page shows the set as `1.1 + 2.1`.
29. Short cuts. `dtekWatcher.minCutMin` (default 15, 0 to 1440, else the module is off with the reason; 0 = every cut
    counts) is the shortest cut, from its start to the confirmed return, that counts for the module's statistics. A
    shorter cut is a blip: it adds no lateness entry (rule 25), so it cannot move the usual lateness, and it does not
    count in the queue search (rule 9) or the manual queue's hint (rule 20), where a 4-minute blip near a 30-minute
    window would otherwise fit it (both ends within 20 minutes). A cut of exactly `minCutMin` counts. Short cuts stay
    in the cut history and on the chart, and a running cut is overdue and shown as before: the minimum applies when
    the cut ends.

## 12. Power modes (`modes.engine` = `v2`)

On by default since 1.8.0 (`modes.engine` = `legacy` keeps §5's fixed eco). Code in `src/BluettiWatcher/Modes/`; API
in [api.md](api.md), Power modes.

1. A **mode** is a named list of actions (`{ typeId, enabled, enforce, settings }`) plus the flag word it maps to
   (`normal`, `eco`, `emergency`). Exactly one mode is active. Built-ins `normal`, `eco`, `max` are editable, never
   deletable; a missing one is re-added at start.
2. An **action module** (`IActionModule`) is one capability: its descriptor (type id, settings JSON Schema, UI hints,
   discovery list names, restoration class, needs-admin, default settings) and an adapter that discovers choices,
   validates settings, expands them into **resource intents** (one per field: `windows.activePlan`, `gpu/0/powerLimit`,
   `service/<name>/running`), reads, compares and writes one resource, answering with the value read back. Modules
   are registered by hand (`ModuleCatalog`, no reflection: NativeAOT); the registry refuses a duplicate type id or
   defaults that fail their own schema. Settings are checked against the schema (type, properties, required,
   additionalProperties, enum, minimum, maximum, minLength, maxLength, items, maxItems) and the module's own checks
   before they are saved. A mode id is 1-32 of `a-z 0-9 -`; `auto` is reserved (it selects the rules). The modules
   (restoration class in brackets; * = writes through the admin broker, §13):
   - `windows.powerPlan` (reversible): the active power plan.
   - `windows.cpuPolicy` (reversible): the processor's maximum state and boost mode of a plan, on mains and on battery
     (`plan`: a GUID or `active`; with `active` a plan switch during the mode leaves the old plan holding the mode's values).
   - `display.offTimeout` (reversible): the display-off timeout of the active plan, on battery and on mains.
   - `gpu.powerLimit`* (reversible): the NVIDIA board power limit of one GPU (`gpu`: its index), set as exactly one of
     `percent` of its default limit, `watts`, or `limit: "min"` (the card's own minimum, read from nvidia-smi: a percent
     cannot name it portably); the broker checks the card's min..max again. Eco's built-in GPU action is `limit: "min"`.
   - `display.refreshRate` (reversible), `display.brightness` (reversible, DDC/CI monitors).
   - `nvidia.profile`* (compensatable): a Profile Inspector `.nip` imported, the customised profiles exported first
     and imported back on leaving. Its read is a broker export, so it is read and written on entering and leaving the
     mode, not at every poll; a failed read is tried again at the next poll.
   - `gpu.idleWatcher` (token): writes nothing; logs the processes holding a GPU that stays in P0 at a high clock with
     next to no load, once per idle stretch.
   - `service.state`* (reversible): a service's running state and start type (Windows Search, Xbox gaming services, ...).
   - `scheduledTask.state` (reversible): a scheduled task enabled or disabled.
   - `process.close`, `windows.phoneLink` (irreversible): apps closed, Phone Link closed.
   - `command.pair` (compensatable): a named command (`modes.commands`) on entering and another on leaving. The module
     cannot read what its commands changed, so its read is its memory of what it ran; after a restart the reconciler
     hands it the journaled target of every pair it still owns (`IRecallsWrites`), so leaving still runs the leave
     command.
   - `rgb.lighting` (compensatable): the RGB lighting off through OpenRGB's SDK server (127.0.0.1:6742). Entering turns
     the listed devices (none = all) off and reads their colours back; leaving loads the OpenRGB profile the settings
     name, or leaves the lighting off when they name none. Not a snapshot restore: OpenRGB cannot read what some
     controllers show, and its own device detection resets some (a Gigabyte RGB Fusion 2 board), so the "on" state is
     the profile the person saved. When no server answers, a write starts one headless, windowless instance
     (`OpenRGB.exe --server --noautoconnect`, no tray icon) under the watcher's user and leaves it running, so the
     detection runs once; the catalogue, the page and a disabled action never start it (the device list is shown only
     while a server runs; the profile list is read from OpenRGB's profiles folder). The command line is not used: it
     prints only to an inherited console, so its writes cannot be read back. OpenRGB.exe comes from config.json
     (`modes.openRgbPath`) or the install folders, never from a mode; missing = unavailable. Its read is its memory of
     what it did, recalled from the journal after a restart (`IRecallsWrites`), as `command.pair`. No admin rights: the
     USB and GPU controllers answer to the user; RAM lighting (SMBus) would need OpenRGB elevated and is not covered.
3. **Selection.** A manual pick holds until Auto. Auto takes the first rule (in order) whose condition holds:
   `mainsOffFor` / `mainsOnFor` N s (mains as the decision confirmed it), `socBelow` X %, `cutWithin` N min (the DTEK
   module's next planned outage, §11; 0 while a cut is on; with the module off or failing it never matches), `timeWindow` from-to local time (may wrap midnight). A rule in force keeps matching within its
   hysteresis (SoC points, minutes; a mains rule for its whole spell); when no rule matches, the last mode stays.
   Unknown inputs match nothing. The default rules reproduce §5: `eco` after `mainsLostSec` off, `normal` after
   `mainsBackSec` on. A battery emergency (§6) overrides both: the mode's actions stay and the engine never writes
   over the flag's `emergency` while the station's emergency level holds in the cut that is on (the decision's state,
   not the flag); once mains is back the engine writes the mode's word over the emergency left in the flag.
4. **Reconciler.** Each poll, and at once after an API change, for every resource of the active mode: read; a failed
   read writes nothing and claims nothing (status `failed`); equivalent to the target = `satisfied`, nothing to undo;
   else `Prepared(before, target)` is journaled, the module writes and reads back, `Applied(written)` is journaled and
   the resource is owned. A resource is ours only while it reads what we wrote. A resource the person changed is let go
   (`relinquished`, event `mode-drift`) and not touched again while the mode stays active, unless its action has
   `enforce`: then it is written back on that poll. Leaving a mode restores each owned resource the new mode does not
   set to its baseline (the value before the first write), only while it is still ours; a failed restore is retried on
   later polls, at most 3 times, then reported (`restore-failed`, event `action-failed`) and given up. A resource the
   next mode also sets keeps its first baseline. Irreversible actions (closing apps) are never undone. One action's
   failure never stops the others. A failed write is tried again after 1, 2, 4 ... minutes, at most 30 apart, and
   alerted once (event `action-failed`) per resource and mode entry; entering a mode or a write that succeeds starts
   afresh.
5. **Journal** `modes-journal.jsonl` in the data directory: one JSON line per record, written and flushed to disk
   before the step it records goes on. Replayed at start before any write: a `Prepared` with no outcome is settled by
   reading (the target = owned, the baseline = dropped, anything else = relinquished; an unreadable resource stays
   open and is never written over); a token resource (a pause) written before the last boot is let go. A torn last line
   (a crash mid-write; never acted on) is cut off. Any other unreadable line: the journal is moved aside
   (`.corrupt-<unix>`), nothing is restored, the fallback mode (`normal`) is held as a manual pick until Auto is
   pressed, and the problem is shown in `/api/modes` and as event `modes-problem`. Compacted at each start and past
   5000 records: kept are the records a replay rebuilds each owned resource from, the last mode entered and the last
   200 records (sequence numbers unchanged); written to a temporary file and moved over the journal.
6. **Settings** `modes.json` in the data directory, versioned, replaced atomically. First start: an install that ran
   the switch before (its flag or `power-mode.config.json` exists) is migrated: `returnPlan` becomes Normal's plan
   (`previous` = no plan action, so leaving Eco restores whatever plan was active before it), `gpuCap` turns Eco's GPU
   limit on or off; a fresh install gets the public defaults (Eco =
   Power saver, its GPU limit, a command pair, services, Phone Link and the GPU idle watcher listed but off; Max = High
   performance). The config's `actions.eco` / `actions.normal` become the commands `eco` / `normal` and the built-in
   Eco's command pair runs them on entering and leaving Eco (configuration.md, Power mode and actions); under `v2` the
   station's eco and mains-back no longer fire them. The other `actions` keys fire as before. A file this build cannot parse is moved aside and
   replaced; one from a newer build is left alone and the defaults run in memory.
7. **Flag.** The active mode's flag word is written through the switch's own flag code (same lock, same format; the
   battery fields and any plan or GPU limit an older eco still owes are carried over), so its readers are unchanged.
   Under `v2` the station's eco and mains-back run no switch command; the station emergency still does (§6).
8. **Page.** The station page has a Power modes panel, shown only under `v2` and asked nothing while it is closed (open:
   the catalogue once, `/api/modes` each minute; `/#modes` opens it): Auto or a mode, why the mode is in force (the
   rule in the page's language), a chip per action state with its reason, the hibernate setting, the modes (built-ins
   not deletable) with an editor whose settings forms are generated from the catalogue (api.md, Power modes), and the
   Auto rules with up/down ordering and an editor per condition kind (times as `HH:mm`, 24-hour). A refused save shows
   each problem at its field and keeps the edit; an emptied rule value is refused before it is sent.

## 13. Admin broker (power modes)

Some mode actions need administrator rights (a service's start or start type, a scheduled task, a GPU power limit, an
NVIDIA driver profile). The service never runs elevated; it asks one SYSTEM scheduled task, `BluettiWatcherBroker`, with
no trigger, which it starts on demand (`schtasks /run`), and waits up to 10 s for the answer.

- Folders: what SYSTEM trusts sits in `%ProgramFiles%\BluettiWatcher\broker` (`bin\` with the copy of
  `BluettiWatcher.exe` the task runs, `policy.json`, `profiles\`, `exports\`, `broker.log`); the requests and answers
  in their own root, `%ProgramData%\BluettiWatcherBroker` (`requests\`, `responses\`), never inside the flag's folder,
  which the user may change. Both roots are owned by Administrators with inheritance cut: Administrators and SYSTEM
  full control, the user read and traverse only; in `requests\` the user may also add files (never folders), and on
  the files it adds read, write and delete (the client writes `.tmp` and renames it). Every child is reset to inherit
  only that (a pre-created one loses its own entries). SYSTEM never runs the exe in the user's own bin, which the user
  can replace, nor an exe from the exchange root. Before anything changes, the install refuses (exit 1) when an
  existing ancestor of either root is owned by the user or grants the user delete, delete-child, change-permissions,
  take-ownership, add-file or add-folder (the groups every user is in: the first four) - an ACL that cannot be read
  refuses too; after setting them it reads the ACLs back and fails on any difference. The policy's user is the user
  the service's logon task runs as (its principal), never the elevated caller. A 1.8.0 pre-release broker under
  `%ProgramData%\BluettiWatcher\broker` is carried out (its policy and `.nip` profiles only when Administrators own
  them) and removed. `install.ps1 -Broker -Apply` (elevated) creates it all; the normal `install.ps1 -Apply` runs
  that step too when its shell is elevated, else it names it as the step left to run. Once the broker is registered, the switch's old GPU cap tasks (`<prefix>GpuCap`,
  `<prefix>GpuRestore`) are removed: the broker's `gpu.setPowerLimit` replaces them. After an upgrade the copy is
  stale until that step runs again: the service compares the copy's file version with its own and names a missing or
  older broker in `/api/health` (`broker`), and an older one at start as event `broker-stale` (a tray notice).
- Request: `requests\<id>.json` (`{ v, id, op, args, issued }`, id = 32 hex digits, written as `.tmp` and renamed into
  place); answer: `responses\<id>.json` (`{ id, ok, error, value }`), pruned after 10 minutes. The broker logs every
  request to `broker.log` in its Program Files folder.
- Default deny. A request runs only when: `policy.json` is a plain file owned by Administrators (or SYSTEM) that no other
  account may write, and parses strictly (an unknown key, a wrong type or a bad name invalidates it - then everything
  is refused); the request file is a plain file (no link, one name) owned by the policy's user or an administrator,
  opened without following links and deleted through the same handle; it is under 4 KB, well-formed, issued within the
  last 30 s; its operation is known and its arguments are exactly the operation's.
- Operations: `service.query|start|stop|setStartType` and `task.enable|disable` for names the policy lists;
  `gpu.query`, `gpu.setPowerLimit` within the GPU's own min..max read at the time; `nvidia.exportProfiles` (Profile
  Inspector `-exportCustomized`; the file is moved into `exports\` and answered with its SHA-256) and
  `nvidia.applyProfile` (`-silentImport -replaceImport`) for a listed `.nip` in `profiles\` or a file in `exports\`.
  Profile Inspector must sit under Program Files or the broker folder. Nothing from a request reaches a shell: services
  and tasks go by their policy spelling, numbers are re-formatted, files are names joined to a fixed folder.
- `BluettiWatcher.exe --broker-allow service|task|profile|inspector <name>` (elevated) adds to the policy.

## 14. Tray and notifications

1. The icon lives in the service process: one more thread (STA) with a hidden top-level window and a blocking
   `GetMessage` loop, no timer. Win32 through source-generated P/Invoke (`Shell_NotifyIconW` with
   `NOTIFYICON_VERSION_4`, `TrackPopupMenuEx`, `CreateIconIndirect`), NativeAOT-safe, no WinForms or WPF. On by default
   (`tray`), only in an interactive session; `--no-tray` turns it off. Measured once (NativeAOT, idle, no station):
   private bytes 5.5 MB with the icon against 4.4 MB without, 10 threads against 5.
2. The service pushes a snapshot after each poll and each page or tray choice: charge, the last fresh reading's input,
   watts, mode (the flag's mode and the page choice), time left on battery, the confirmed cut. A reading older than
   `maxGapSec` is stale: grey icon, the tooltip names its time and shows no watts or time left.
3. The icon is drawn at run time from a 32-bit pixel buffer at the taskbar's size for the system DPI (the tray thread
   is per-monitor DPI aware): a dark tile, the rounded charge in a 3x5 pixel font, a bar filled to the charge, green
   on mains, amber on battery, grey unknown. It is rebuilt only when the digits, colour, staleness or size change; the
   tooltip only when its text changes.
4. The menu is Auto and the modes as one radio group, "Open panel" (the page through the shell, no window of ours),
   "Exit tray" (the icon and its notifications go until the next start; the service runs on). The modes come through
   a small port (list, current, select): under `v2` every mode of `modes.json` in its order, a choice held until Auto
   as `POST /api/modes/select` holds it; under `legacy` the page's Auto / Eco / Max through the path of
   `POST /api/mode`. A choice runs off the tray thread.
5. Explorer's restart (`TaskbarCreated`) adds the icon again; so does a taskbar that was not there yet when the
   service started at logon.
6. Notifications are shell balloons (`NIF_INFO`), shown by Windows 10 and 11 as toasts with no app registration or
   AUMID, respecting quiet time. A WinRT toast would need a Start-menu shortcut with an AUMID for an unpackaged exe
   and the WinRT notification projection in the NativeAOT image. Without the icon there is nowhere to show them.
7. Sources: each stored event (a hook on the store, so every writer's events count) by kind: `eco-on`, `mains-back`,
   `mode-changed`, `mode-dropped` (mode changes); `emergency-on`, `stop`, `hibernate-warning` (low battery);
   `action-failed`, `mode-refused`, `broker-stale` (failures); `grid-voltage-high` (faults). Mains lost and back follow
   the confirmed cut between two snapshots, never the first one after a start. Station faults, warnings and
   over-temperature (V2 stations, §3) come as the whole active list after each poll (`TrayController.ReportAlarms`,
   fed from the station's own codes and its temperature against `stationTempHighC`): an alarm notifies when it
   appears, not while it stays.
8. Each group is switched in `notifications.json`, read at start and on each `PUT /api/settings/notifications`
   (all or nothing, written as a temp file moved over the old one); a change applies to the next notification.
