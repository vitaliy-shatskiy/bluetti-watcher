# BLUETTI stations - read-only telemetry map

What the official BLUETTI Android app reads from a station over Bluetooth LE, so the watcher can add features from data
the station already reports. The watcher stays read only (see [design.md](design.md), section 1): control registers
(2000 and up) exist, the watcher never writes.

Source: the app `net.poweroak.bluetticloud`, `base.apk` sha256
`2d4df0c9146cb93cd8f0cd4eb491300f113260fc685cb9b4bea472e3258a6a59`, decompiled with JADX. The app-derived sections (1 and
up) are static analysis only: no device, no network, nothing executed. The "Verified on a live AC200PL" section comes
from one read-only Modbus read and a Bluetooth HCI capture of the official app. Each app-derived fact names its
evidence:

- `[code: Class.method]` read in the decompiled method.
- `[inferred]` derived from code, not read verbatim. The reasoning is given.

Short class names: `ProtocolParserV2` and `ConnConstantsV2` live in `net.poweroak.bluetticloud.ui.connectv2.tools`,
`ProtocolParse`, `DeviceConnUtil` and `ProtocolVer` in `net.poweroak.bluetticloud.ui.connect` (and its `tools`),
`BleConfig`, `BluettiBleManager` and `CRC16` in `net.poweroak.lib_ble`.

## Verified on a live AC200PL

Read on a live station. These facts override the app-derived claims below.

- A V2 read of 1300 (count 16) got Modbus exception 0x02 (illegal data address). This station does not serve the V2
  grid page.
- A Bluetooth HCI capture of the official app talking to this station: the app reads **V1 only**, in three pages,
  start 10 count 59, start 70 count 87 and start 3000 count 91. It also writes a settings range.

Values from the app's 70+87 answer, station in bypass. The app showed 224.0 V grid voltage a few minutes later.

| Reg | Value seen | Scale | Meaning | Confidence |
|---|---|---|---|---|
| 71 | 223 | V | AC output voltage | high |
| 74 | 500 | /10 Hz | AC output frequency (50.0 Hz) | high |
| **77** | 2246 | /10 V | **grid input voltage** (224.6 V) | high (matches the app) |
| **80** | 500 | /10 Hz | **grid input frequency** (50.0 Hz) | high |
| 73, 76, 79 | 263 each | W | powers, meaning per register not confirmed | low |
| 105-120 | 3447-3561 | mV | 16 cell voltages | high |
| 121-123 | 71-74 | not confirmed | likely temperatures | low |

Register 77 versus the app: the capture spans about a minute and register 77 read 224.3 to 224.6 V across it. The app's
224.0 V was read by eye a few minutes later, so a gap of under 1 V is expected. The /10 scale is what makes the value a
plausible mains voltage (scale 1 would give 2246 V).

## 1. Transport

### 1.1 GATT [code: BleConfig static fields]

| Item | Value |
|---|---|
| Service | `0000ff00-0000-1000-8000-00805f9b34fb` (`TARGET_SERVICE_UUID`) |
| Notify | `0000ff01-0000-1000-8000-00805f9b34fb` (`TARGET_CHARACTERISTIC_NOTIFY_UUID`) |
| Write | `0000ff02-0000-1000-8000-00805f9b34fb` (`TARGET_CHARACTERISTIC_WRITE_UUID`) |
| CCC descriptor | `00002902-0000-1000-8000-00805f9b34fb` |
| Advertised manufacturer data | ASCII `BLUETTI` (`AD_DATA_BLUETTI_HEX`) |

The watcher uses the same service and characteristics (design.md, section 2). Per product the app may take another
UUID triple from `DeviceConnUtil.getBLEUUIDs` [code: ConnectManager.bleConnect]. The alternatives were not read.

### 1.2 Read frame

```
unit(1) | 0x03 | start(2, BE) | count(2, BE) | CRC16-Modbus(2, low byte first)
answer: unit | 0x03 | byteCount = 2 x count | data | CRC
```

- Built as a hex string: unit, `CODE_READ` "03", start, count, then the CRC [code: ProtocolParse.buildReadTask]. The
  expected answer length is `regLen * 2` data bytes [code: same, `new BleTaskItem(regAddr, string, regLen * 2, ...)`].
  The 4-digit width of start and count comes from a Kotlin default [inferred].
