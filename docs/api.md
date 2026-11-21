# HTTP API

The service listens on `http://127.0.0.1:<port>/` (`port`, default 18940) and on no other address. Answers are JSON
(UTF-8) unless noted; an error answer is `text/plain` with the reason. Times are ISO 8601 with the PC's local offset
(`2026-01-31T18:05:00+02:00`); a time in a query may be ISO 8601 or Unix milliseconds.

## Routes

| Method | Path | Guard | Section |
|---|---|---|---|
| GET | `/`, `/index.html` | Host | [Page](#get-) |
| GET | `/api/now` | Host | [Now](#get-apinow) |
| GET | `/api/history` | Host | [History](#get-apihistory) |
| GET | `/api/events` | Host | [Events](#get-apievents) |
| GET | `/api/health` | Host | [Health](#get-apihealth) |
| POST | `/api/mode` | write | [Page mode](#post-apimode) |
| GET, POST | `/api/return-plan` | Host; POST write | [Return plan](#get-apireturn-plan-post-apireturn-plan) |
| GET | `/api/cloud` | Host | [Cloud](#cloud-and-reading-source) |
| POST | `/api/reading-source`, `/api/cloud/sign-in`, `/api/cloud/sign-out` | write | [Cloud](#cloud-and-reading-source) |
| GET, PUT | `/api/settings/notifications` | Host; PUT write | [Notifications](#get-apisettingsnotifications-put-apisettingsnotifications) |
| POST | `/api/settings/notifications/test` | write | [Test notification](#post-apisettingsnotificationstest) |
| GET, POST, PUT, DELETE | `/api/modes`, `/api/modes/...`, `/api/settings/hibernate` | Host; writes write | [Power modes](#power-modes-modesengine--v2) |

## Request rules

Host guard, on every request: the `Host` header must be exactly `127.0.0.1:<port>` or `localhost:<port>`, else
`403 forbidden: foreign Host`. This blocks DNS rebinding.

Write guard, on every route marked "write" above, in addition: the header `X-Station: 1`, and an `Origin` that is
absent or the service's own (`http://127.0.0.1:<port>` or `http://localhost:<port>`). Else 403 with the reason
(`forbidden: no X-Station: 1 header`, `forbidden: foreign Origin`). A page on another origin cannot send the custom
header without a CORS preflight, and the service never sends CORS headers.

| Limit | Answer |
|---|---|
| One request per connection (`Connection: close`), HTTP/1.x only | 400 `bad request line` |
| Request head (line and headers) up to 16 KB | 431 |
| Head received within 5 s | connection closed |
| Body up to 4 KB; 64 KB on `/api/modes*` and `/api/settings/hibernate` | 413 |
| A repeated `Host`, `Origin`, `X-Station` or `Content-Length` header | 400 |
| 10 s per connection, from accept to the last byte written; at most 32 connections at a time | connection dropped / queued |
| Wrong method | 405 (`GET only`, `POST only`, `GET or PUT only`) |
| Unknown path | 404 `not found` |
| A handler failure | 400 (bad input) or 500, logged |

Every answer carries `Cache-Control: no-store` (the page: `no-cache` with an ETag), `X-Content-Type-Options: nosniff`,
`X-Frame-Options: DENY`, `Referrer-Policy: no-referrer` and a `Content-Security-Policy` that allows only the service's
own origin. A JSON or HTML 200 of 1400 bytes or more is compressed by `Accept-Encoding` (Brotli preferred, then gzip).

Example of a write:

```powershell
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:18940/api/mode -Headers @{ 'X-Station' = '1' } -ContentType 'application/json' -Body '{"mode":"eco"}'
```

## GET /

The dashboard page (`text/html`); also `/index.html`. A request with a matching `If-None-Match` gets 304.

## GET /api/now

The current state, for the page. `?settle=1` waits until a saved page choice (`modePending`) is applied, refused or
failed, at most 7 s, and answers at once when none waits. Every wait counts against the request's 10 s.

| Field | Meaning |
|---|---|
| `type`, `capacityWh`, `efficiency` | From the configuration. |
| `mode`, `modeCode` | The station's output mode, readable (`inverter output`) and raw (`INVERTER_OUTPUT`, `CLOUD_MAINS`, `BLE_MAINS`, ...). |
| `eco`, `ecoNote` | The power mode in words (`""` when normal) and as a note. |
| `manual` | The page's choice: `auto`, `eco`, `max`. |
| `modePending` | A page choice not applied yet, or null. |
| `modeError`, `modeErrorNote` | The last refusal or failure of a page choice (`""` / null when none), as text and as a note. |
| `modeHold` | What holds the mode, as a note, or null: `max-pinned-cut`, `max-pinned-mains`, `eco-pinned-cut`, `eco-pinned-mains`, `auto-next`. |
| `returnPlan` | The plan the end of eco sets, as in [`/api/return-plan`](#get-apireturn-plan-post-apireturn-plan). |
| `powerMode` | The flag's mode as last read: `normal`, `eco`, `emergency`, or `unreadable` (not read yet or untrusted). |
| `powerPlan` | The active Windows power plan: `{ "id": "high" \| "balanced" \| "saver" \| "other" \| "", "name" }`; `""` = unknown. |
| `level` | Battery level in the cut: `ok`, `low` (emergency), `critical` (stop), `warning` (hibernate warning given). |
| `cut` | true while a power cut is confirmed. |
| `hibernateDelayedUntil` | During a cut, the earliest time the rebase rule allows hibernate; else null. |
| `ecoAt` | When eco begins if mains stays off; null when none is pending. |
| `grid` | Mains: `on`, `off`, `unknown`. |
| `silent` | true while the station does not answer. |
| `note`, `notes` | Notes for the page: one text joined with ` · `, and the list of notes. |
| `dryRun` | true in a dry run. |
| `hoursLeft` | Hours of battery to empty at the current load; null on mains or without an estimate. |
| `runtimeSource` | Where `hoursLeft` comes from: `model`, `rate`, `learned`, `config`; null without an estimate. |
| `ratePerHour` | Measured discharge, % per hour, or null. |
| `hoursLeftLow`, `hoursLeftHigh` | The P10-P90 range of `hoursLeft` under `model`; null otherwise or without enough history (`hoursLeftHigh` null = no upper bound). |
| `learnedEfficiency` | The learned efficiency, or null. |
| `thresholds` | `emergencyAt`, `stopAt`, `warnAt`, `hibernateAt` as configured. |
| `effectiveThresholds` | The same four as the last poll applied them (each at least the configured one, raised by the measured drain), and `drainPerHour` (% per hour; null = the configured levels). |
| `last` | The last accepted reading, or null (below). |
| `wake` | The wake timer, or null: `everyMinutes`, `floorPercent`, `armed`, `wakeAt`, `disarmOwed`, `error`. |
| `dtekWatcher` | The DTEK module (below). |

A note is `{ "code", "text", "args": [] }`: `code` is stable for translation, `text` is English.

`last`: `at`, `soc`, `inAc`, `inDc`, `outAc`, `outDc` (W), `volt`, `hz`, `mains` (AC input above 0), `gridVolt` and
`gridHz` (grid input; null where the station does not report it), and `station`: a V2 station's own values or null —
`packVolt`, `packAmp`, `minutesToFull`, `minutesToEmpty`, `emptyGapHours` (the station's time to empty minus
`hoursLeft`), `warnings` and `faults` (code lists; null when not read), `tempAmbient`, `tempInverter`, `tempPvDcDc`
(°C).

`dtekWatcher` (see [design.md](design.md#11-dtek-module-planned-outages-kyiv-region)):

| Field | Meaning |
|---|---|
| `state` | `off` · `ok` the schedule in use covers now and is fresh · `stale` it covers now and is older than `staleAfterMin` · `none` no schedule covers now, or the house's queues are unknown. |
| `note`, `notes` | Text for the page, and notes. |
| `source` | The schedule in use: `dataset` or `site`. |
| `update`, `collectedAt` | The distributor's own update time (as written), and when the schedule was collected. |
| `datasetState`, `siteState` | Per source: `ok`, `failed`, `off`; the site also `blocked`. |
| `queues`, `queuesSource` | The house's queues and where they came from (`config`, `site`, `""`). |
| `queue`, `queueSource` | The queue followed (null when none) and how it was found (`config`, `matched`, `single`, `""`). |
| `queueOrigin`, `queueContradictions` | `manual`, `detected`, `manual-refuted` or `""`; cuts in a row against the manual queue. |
| `queueSet` | Members of the followed queue. |
| `nextStart`, `nextEnd`, `nextQueue`, `nextQueues` | The first planned outage not over yet among the shown queues, and the queues it comes from. |
| `return` | During a cut, the expected return; else null. |
| `outageKind`, `outageStart`, `outageEnd`, `outageEndAt` | The site's current outage (texts as written; `outageEndAt` a time or null). |
| `batteryLasts` | During a cut: `yes`, `no`, `unknown`; else null. |
| `overdue`, `overdueMin` | The cut outlasted its planned window; whole minutes since its end, or null. |
| `plannedReturn` | During a cut, the end of the planned window followed; else null. |
| `batteryUntil` | During a cut, when the battery reaches `hibernateAt` at the current load; else null. |
| `planChanges` | Plan edits seen during this cut (0 outside a cut). |
| `lateBackMin`, `lateCuts` | Median minutes the power came back after the plan (null with too few cuts), and the cuts recorded. |
| `today`, `tomorrow` | Dates `DD.MM.YYYY` (Kyiv). |
| `windows` | Per shown queue: `queue`, `today`, `tomorrow` (text such as `17:00-21:00`, `no cuts`, or `""`), `intervals` (`start`, `end`, `queues`). |

## GET /api/history

Query `from`, `to`, `step`. `to` defaults to now, `from` to 26 hours before `to`, `step` (seconds) to 60. A step of 60
or less returns each stored row (raw minutes, and 5-minute aggregates for older times); a larger step groups them.

```json
{ "step": 60, "rows": [ { "at": "2026-01-31T18:05:00+02:00", "soc": 87, "inAc": 0, "inDc": 0, "outAc": 142, "outDc": 0, "volt": 52.1, "hz": 50.0, "mains": false } ] }
```

400 when a time or `step` cannot be read, `step` < 1, or `from` >= `to`.

## GET /api/events

Query `from`, `to`, with the defaults of `/api/history`. Answer `{ "events": [ { "at", "kind", "detail" } ] }`; 400
when a time cannot be read.

Kinds the service writes: `mode`, `mode-applied`, `mode-refused`, `mode-dropped`, `mode-superseded`, `eco-on`,
`emergency-on`, `mains-back`, `stop`, `hibernate-warning`, `hibernate`, `hibernate-refused`, `hibernate-skipped`,
`hibernate-delayed`, `action`, `action-failed`, `config-warning`, `grid-voltage-high`, `station-warning`,
`station-fault`, `station-alarm-cleared`, `station-hot`, `broker-stale`, `wake-*` and `dtek-watcher-*`. Under
`modes.engine` = `v2` also `mode-changed` (the service switched the mode on its own), `mode-chosen` (a switch the user
made: a select, the tray, an edit; no notification), `mode-drift` and `modes-problem`.

## GET /api/health

| Field | Meaning |
|---|---|
| `ok` | true when `problem` is null. |
| `problem` | What keeps the service from working (a missing or bad `mac`, a model nothing can read, an invalid `powerMode.extraReaders` name), or null. |
| `version` | The service version. |
| `uptimeSec` | Seconds since start. |
| `dryRun` | true in a dry run. |
| `lastPollAt`, `lastAcceptedAt` | The last poll and the last accepted reading, or null. |
| `lastSource` | Source of the last accepted reading: `bluetooth`, `cloud`, `cloud-history` (the cloud history's newest point standing in for a missing live state), or null. |
| `bluetoothEncrypted` | true once the station opened an encrypted Bluetooth handshake: Bluetooth is not tried again until a restart and the cloud fallback answers. Does not change `ok`. |
| `bluetoothStalled` | true while an abandoned Bluetooth read is still running: no new Bluetooth read starts and the cloud fallback answers. Does not change `ok`. |
| `readingSource` | The source in force: `auto`, `bluetooth`, `cloud`. |
| `cloud` | `{ "state", "detail", "at", "until" }`: `state` is `off`, `signed-out` (no token), `ok`, `sign-in-needed` (the token refresh was refused) or `error` (the last read failed; `detail` says why). `until` = when the access token runs out, or null without a readable token. Never a token. |
| `misses` | Misses in a row. |
| `silent` | true while the station does not answer. |
| `rawRows`, `aggregateRows` | Rows in the database. |
| `stationBlocks` | Per optional register block: `supported`, `not supported: <why>`, `not asked yet`, or `not on this protocol (V1)`. |
| `broker` | `v2` engine only: `{ "state": "missing" \| "stale" \| "current", "version", "problem" }`. `stale` = the admin broker's copy of the exe is older than the service: run `install.ps1 -Broker -Apply` elevated. Does not change `ok`. |

## POST /api/mode

The page's Auto / Eco / Max. Body `{"mode": "auto" | "eco" | "max"}`, write guard; 400 for another body.

- `modes.engine` = `v2`: selects the built-in `eco` or `max` mode (held until `auto`), or `auto`; the answer is that of
  [`POST /api/modes/select`](#power-modes-modesengine--v2).
- `modes.engine` = `legacy`: answers `{ "ok", "accepted", "manual", "modePending", "message", "code" }`. 202 = saved;
  the service loop applies it (`modePending` null when nothing is left to run). 409 = Max or Eco refused during a
  battery emergency (`code` `max-held` or `eco-held`). The page's Eco runs `actions.eco`, Max or an Auto that lifts
  the page's Eco runs `actions.normal` ([configuration.md](configuration.md#power-mode-and-actions)).

## GET /api/return-plan, POST /api/return-plan

The power plan every end of eco sets: `returnPlan` in `power-mode.config.json` beside the flag file
([power-mode/README.md](../power-mode/README.md#configuration)).

- GET: `{ "returnPlan": "high" | "balanced" | "previous" | "custom" }`. `custom` = a plan GUID set by hand; no file =
  `high`.
- POST: body `{"returnPlan": "high" | "balanced" | "previous"}`, write guard. Every other key of the file is kept; the
  file is replaced through a temp file. Answer `{ "ok", "returnPlan", "message" }`.

| Status | Meaning |
|---|---|
| 200 | Saved. |
| 400 | Not JSON, or another value. |
| 409 | The flag folder does not exist yet, or the file is not a JSON object; nothing written. |
| 500 | The file could not be read or written; the old file stays. |

## Cloud and reading source

`GET /api/cloud`, `POST /api/reading-source`, `POST /api/cloud/sign-in`, `POST /api/cloud/sign-out`
([design.md](design.md#4-cloud-fallback)). Every success answers the same object:

```json
{ "readingSource": "auto", "readingSourceFrom": "default",
  "cloud": { "state": "ok", "detail": "", "at": null, "until": "2026-12-21T19:06:00+00:00" },
  "signIn": { "state": "idle", "message": "", "at": null } }
```

| Field | Meaning |
|---|---|
| `readingSource` | `auto`, `bluetooth`, `cloud`. |
| `readingSourceFrom` | `page` (`reading-source.json`), `config` (`readingSource`, or `cloudEnabled: false`), `default`. |
| `cloud` | As in `/api/health`. |
| `signIn` | The page's sign-in: `state` `idle`, `waiting` (for the browser), `signed-in`, `failed`; `message` the last line (never a token); `at`. |
| `authorizeUrl` | Only in the `POST /api/cloud/sign-in` answer: the BLUETTI sign-in address for the page to open. |

| Route | Body | Status |
|---|---|---|
| `POST /api/reading-source` | `{"readingSource": "auto" \| "bluetooth" \| "cloud" \| "config"}`; `config` deletes the page's choice | 200 saved, in force from the next poll · 400 another body · 409 with that source nothing would be polled (nothing changed) · 500 not written or not deleted |
| `POST /api/cloud/sign-in` | none | 202 with `authorizeUrl` (a second call while one waits answers the same address) · 502 the sign-in could not start |
| `POST /api/cloud/sign-out` | none | 200 a waiting sign-in stopped and the token file deleted · 503 a cloud read holds the reader (try again) · 500 the file could not be deleted |

## GET /api/settings/notifications, PUT /api/settings/notifications

Which Windows notifications show ([configuration.md](configuration.md#notifications-notificationsjson)).

- GET: `{ "enabled", "mains", "modeChanges", "lowBattery", "failures", "faults", "overTemperature" }`, each true or
  false. `enabled` false silences every group and keeps each group's own value for when it is true again.
- PUT: any subset of those keys with true or false, write guard; the rest keep their value. All or nothing: an unknown
  key or a non-boolean value is 400 and changes nothing. 200 answers the whole set as saved; the next notification
  follows it.

```powershell
Invoke-RestMethod -Method Put -Uri http://127.0.0.1:18940/api/settings/notifications -Headers @{ 'X-Station' = '1' } -ContentType 'application/json' -Body '{"modeChanges":false}'
```

## POST /api/settings/notifications/test

Shows one "Test notification" whatever the switches say; write guard, no body. 200 `{ "sent": true }` once handed to
the tray icon · 503 the service shows no tray icon (`tray` off, or not in the signed-in session).

## Power modes (`modes.engine` = `v2`)

User-defined modes, the Auto rules and per-action status ([design.md](design.md), Power modes). With
`modes.engine` = `legacy` every route here answers 409.

| Route | Body / query | Does |
|---|---|---|
| `GET /api/modes` | | The view (below). |
| `POST /api/modes` | a mode | Adds it. |
| `PUT /api/modes/{id}` | a mode with the same `id` | Replaces it; a built-in stays built in. |
| `DELETE /api/modes/{id}` | | Removes it. |
| `PUT /api/modes/rules` | an array of rules | Replaces the Auto rules; order = priority, the first match wins. |
| `POST /api/modes/select` | `{ "mode": "auto" \| "<id>" }` | Holds that mode until `auto`. |
| `PUT /api/settings/hibernate` | `{ "autoHibernate": true \| false }` | Hibernation at the hibernate level on or off; a `no-hibernate` file found at start sets it to `false` once (design.md, section 6). |
| `GET /api/modes/catalogue` | | Every action type: `{ "modules": [ { "descriptor", "available", "unavailableReason", "discovery" } ] }`. Built at start; each ask is answered from the last build at once and one rebuild runs behind it (each module gets 3 s), so the next ask sees the machine as it is then. |
| `GET /api/modes/journal` | `?since=<seq>` | `{ "pageSize": 500, "records": [] }`: journal records after that sequence number, at most 500. |

Writes take the write guard. A change is saved to `modes.json`, reconciled at once and answered with the whole view.

| Status | Meaning |
|---|---|
| 200 | Done; the view. |
| 202 | The transition takes longer than 5 s: the view as it stands (`pending` true, `switching` = the mode being entered). It runs on to its end; poll `GET /api/modes` until `pending` is false. |
| 400 | A bad body or setting; text naming each field (`actions[0].settings.plan: required`). Also a body id that differs from the path, or `since` that is not a number. |
| 404 | No such mode (an unknown, reserved or invalid id in the path, or in `select`). |
| 405 | Not GET on `catalogue` or `journal`. |
| 409 | An id taken, more than 32 modes, a built-in deleted, a mode a rule still chooses or the fallback mode deleted, `modes.json` not writable by this build, or the `legacy` engine. |
| 500 | `modes.json` not saved. |

View (`GET /api/modes` and every write):

| Field | Meaning |
|---|---|
| `engine` | `v2`. |
| `activeModeId` | The mode in force. |
| `reason` | Why it is in force, in words. |
| `switching` | The mode being entered, or null. |
| `pending` | true while a change is still running. |
| `emergency` | true during a battery emergency. |
| `actions` | Per enabled action of the active mode: `{ "index", "typeId", "status", "detail" }`. |
| `restores` | Resources an earlier mode left that are not back yet: `{ "key", "typeId", "status", "detail" }`. |
| `problems` | Texts; among them each entry of the active mode the admin broker's policy does not list. |
| `warnings` | Only after `POST /api/modes` or `PUT /api/modes/{id}`: `{ "action", "kind", "name", "command" }` per entry of that mode the broker's policy does not list (`kind` `service`, `task`, `profile`; `command` = the elevated `--broker-allow` to run). |
| `settings` | The stored settings: `version`, `modes`, `rules`, `fallbackModeId` (`normal`), `selection` (`kind` `auto` \| `manual`, `manualModeId`, `autoModeId`, `autoRule`), `autoHibernate`. |

`status` values: `unknown` (not reconciled yet, or the last read failed), `satisfied` (already so: nothing written,
nothing to undo), `applied`, `failed` (retried on the next poll), `unsupported` (the module is not available here or
not known to this build), `relinquished` (changed by hand: left alone), `restore-failed` (retries spent).

A mode:

```json
{ "id": "quiet", "name": "Quiet", "flagWord": "normal",
  "actions": [ { "typeId": "windows.powerPlan", "enabled": true, "enforce": false, "settings": { "plan": "<GUID>" } } ] }
```

- `id`: 1-32 characters of `a-z 0-9 -`. Reserved: `auto`, `rules`, `select`, `catalogue`, `journal` (400 in a body).
  Built-ins: `normal`, `eco`, `max` (editable, not deletable; `builtIn` is set by the service).
- `flagWord`: what the power-mode flag says while the mode is active: `normal`, `eco`, `emergency`.
- An action: `typeId` (from the catalogue), `enabled` (default true), `enforce` (re-apply a value changed by hand
  instead of letting go; default false), `settings` (checked against the module's schema). At most 32 actions. An
  action of a type this build does not know is kept and shows `unsupported`.

A rule (at most 32):

```json
{ "condition": { "kind": "socBelow", "percent": 40, "hysteresis": 3 }, "modeId": "eco" }
```

| `kind` | Fields read | Holds when |
|---|---|---|
| `mainsOffFor` | `seconds` (0-86400) | Mains confirmed off at least that long. |
| `mainsOnFor` | `seconds` (0-86400) | Mains confirmed on at least that long. |
| `socBelow` | `percent` (0-100), `hysteresis` (points) | The battery is under `percent`. |
| `cutWithin` | `minutes` (0-1440), `hysteresis` (minutes) | A planned (DTEK) outage starts within `minutes`. |
| `timeWindow` | `from`, `to` (`HH:mm`, local; `to` exclusive; may wrap midnight) | The local time is in the window. |

`hysteresis` keeps a rule in force matching that far past its threshold.

Catalogue `descriptor`: `typeId`, `version`, `title`, `settingsSchema` (JSON Schema subset: `type`, `properties`,
`required`, `enum`, `minimum`, `maximum`, `items`, `additionalProperties`), `uiHints`, `discoveryLists`,
`restoration` (`reversible`, `token`, `compensatable`, `irreversible`), `needsAdmin`, `defaultSettings`. `discovery`
maps each list name to `[ { "id", "label", "numbers" } ]` (plans, services, ...); `numbers` is an optional map of
named values a page words itself (a GPU's `min`, `max` and `default` watts). The page renders a module's settings form from
`settingsSchema` and `uiHints` alone: `uiHints` maps a field's dotted path (`limit`, `inner.depth`) to
`{ label, help, units, order, widget, discovery, step, labels }`; `discovery` (or `list`) names a discovery list,
`widget: "slider"` makes a bounded number a slider.

Journal record: `{ "seq", "at", "kind", "modeId", "typeId", "key", "before", "value", "final", "detail" }`; `kind` is
`mode`, `prepared`, `applied`, `relinquished`, `restored`, `restore-failed` or `dropped`; null fields are left out.
