# HTTP API

The service listens on `http://127.0.0.1:<port>/` (port 18940 by default) and nowhere else. Answers are JSON (UTF-8)
unless noted. Times are ISO 8601 with the PC's local offset (`2026-01-31T18:05:00+02:00`); query times may be ISO 8601
or Unix milliseconds.

## Guard

- Every request: the `Host` header must be exactly `127.0.0.1:<port>` or `localhost:<port>`; anything else is `403
  forbidden: foreign Host`. This blocks DNS rebinding.
- `POST /api/mode`, `POST /api/return-plan`, `POST /api/reading-source`, `POST /api/cloud/sign-in`, `POST /api/cloud/sign-out` and `PUT /api/settings/notifications` also need the header `X-Station: 1` and an `Origin` that is absent or the service's own
  (`http://127.0.0.1:<port>` or `http://localhost:<port>`); else 403 with the reason. A web page on another origin
  cannot send the custom header without a CORS preflight, and the service never sends a CORS header.
- Wrong method: 405 (`POST only` for `/api/mode`, `GET or PUT only` for `/api/settings/notifications`, `GET only` elsewhere; `/api/return-plan` takes both). Unknown path: 404.

## GET /

The dashboard page (`text/html`). Also `/index.html`.

## GET /api/now

The current state, for the page. `?settle=1` answers once a saved page choice (`modePending`) is applied, refused or failed, at most 8 s later; at once when none waits. Every wait counts against the time left in the request's 10 s: a request that started late answers sooner, with the state as it is.

| Field | Meaning |
|---|---|
| `type`, `capacityWh`, `efficiency` | From the configuration. |
| `mode` | The station's output mode, readable (`inverter output`); `modeCode` the raw code (`INVERTER_OUTPUT`, `CLOUD_MAINS`, `BLE_MAINS`, ...). |
| `eco`, `ecoNote` | The power mode in words ("" when normal) and as a coded note. |
| `manual` | The page's choice: `auto`, `eco`, `max`. `modePending`: a choice not applied yet, or null. |
| `modeError`, `modeErrorNote` | The last refusal or failure of a page choice ("" when none), and as a coded note. |
| `powerMode` | The flag's mode in force as the service last read it (on a poll and right after an applied page choice): `normal`, `eco`, `emergency`, or `unreadable` (not read yet or untrusted). |
| `modeHold` | What holds the mode, as a coded note, or null under Auto: `max-pinned-cut`, `max-pinned-mains`, `eco-pinned-cut`, `eco-pinned-mains` (pinned until mains returns or goes, or Auto), `auto-next` (Auto pressed: the battery rules decide at the next reading, which is taken at once). |
| `powerPlan` | The active Windows power plan as the switch reads it (`powercfg /list`): `{ "id": "high" \| "balanced" \| "saver" \| "other" \| "", "name" }`; `""` = unknown. |
| `returnPlan` | The plan the end of eco sets (see `/api/return-plan`). |
| `level` | Battery level in the cut: `ok`, `low` (emergency), `critical` (stop), `warning` (the hibernate warning was given; the PC hibernates at `hibernateAt`). |
| `cut` | true while a power cut is confirmed. |
| `grid` | Mains: `on`, `off`, `unknown`. |
| `silent` | true while the station does not answer. |
| `ecoAt` | When eco begins if mains stays off; null when no eco is pending. |
| `hibernateDelayedUntil` | During a cut: the earliest time the rebase rule allows hibernate; else null. |
| `note`, `notes` | Notes for the page: one text joined with ` · `, and the list as `{code, text, args}`. |
| `dryRun` | true in a dry run. |
| `hoursLeft` | Hours of battery to empty at the current load; null on mains or without an estimate. |
| `hoursLeftLow`, `hoursLeftHigh` | With `runtimeSource` `model`: the P10-P90 range of `hoursLeft`; null otherwise or without enough history (5 cuts; high null = no upper bound). |
| `runtimeSource` | `model` (fitted to this station's discharges), `rate` (measured), `learned`, `config`; null without an estimate. |
| `ratePerHour` | Measured discharge, % per hour, whenever the readings yield one (with `runtimeSource` `rate` or `model`); else null. |
| `learnedEfficiency` | The learned efficiency, or null. |
| `thresholds` | `emergencyAt`, `stopAt`, `warnAt`, `hibernateAt`, as configured. |
| `effectiveThresholds` | The levels the last accepted reading was decided on: `emergencyAt`, `stopAt`, `warnAt`, `hibernateAt`, each the configured one raised by the measured drain (design.md section 6), and `drainPerHour` (% per hour, capped at 120; null = no measured drain, the configured levels). |
| `last` | The last accepted reading, or null: `at`, `soc`, `inAc`, `inDc`, `outAc`, `outDc`, `volt`, `hz`, `mains`; the grid input `gridVolt` (V) and `gridHz`, V1 registers 77 and 80 of the AC200PL over Bluetooth, else null. `station`: a V2 station's own values, else null: `packVolt`, `packAmp`, `minutesToFull`, `minutesToEmpty`, `emptyGapHours` (the station's time to empty minus `hoursLeft`, hours), `warnings` and `faults` (code lists, null when that block was not read), `tempAmbient`, `tempInverter`, `tempPvDcDc` (°C). |
| `wake` | The wake timer, or null: `everyMinutes`, `floorPercent`, `armed`, `wakeAt`, `disarmOwed`, `error`. |
| `dtekWatcher` | The DTEK module (below). |