- CRC: init `0xFFFF`, polynomial `0xA001` (reflected 0x8005), returned low byte first
  (`upperCase.substring(2, 4) + upperCase.substring(0, 2)`) [code: CRC16.CRC16_Modbus]. The watcher computes the same
  (`ModbusFrame.cs`, unit 0x01).
- Write functions 0x06 and 0x10 exist in the app [code: BleConfig.CODE_WRITE, ConnectManager.modbusDataHandle]. The
  watcher builds neither.

### 1.3 MTU, pacing and retries

- MTU: default 23, maximum 517, `MAX_MTU_BLE42` 247 [code: BleConfig]. `requestMtu` accepts 23 to 517
  [code: BluettiBleManager.requestMTU]. The value the app asks for was not found.
- A write longer than the negotiated payload is split into chunks [code: BluettiBleManager.writeCharacteristicSplit].
  The next chunk follows after 5 ms when the payload is 20 bytes or less, else at once
  (`supportedGattByte <= 20 ? 5L : 0L`) [code: BluettiBleManager.onCharacteristicWriteHandle]. A read request is 8
  bytes, so it is never split.
- The page lengths reach 116 registers, a 237-byte answer [code: ProtocolParserV2.getReadRegLen]. The app asks a
  page in one request [inferred: getReadTask takes the length from getReadRegLen, its body not read]. The watcher asks at most 32 registers a request (`ModbusRead.MaxQuantity`), so a page takes several requests. A
  larger read would need a larger MTU [inferred: one notification carries at most MTU - 3 bytes].
- No per-request timeout field exists in `BleTaskItem`. `BluettiBleManager` has a `retryCount` field and two
  disconnect timeouts. Their values and use were not read (section 5).

## 2. Telemetry pages (IoT protocol V2)

### 2.1 Encoding

- A page is read as `count` registers from `start`. Parsers index the answer as a byte list, so
  **register = start + byte / 2**. A pair `get(2k) + get(2k+1)` is one big-endian u16 [code: every parser]. "low byte"
  below = the odd byte alone.
- u32 = two registers, **low word first**: `bit32HexSwap` joins bytes 2, 3, 0, 1 [code: ProtocolParserV2.bit32HexSwap].
  This is why bluetti-bt-lib (and the watcher) can read the first register of a power pair as a u16.
- s16 / s32 = the same, signed (`(short)` cast, or `bit32RegByteToNumber(..., true, ...)`).
- Page lengths grow with the station's protocol version (`protocolVer`), the parser reads a field only when the page
  is long enough or the version is high enough [code: ProtocolParserV2.getReadRegLen, parseHomeData].

### 2.2 AC200PL / AC240 family

- AC200L and AC200PL take the AC240 configuration: `getDeviceFunc$default(this, "AC240", ...)`. AC240 is
  `DeviceCategory.PORTABLE_POWER` with `ProtocolVer.VER_1022` [code: DeviceConnUtil.getDeviceFunc]. Single phase for
  PV, grid and AC output [inferred: first argument 1 of `PVPhaseParams`, `GridPhaseParams`, `ACPhaseParams`].