`dtekWatcher` fields:

| Field | Meaning |
|---|---|
| `state` | `off` module off · `ok` the schedule in use covers now and is fresh · `stale` it covers now and is older than `staleAfterMin` · `none` no schedule covers now, or the house's queues are not known. |
| `note`, `notes` | Text for the page, and coded notes. |
| `source` | Where the schedule in use came from: `dataset` or `site`. |
| `update` | The distributor's own update time of the schedule in use, as written. |
| `collectedAt` | When that schedule was collected. |
| `datasetState`, `siteState` | Per source: `ok`, `failed`, `off`; the site also `blocked`. |
| `queues`, `queuesSource` | The house's queues and where they came from (`config`, `site`, ""). |
| `queue`, `queueSource` | The queue followed, or null, and how it was found (`config`, `matched`, `single`, ""). |
| `queueOrigin`, `queueContradictions` | `manual`, `detected`, `manual-refuted` or ""; cuts in the running row against the manual queue. |
| `queueSet` | Members of the followed queue (one for a single queue; none while no queue is followed). |
| `nextStart`, `nextEnd`, `nextQueue`, `nextQueues` | The first planned outage not over yet among the shown queues, and the members it came from. |
| `return` | During a cut: the expected return (the planned window's end, else the site's); else null. |
| `outageKind`, `outageStart`, `outageEnd`, `outageEndAt` | The site's current outage (texts as written; `outageEndAt` as a time or null). |
| `batteryLasts` | During a cut: `yes`, `no`, `unknown`; else null. |
| `overdue`, `overdueMin` | The cut outlasted its planned window; whole minutes since its end, or null. |
| `plannedReturn` | During a cut: the end of the planned window followed; else null. |
| `batteryUntil` | During a cut: when the battery reaches `hibernateAt` at the current load; else null. |
| `planChanges` | Plan edits seen during this cut (0 outside a cut). |
| `lateBackMin`, `lateCuts` | Median minutes the power came back after the plan (null under 3 recorded cuts), and the cuts recorded. |
| `today`, `tomorrow` | Dates `DD.MM.YYYY` (Kyiv). |
| `windows` | Per shown queue: `queue`, `today`, `tomorrow` (text such as `17:00-21:00, possible 21:00-22:00`, `no cuts`, or ""), `intervals` (`start`, `end`, `queues`). |

## GET /api/history?from&to&step

Stored readings. `to` defaults to now, `from` to 26 hours before `to`; `step` seconds, default 60. A step of 60 or
less returns each stored row (raw minutes, and 5-minute aggregates for older times); a larger step groups them.