- The watcher reads the AC200PL with the **V1** map of bluetti-bt-lib (registers 36 to 88, `V1Models.cs`).
- Live: the AC200PL does not serve the V2 pages, and the app itself reads it over V1 (section "Verified on a live
  AC200PL"). Its grid voltage is V1 register 77, not 1314. The V2 pages below stay valid for V2 models.
- `ProtocolVer` values: `VER_2000` 2000, `VER_2001` 2001, `VER_2005` 2005, `VER_2022` 2022 [code: ProtocolVer.<clinit>
  smali]. `VER_1022` is taken as 1022 [inferred: same naming]. Gates such as `protocolVer >= 2001` then depend on the
  version the station reports, not on this default.

Column "Watcher": **today** = read in a poll (the reading fields of `V2Models.cs`, the values an AC200PL poll reads at
its V1 registers), **map** = declared in `V2Models.All` for the models named but not read in a poll, **no** = not
declared.

### 2.3 Page 100 - home data [code: ProtocolParserV2.parseHomeData]

Length 56 registers (version 2000 and lower) up to 116 (version 2023 and up) [code: getReadRegLen].

| Reg | Field | Type, scale | Watcher |
|---|---|---|---|
| 100 | pack total voltage | u16 /10 V | no |
| 101 | pack total current | u16 /10 A | no |
| 102 | SOC | u16 %, capped at 100 | today |
| 103 | pack charging status | u16 code | no |
| 104 | time to full (`packChgFullTime`) | u16, unit not read | map (TIME_REMAINING: AC50B, AC70, EL10, EL30V2, EL100V2, Handsfree 1) |
| 105 | time to empty (`packDsgEmptyTime`) | u16, unit not read | no |
| 106 | pack aging status, progress, fault | 4-bit fields | no |
| 107 low | pack count (max 16) | u8 | no |
| 108 | packs online | bit list | no |
| 109 | CAN bus fault (version 2009+) | bit list | no |
| 110-115 | device model | ASCII, 12 bytes | map |
| 116-119 | serial number | 4 registers | map |
| 120 low / 121 | inverter count / inverters online | u8 / bits | no |
| 122 low | `invPowerType` (3 = high power) | u8 | no |
| 123 | energy flow lines | bits | no |
| 124 | control status (`getSysCtrlStatus`) | bits | no |
| 126-129 | **warnings** (alarm words) | 4 x u16 bits, section 3 | no |
| 133-138 | **faults** (version 2001+) | 6 x u16 bits, section 3 | no |
| 140-141 | total DC output power | u32 W | today (low word) |
| 142-143 | total AC output power | s32 W | today (low word, as u16) |
| 144-145 | total PV (DC input) power | u32 W | today (low word) |
| 146-147 | total grid (AC input) power | s32 W | today (low word, as u16) |
| 148-149 | total inverter power | s32 W | no |
| 150-159 | lifetime energy: DC out, AC out, PV charge, grid charge, feed-back | 5 x u32 /10 kWh | map (154 POWER_GENERATION: AC2A, AC2P), else no |
| 160 low | charging mode | u8 | no |
| 161 low | inverter working status | u8 | no |
| 162-163, 164 low | PV-to-AC energy /10, self-sufficiency % | u32, u8 | no |
| 165-170 | PV-to-AC power, pack discharge energy /10, rated voltage, rated frequency | u32, u32, u16, u16 | no |
| 171 | components online | bits | no |
| 188 | feature status | bits | no |
| 214 | AC input status | u16 | no |

Watcher note: 142 and 146 are signed in the app. A negative value (feed-back) would read as a large u16 today
[inferred: unlikely on a portable station].

### 2.4 Page 1100 - inverter base info [code: ProtocolParserV2.parseInvBaseInfo]

Length 45 to 100 registers [code: getReadRegLen].

| Reg | Field | Type, scale | Watcher |
|---|---|---|---|
| 1100 low | inverter id | u8 | no |
| 1101-1106, 1107-1110 | inverter type ASCII, inverter serial | | no |
| 1111 low | `invPowerType` | u8 | no |
| 1113-1130 | 6 firmware entries: type u16, version u32 | | no |
| 1148 low, 1149 low | working time number, voltage type (version 2001+, else 1142, 1143) | u8 | no |
| 1151 | ambient temperature | u16 - 40 °C (0 = none) | no |
| 1152 | inverter max temperature | u16 - 40 °C | no |
| 1153 | PV DC-DC max temperature | u16 - 40 °C (temps need version 2005+) | no |
| 1155-1160 | charge and discharge power limits per phase | u16 W | no |
| 1161-1169 | rated input, output and grid input current per phase | u16 | no |

### 2.5 Page 1200 - PV [code: ProtocolParserV2.parseInvPVInfo]

Length 90 registers (42 at version 2000) [code: getReadRegLen].

| Reg | Field | Type, scale | Watcher |
|---|---|---|---|
| 1200-1201 | total PV power | u32 W | no |
| 1202-1203 | total PV energy | u32 /10 kWh | map (1202 POWER_GENERATION: EP600) |
| 1209 low | AC PV count (high nibble), DC PV count (low nibble) | u4, u4 | no |
| 1210 + 8n | input n: working status (low), type (1211 low), power 1212, voltage 1213 /10 V, current 1214 /10 A | | map (PV_S1 1212-1214, PV_S2 1220-1222: EP600, EP760, EP2000. 1213-1214 DC_INPUT_VOLTAGE, DC_INPUT_CURRENT: AC70, AC70P, AC180, AC180T, Handsfree 1. 1228-1230, 1236-1238, 1244-1246 as SM_P1-3: EP600, SM_P1: EP760) |

### 2.6 Page 1300 - grid (AC input) [code: ProtocolParserV2.parseInvGridInfo]

Length 31 registers (43 at version 2022+) [code: getReadRegLen].

| Reg | Field | Type, scale | Watcher |
|---|---|---|---|
| 1300 | grid frequency | u16 /10 Hz | map (AC_INPUT_FREQUENCY: AC70, AC70P, AC180, AC180T, Handsfree 1. GRID_FREQUENCY: EP600, EP760, EP2000) |
| 1301-1302 | total charge power | s32 W | no |
| 1303-1304 | charge energy | u32 /10 kWh | no |
| 1305-1306 | feed-back energy, high word first (`listJoinString`, no swap) | u32 /10 kWh | no |
| 1312 low | phase count (min with the configured count) | u8 | no |
| 1313 + 6n | phase power | s16, abs, W | map (GRID_P1-3_POWER: EP600, P1: EP760) |
| 1314 + 6n | **grid voltage** | u16 /10 V | map (1314 AC_INPUT_VOLTAGE: AC60, AC60P, AC70, AC70P, AC180, AC180T, AC180P, AP300, EL10, EL30V2, EL100V2, Handsfree 1, PR30V2, PR100V2. GRID_P1-3_VOLTAGE: EP600, EP2000, P1: EP760) |
| 1315 + 6n | grid current | s16, abs, /10 A | map (1315 AC_INPUT_CURRENT: AC70, AC70P, AC180, AC180T, Handsfree 1, EL10, EL100V2. GRID_P1-3_CURRENT: EP600, P1: EP760) |
| 1316 + 6n | apparent power | s16, abs | no |

32-bit phase powers at 1331 and 1337 are used only for `HOME_POWER` at version 2022+ [code: same, condition `z`].

Live: an AC200PL answers page 1300 with exception 0x02. Its grid voltage is V1 register 77 (/10 V), frequency 80
(/10 Hz).

### 2.7 Page 1400 - loads [code: ProtocolParserV2.parseInvLoadInfo]

Length 48 to 92 registers [code: getReadRegLen].

| Reg | Field | Type, scale | Watcher |
|---|---|---|---|
| 1400-1401, 1402-1403 | DC load power, DC load energy | u32 W, u32 /10 kWh | no |
| 1404/1405, 1406/1407, 1408/1409 | 5 V, 12 V, 24 V outputs: power W, current /10 A | u16 | no |
| 1412, 1413 | DC total voltage, current | u16 /10 | no |
| 1420-1421, 1422-1423 | AC load power, AC load energy | u32 W, u32 /10 kWh | no |
| 1429 low | phase count | u8 | no |
| 1430 + 6n | phase load power, voltage 1431 /10 V, current 1432 /10 A, apparent 1433 | u16 | no |

### 2.8 Page 1500 - inverter output [code: ProtocolParserV2.parseInvInvInfo]

Length 30 registers (36 at version 2023+) [code: getReadRegLen].

| Reg | Field | Type, scale | Watcher |
|---|---|---|---|
| 1500 | output frequency | u16 /10 Hz | map (AC_OUTPUT_FREQUENCY: AC70, AC70P, AC180, AC180T, Handsfree 1, EP600, EP760, EP2000) |
| 1501-1502 | total inverter energy | u32 /10 kWh | no |
| 1508 low | phase count | u8 | no |
| 1509 + 6n | phase work status (low byte) | u8 | map (1509 AC_OUTPUT_ON: AC2A) |
| 1510, 1511, 1512 (+ 6n) | phase power W, voltage /10 V, current /10 A | u16 | map (1511 AC_OUTPUT_VOLTAGE: AC70, AC70P, AC180, AC180T, Handsfree 1, EL10, EL100V2. AC_P1 1510-1512: EP600, EP760. AC_P1-3_VOLTAGE 1511, 1518, 1525: EP2000. AC_P2, AC_P3 at 1517, 1524: EP600, a stride of 7) |

## 3. Warning and fault codes

- Words: page 100, registers 126-129 (warnings) and 133-138 (faults) [code: ProtocolParserV2.parseHomeData,
  `dataRes.subList(52, 60)` and `subList(66, 78)`].
- Decoding [code: ProtocolParse.getLogInfo, getAlarmFaultInfo]: a word equal to 0 is skipped. Word `i` (1-based) looks
  up `names[i]`, a list of Android string resource ids, one per bit. A set bit with a non-zero id yields an
  `AlarmFaultInfo` with number = start + 16 x (i - 1) + bit, the prefix and the type (1 warning, 2 fault). Bit 0 is the
  least significant [inferred: index taken from the end of the bit array].
- Tables [code: ConnConstantsV2.<clinit> smali]: `lowPowerWarnNames`, `highPowerWarnNames`, `lowPowerFaultNames`,
  `highPowerFaultNames`, `microInvWarnNames`, `microInvFaultNames`, each `Map<Integer, List<Integer>>`. About 45 such
  maps exist for chargers, packs, panels, meters.
- Choice [code: parseHomeData]: `invPowerType` 3 = high-power maps, prefix "B", warnings start at 97. Other values =
  low-power maps, prefix "E", warnings start at 113. Faults start at 1. Micro inverters use "C".
- Sizes of the low-power tables (string ids counted in the smali): warnings word 1 = 15, word 2 = 8. Faults word 1 =
  16, word 2 = 12, word 3 = 24, word 4 = 12, word 5 = 13.
- Sample entries (resource names, source order): `device_low_power_warn1_01`, `warn1_02`, `warn2_01`,
  `device_low_power_fault1_01`, `fault1_02`. Their English texts live in `res/values/strings.xml` and were not
  resolved (section 5). So a set bit 0 of warning word 1 on a low-power station would be code "E113" [inferred: the
  display join of prefix and number was not read].
- `AlarmFaultInfo` (in `ui.connect.bean`) carries number, nameResId, codePrefix, type, status [code: class fields].

## 4. Read-only feature ideas, by value

Ideas 1, 10 and 11 work on the AC200PL today (V1, live-verified). The others need a V2 station. All are reads under
register 2000.

1. **Mains present at 0 W.** Grid voltage and frequency show mains even when a full battery draws ~0 W of AC input,
   which today leaves the mains state unknown. Also a log of brown-outs and unstable frequency before a cut. AC200PL:
   V1 77 (/10 V) and 80 (/10 Hz). V2 models: 1314 and 1300.
2. **Station warnings and faults.** Decode 126-129 and 133-138 into codes, show them on the page, store an event when
   one appears or clears, put them in `/api/health`.
3. **Battery current and voltage.** 100 and 101 give the real charge or discharge current, a better input for the
   battery-time model than SOC steps. 103 gives the station's own charging state.
4. **Station time to empty or full.** 104 and 105, shown next to the watcher's estimate (design.md, section 7), once
   their unit is confirmed on a device.
5. **Temperatures.** 1151-1153 (ambient, inverter, PV DC-DC): a warning before a thermal derate or shutdown.
6. **Lifetime energy counters.** 150-159 and 1501-1502: daily kWh in and out from counter deltas, no integration of
   minute powers, and a cross-check of the cloud history back-fill.
7. **Output quality.** 1500, 1511, 1512: inverter voltage, frequency and current per phase on the page.
8. **PV per input.** 1210 + 8n: power, voltage, current per solar input.
9. **Model check.** 110-119 are in the map already: compare the station's own model with `type` at start and log a
   mismatch.
10. **Cell voltages.** V1 105-120: 16 cells in mV. Spread between the lowest and highest cell flags pack imbalance.
11. **Temperatures (to confirm).** V1 121-123, likely temperatures, unit not confirmed.

## 5. Unknowns

- Settled: the AC200PL does not serve the V2 pages, the app reads it over V1 (section "Verified on a live AC200PL").
- The meaning of V1 73, 76, 79 and the unit of 121-123.
- The protocol version the station reports, and so which version gates apply. The register that carries it was not
  traced (page 1, 16 registers, is a candidate [inferred from getReadRegLen]).
- Which code schedules the periodic reads for a `PORTABLE_POWER` station, and its interval.
- The response timeout and retry policy (`BluettiBleManager.retryCount`, disconnect timeouts), and the MTU the app asks.
- Units of 104 and 105 (minutes is the likely one), and the codes of 103, 160, 161, 214.
- The English texts of the warning and fault resource ids, and how the app formats a code (`codeFormat`).
- The high-power tables (`invPowerType` 3) were not counted. Which `invPowerType` an AC200PL reports is not known.