```json
{ "step": 60, "rows": [ { "at": "...", "soc": 87, "inAc": 0, "inDc": 0, "outAc": 142, "outDc": 0, "volt": 52.1, "hz": 50.0, "mains": false } ] }
```

400 when a time cannot be read, `step` < 1 or `from` >= `to`.

## GET /api/events?from&to

Stored events, same time defaults: `{ "events": [ { "at", "kind", "detail" } ] }`. Kinds include `eco-on`,
`emergency-on`, `mains-back`, `stop`, `hibernate-warning`, `hibernate`, `hibernate-refused`, `hibernate-skipped`,
`hibernate-delayed`, `mode-dropped`, `mode-refused`, `grid-voltage-high`, `station-warning`, `station-fault`, `station-alarm-cleared`, `station-hot`, action failures, `wake-*` and `dtek-watcher-*`.

## GET /api/health

| Field | Meaning |
|---|---|
| `ok` | true when `problem` is null; false while a problem is set (for example when `mac` is missing). |
| `problem` | What keeps the service from working (for example the missing `mac`), or null. |
| `version` | The service version. |
| `uptimeSec` | Seconds since start. |
| `dryRun` | true in a dry run. |
| `lastPollAt`, `lastAcceptedAt` | Times of the last poll and the last accepted reading, or null. |
| `lastSource` | `bluetooth`, `cloud` or `cloud-history` (the cloud history's newest point, standing in for a device list with no live state): the source of the last accepted reading, or null. |
| `bluetoothEncrypted` | true once the station opened the encrypted Bluetooth handshake: the built-in reader reads unencrypted stations only, so it is not connected to again until the service restarts and the cloud fallback answers. Information, not a problem: `ok` does not change. false otherwise. |
| `readingSource` | The source in force: `auto`, `bluetooth` or `cloud` (see `/api/cloud`). |
| `cloud` | The cloud's state `{ "state", "detail", "at" }`: `off` (the source never calls it) · `signed-out` (no token) · `ok` · `sign-in-needed` (the server refused the token refresh: sign in again) · `error` (the last read failed; `detail` names why). `at` = when it was seen, or null. Never a token. |
| `bluetoothStalled` | true while a Bluetooth read abandoned at `readerTimeoutSec` plus 2 s (a Bluetooth call that ignored its timeout) is still running: no new Bluetooth read starts and the cloud fallback answers, until it ends or, after max(4 x that bound, 2 minutes), is given up. Information, not a problem: `ok` does not change. false otherwise. |
| `stationBlocks` | The station's optional register blocks (`battery`, `stationTime`, `alarms`, `temperatures`) and whether it serves them: `supported`, `not supported: <why>` (until the service restarts), `not asked yet`, or `not on this protocol (V1)`. Information only. |
| `broker` | The power modes' admin broker ([design.md](design.md) §13): `{ "state": "missing" \| "stale" \| "current", "version", "problem" }`. `stale` = its copy of the exe is older than the service: run `install.ps1 -Broker -Apply` in an elevated shell (`problem` says so; at start it is also event `broker-stale` and a tray notice). Information only: `ok` does not change. |
| `misses` | Misses in a row. |
| `silent` | true while the station does not answer. |
| `rawRows`, `aggregateRows` | Rows in the database. |

## GET /api/return-plan, POST /api/return-plan

The power plan every end of eco sets: `returnPlan` in `power-mode.config.json` beside the flag ([power-mode/README.md](../power-mode/README.md), Configuration).
GET answers `{ "returnPlan": "high" | "balanced" | "previous" | "custom" }` (`custom` = a plan GUID set by hand; no file = `high`); `/api/now` carries the same field.
POST body `{"returnPlan": "high" | "balanced" | "previous"}`, headers per the guard above; every other key of the file is kept, the write is a temp file moved over it.
Answer `{ "ok", "returnPlan", "message" }`: 200 saved · 400 a body that is not JSON or another value · 409 the flag folder does not exist yet or the file is not a JSON object (nothing written) · 500 the write failed (the old file stays).

## GET /api/cloud, POST /api/reading-source, POST /api/cloud/sign-in, POST /api/cloud/sign-out

The reading source and the BLUETTI cloud account ([design.md §4](design.md#reading-source)). Every answer of the four is
the same object:

```json
{ "readingSource": "auto", "readingSourceFrom": "default", "cloud": { "state": "ok", "detail": "...", "at": null },
  "signIn": { "state": "idle", "message": "", "at": null } }
```

| Field | Meaning |
|---|---|
| `readingSource` | `auto`, `bluetooth` or `cloud`. |
| `readingSourceFrom` | `page` (saved by the page, `reading-source.json`), `config` (`readingSource` or `cloudEnabled: false` in config.json) or `default`. |
| `cloud` | As in `/api/health`. |
| `signIn` | The page's sign-in: `idle`, `waiting` (for the browser, at most 5 minutes), `signed-in`, `failed`; `message` the last line in English (never a token). |
| `authorizeUrl` | Only in the answer of `POST /api/cloud/sign-in`: the BLUETTI sign-in address for the page to open. |

- `POST /api/reading-source` body `{"readingSource": "auto" | "bluetooth" | "cloud" | "config"}`: saved and in force from the next poll, no restart; `config` deletes the page's choice so the config applies again. 200 · 400 another body · 409 with that source nothing would be polled (e.g. `bluetooth` for a model Bluetooth does not read, or without a `mac`): the reason, nothing changed · 500 not written or not deleted (nothing changed).
- `POST /api/cloud/sign-in`: starts the `--cloud-login` flow inside the service, its catcher on `127.0.0.1` at a port the OS assigns. 202 with `authorizeUrl`; a second call while one waits answers the same address · 502 it could not start.
- `POST /api/cloud/sign-out`: stops a waiting sign-in and deletes the token file. 200 · 503 a cloud read holds the reader past 5 s (try again) · 500 the file could not be deleted.

## POST /api/mode

Body `{"mode": "auto" | "eco" | "max"}`, headers per the guard above.

```powershell
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:18940/api/mode -Headers @{ 'X-Station' = '1' } -ContentType 'application/json' -Body '{"mode":"eco"}'
```

The page's Eco fires the `eco` action and Max (or Auto lifting the page's Eco) fires `normal`, when `actions` define
them: an executable with arguments, no shell, logged only in a dry run ([configuration.md](configuration.md), "Power
mode and actions"; the power-mode commands have their own limit, `powerModeTimeoutSec`).

Answer: `{ "ok", "accepted", "manual", "modePending", "message", "code" }`. 202 = saved; the service loop applies it;
`modePending` is the choice left for it to run (null when the press runs no command, as Auto after Max). 409 = Max or Eco refused
during a battery emergency (`code` names it: `max-held`, `eco-held`). 400 = a body that is not JSON or a mode that is not one of the three.

## Power modes (`modes.engine` = `v2`)

User-defined modes, the Auto rules and per-action status ([design.md](design.md) §12). `v2` is the default; with the
engine at `legacy` every route below answers 409; under `v2`, `POST /api/mode` selects the built-in `eco` / `max` or
`auto` and answers like `GET /api/modes`. Writes (POST, PUT, DELETE) take the guard above (`X-Station: 1`, own or no
`Origin`); bodies on these routes are at most 64 KB (413 above; every other route keeps 4 KB). A mode id `auto` is
refused (400): it is the selection value of the rules. A change is saved to `modes.json`, reconciled at once, and answered with the whole view (200); a refusal
is text: 400 a bad body or setting (each problem names its field, `actions[0].settings.plan: required`), 404 no such
mode, 409 a conflict (a built-in deleted, a mode a rule still chooses, an id taken, `modes.json` not writable).

| Route | Body / query | Does |
|---|---|---|
| `GET /api/modes` | | `{ engine, activeModeId, reason, emergency, actions[], restores[], problems[], settings }` |
| `POST /api/modes` | a mode | Adds it. |
| `PUT /api/modes/{id}` | a mode, same `id` | Replaces it; a built-in stays built in. |
| `DELETE /api/modes/{id}` | | Removes it; built-ins (`normal`, `eco`, `max`) are not deletable. |
| `PUT /api/modes/rules` | an array of rules | Replaces the Auto rules (order = priority). |
| `POST /api/modes/select` | `{ "mode": "auto" \| <id> }` | A mode is held until `auto`. |
| `GET /api/modes/catalogue` | | `{ modules: [{ descriptor, available, unavailableReason, discovery }] }`: every action type, its settings JSON Schema and UI hints, and the live lists it offers (plans, services, ...); 3 s per module. |
| `GET /api/modes/journal` | `?since=<seq>` | `{ pageSize, records[] }`: the journal after that sequence number, 500 at most. |
| `PUT /api/settings/hibernate` | `{ "autoHibernate": true \| false }` | Hibernation at the hibernate level on or off (the `no-hibernate` file is still honoured). |

A mode: `{ "id": "quiet", "name": "Quiet", "flagWord": "normal" | "eco" | "emergency", "actions": [{ "typeId":
"windows.powerPlan", "enabled": true, "enforce": false, "settings": { "plan": "<GUID>" } }] }`. `id`: 1-32 of `a-z 0-9 -`.
An action of a type this build does not know is kept and shows `unsupported`.

A rule: `{ "condition": { "kind": "mainsOffFor" | "mainsOnFor" | "socBelow" | "cutWithin" | "timeWindow", "seconds",
"percent", "minutes", "from": "HH:mm", "to": "HH:mm", "hysteresis" }, "modeId": "eco" }`; only the fields of its kind are read.

`actions[]`: `{ index, typeId, status, detail }` per enabled action of the active mode; `status` is `satisfied` (already
so, nothing to undo), `applied`, `failed`, `unsupported`, `relinquished` (changed by hand: left alone), `unknown`.
`restores[]`: resources an earlier mode left that are not back yet: `{ key, typeId, status, detail }`
(`restore-failed` with its try count, `relinquished`, `unknown`).

A module's settings form is generated from its `settingsSchema` and `uiHints` alone (the page's Power modes panel names
no module). `uiHints` maps a field's dotted path from the settings root (`limit`, `inner.depth`) to `{ label, help,
units, order, widget, discovery, step, labels }`: `discovery` (or `list`) names one of the module's `discovery` lists
(a select of its labels that stores their ids; for an array, a checkbox list); `widget: "slider"` makes a number with
both `minimum` and `maximum` a slider; `order` sorts the fields (lower first, the rest in schema order); `labels` names
`enum` values. Without hints: `enum` = a select, `boolean` = a checkbox, `number` / `integer` = a number field with
the schema's bounds, any other array = one item per line, `object` = a group of its own fields, several types = a text
field; `title` and `description` are the label and the help, `default` is filled in. A stored value a list no longer
offers stays chosen and marked "not found here".

Journal record: `{ seq, at, kind: "mode" | "prepared" | "applied" | "relinquished" | "restored" | "restore-failed" | "dropped", modeId, typeId, key, before, value, final, detail }`.

## GET /api/settings/notifications, PUT /api/settings/notifications

Which Windows notifications show (`notifications.json`, [configuration.md](configuration.md), Notifications).
GET answers `{ "mains", "modeChanges", "lowBattery", "failures", "faults", "overTemperature" }`, each true or false.
PUT body: any of these keys with true or false, headers per the guard above; the rest keep their value. All or nothing:
an unknown key or a value that is not a boolean is 400 and changes nothing. 200 answers the whole set as saved (a temp
file moved over the old one); the next notification follows it, no restart.

```powershell
Invoke-RestMethod -Method Put -Uri http://127.0.0.1:18940/api/settings/notifications -Headers @{ 'X-Station' = '1' } -ContentType 'application/json' -Body '{"modeChanges":false}'
```
