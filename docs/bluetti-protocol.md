# BLUETTI Bluetooth protocol

The wire format the native Bluetooth reader speaks (`src/BluettiWatcher/Io/Ble/`). The register maps are ported from
[bluetti-bt-lib](https://github.com/Patrick762/bluetti-bt-lib) 0.1.8 (MIT); the AC200PL grid registers and the V2 extra
blocks come from reading the official app and a live AC200PL. Polling, timeouts and the cloud fallback are behaviour:
see [design.md](design.md). The keys `type`, `mac` and `readerTimeoutSec`: see [configuration.md](configuration.md).

The reader is read only. The only frame it can build is function 0x03, and never over register 2000 or above, where
every V1 and V2 control register lies (`ModbusRead.Encode` throws otherwise).

## 1. Transport

### 1.1 GATT

| Item | Value |
|---|---|
| Notify characteristic (answers) | `0000ff01-0000-1000-8000-00805f9b34fb` |
| Write characteristic (requests) | `0000ff02-0000-1000-8000-00805f9b34fb` |
| Service | the official app names `0000ff00-0000-1000-8000-00805f9b34fb`; the reader does not filter by service, it takes ff01 and ff02 from any |

- A plain GATT connection: no pairing, no protection level, no encryption (`WinRtBleConnector`).
- Lookup order: Windows' GATT cache first (one try), then uncached (3 tries, 1 s apart) only when the cache fails or
  holds no ff01/ff02 (`GattDiscovery`).
- The reader writes the ff01 client configuration descriptor (Notify), then only read requests to ff02: with response
  when ff02 has the Write property, else without. Closing writes nothing; the subscription is not turned off.
- A device Windows has not seen advertise since boot opens as null: the reader scans for its address (8 s cap, active
  scan), then opens once more.

### 1.2 Read frame

```
request:   01 | 03 | start (2, BE) | count (2, BE) | CRC (2, low byte first)       8 bytes
answer:    01 | 03 | n = 2 x count | data (n bytes, registers BE) | CRC          2 x count + 5 bytes
exception: 01 | 83 | code | CRC                                                  5 bytes
```

- Unit id is always 0x01. `count` is 1 to 32 (`ModbusRead.MaxQuantity`).
- Examples: `01 03 00 24 00 08 04 07` (registers 36+8), `01 03 00 46 00 0B E5 D8` (registers 70+11).
- Exception codes named in logs: 0x01 illegal function, 0x02 illegal data address, 0x03 illegal data value, 0x04
  server device failure, 0x06 server device busy.

### 1.3 CRC

CRC-16/MODBUS: initial 0xFFFF, reflected polynomial 0xA001, appended low byte first (`ModbusCrc`).

### 1.4 Answer assembly

- An answer may arrive split over several notifications. `ResponseAssembler` collects bytes until the frame is whole
  and rejects a wrong unit, a wrong function, a byte count other than 2 x count, extra bytes or a bad CRC.
- A Modbus answer does not name its registers. The reader remembers its unanswered requests (at most 16) and assumes
  the station answers in order: a whole answer whose byte count fits an earlier unanswered request is taken as that
  request's late answer, set aside, and the same request is written again (at most 2 more times).
- Bytes that arrive between two requests are never read as the next request's answer.
- A batch the station refuses with an exception is asked again field by field.

### 1.5 Encrypted stations

Some stations open an encrypted protocol with a key exchange. The reader speaks the unencrypted protocol only and
detects the exchange (`KeyExchangeGuardLink`):

- A pre-key-exchange message is `2A 2A` ("**"), a body whose first byte is the type (1 = challenge, 3 = challenge
  accepted), then the 16-bit sum of the body, high byte first. A challenge body is `01 04` and a 4-byte seed.
- No Modbus answer can start with it: answers start with unit 0x01.
- The first notifications of a link are held while they could still be the start of such a message (up to 64 bytes),
  so a message split over notifications is still caught. A whole message later on the link is caught too.
- On detection the poll ends as a miss and the reader does not connect to that station again until the service
  restarts; the cloud reader answers instead.

## 2. Register maps

### 2.1 Encoding

- Register r of a read from `start` is body bytes `2 x (r - start)` and the next, big-endian u16.
- A field is decoded only when all its registers lie inside the registers read.
- The model comes from the `type` key, matched case-sensitively against the names below.

| Kind | Decoding |
|---|---|
| UInt | u16. V1: times the multiplier. V2: with a multiplier other than 1, `round(raw x multiplier, 2)` |
| Decimal | `raw x multiplier / 10^scale` |
| Output mode (V1) | 0 `STOP`, 1 `INVERTER_OUTPUT`, 2 `BYPASS_OUTPUT_C`, 3 `BYPASS_OUTPUT_D`, 4 `LOAD_MATCHING`, else none |
| Version (V1) | `(word0 + word1 << 16) / 100`, 2 registers |
| ASCII | V1 as read, V2 with the two bytes of each register swapped; trailing NULs and non-ASCII bytes dropped |
| Serial number | `word0 + word1 << 16 + word2 << 32 + word3 << 48`, 4 registers |
| Bool (V2) | 0 false, 1 true, else none |

A UInt with a range (SOC: 0 to 100) outside it is dropped.

### 2.2 What a poll reads

A poll reads only the reading's fields: SOC, AC input power, AC output power, output mode, DC input power, DC output
power, the internal AC voltage and frequency, and on the AC200PL the grid voltage and frequency. Fields are batched into
as few requests as possible: a gap of up to 4 unused registers is read through, no request is over 32 registers.

| Station | Requests |
|---|---|
| AC200PL | 36+8 and 70+11 |
| V2 models with the four power fields | 102+1 and 140+7 |

The other declared fields (type, serial, versions, PV and phase details) are part of the maps but no poll reads them.

Models without an AC input power field (EP600, EP760, EP800, EP2000) give no Bluetooth reading: battery % alone cannot
tell mains from battery, so the cloud fallback answers. Models without an output mode field (EB3A and every V2 model) get a
derived mode: AC input above 0 W = `BLE_MAINS`; 0 W in while AC output feeds a load = `INVERTER_OUTPUT`; else unknown.

### 2.3 V1 (IoT protocol V1)

Models: AC200L, AC200M, AC200PL, AC300, AC500, EB3A, EP500, EP500P (`V1Models`). Bold = read in a poll.

| Reg | Field | Decoding | Models |
|---|---|---|---|
| 10-15 | `DEVICE_TYPE` | ASCII, 6 registers | all |
| 17-20 | `DEVICE_SN` | serial number | all |
| 23-24, 25-26 | `VER_ARM`, `VER_DSP` | version | AC200L, AC300, EB3A |
| **36** | `DC_INPUT_POWER` | u16 W | all |
| **37** | `AC_INPUT_POWER` | u16 W | all |
| **38** | `AC_OUTPUT_POWER` | u16 W | all |
| **39** | `DC_OUTPUT_POWER` | u16 W | all |
| **43** | `BATTERY_SOC` | u16 %, 0 to 100 | all |
| 43 | `POWER_GENERATION` | /10 | AC300 (declared by the library at the SOC register) |
| **70** | `AC_OUTPUT_MODE` | output mode | all but EB3A |
| **71** | `INTERNAL_AC_VOLTAGE` | u16 V (AC200L, AC200M, AC200PL, AC500); /10 V (AC300) | |
| **74** | `INTERNAL_AC_FREQUENCY` | /10 Hz (AC200L, AC200M, AC200PL, AC500); /100 Hz (AC300) | |
| 71, 74 | `AC_OUTPUT_VOLTAGE`, `AC_OUTPUT_FREQUENCY` | /10 V, /100 Hz | EP500, EP500P |
| **77** | `GRID_INPUT_VOLTAGE` | /10 V | AC200PL |
| **80** | `GRID_INPUT_FREQUENCY` | /10 Hz | AC200PL |
| 77, 80 | `AC_INPUT_VOLTAGE`, `AC_INPUT_FREQUENCY` | /10 V, /100 Hz | AC300, AC500, EP500, EP500P (77 only on EB3A) |
| 86, 87, 88 | `INTERNAL_DC_INPUT_VOLTAGE`, `_POWER`, `_CURRENT` | /10 V, u16 W, /100 A | AC200L, AC200M, AC200PL |
| 86, 87, 88 | `PV_S1_VOLTAGE`, `_POWER`, `_CURRENT` | /10 V, u16 W, /10 A | AC300, AC500, EP500, EP500P |
| 86 | `DC_INPUT_VOLTAGE` | /100 V | EB3A |

The AC200PL grid fields (77 and 80, both /10) are not in the library; the official app reads them in its 70+87 read.
Other names than the library's `AC_INPUT_*` keep other models from reading them with the AC200PL's scale.

Read on a live AC200PL but not decoded: 73, 76, 79 (powers, meaning not confirmed), 105-120 (16 cell voltages, mV),
121-123 (likely temperatures, unit not confirmed).

### 2.4 V2 (IoT protocol V2)

Models: AC2A, AC2P, AC50B, AC60, AC60P, AC70, AC70P, AC180, AC180P, AC180T, AP300, EL10, EL30V2, EL100V2, EP600, EP760,
EP800, EP2000, Handsfree 1, PR30V2, PR100V2 (`V2Models`). The frame is the V1 frame; only the map differs.

| Reg | Field | Decoding | Models |
|---|---|---|---|
| **102** | `BATTERY_SOC` | u16 %, 0 to 100 | all |
| 104 | `TIME_REMAINING` | /10 (AC50B, AC70, Handsfree 1); /60 (EL10, EL100V2); x 0.0167 (EL30V2) | |
| 110-115 | `DEVICE_TYPE` | swapped ASCII, 6 registers | all |
| 116-119 | `DEVICE_SN` | serial number | all |
| **140** | `DC_OUTPUT_POWER` | u16 W | all with power fields |
| **142** | `AC_OUTPUT_POWER` | u16 W | all with power fields |
| **144** | `DC_INPUT_POWER` | u16 W | all with power fields but AC50B |
| **146** | `AC_INPUT_POWER` | u16 W | all with power fields |
| 154 | `POWER_GENERATION` | /10 | AC2A, AC2P |
| 1202 | `POWER_GENERATION` | /10 | EP600 |
| 1212-1214, 1220-1222 | `PV_S1_*`, `PV_S2_*` power, voltage, current | u16 W, /10 V, /10 A | EP600, EP760, EP2000 |
| 1213, 1214 | `DC_INPUT_VOLTAGE`, `DC_INPUT_CURRENT` | /10 | AC70, AC70P, AC180, AC180T, Handsfree 1 |
| 1228 + 8n | `SM_P1-3_*` smart meter power, voltage, current | u16, /10, u16 | EP600 (P1-P3), EP760 (P1) |
| 1300 | `AC_INPUT_FREQUENCY` or `GRID_FREQUENCY` | /10 Hz | AC70-family details; EP600, EP760, EP2000 |
| 1313 + 6n | `GRID_P1-3_*` power, voltage, current | u16, /10, /10 | EP600 (P1-P3), EP760 (P1); EP2000 voltage only |
| 1314 | `AC_INPUT_VOLTAGE` | /10 V (u16 V on AC60, AC60P) | AC60, AC60P, AC70, AC70P, AC180, AC180P, AC180T, AP300, EL10, EL30V2, EL100V2, Handsfree 1, PR30V2, PR100V2 |
| 1315 | `AC_INPUT_CURRENT` | /10 A | AC70, AC70P, AC180, AC180T, Handsfree 1, EL10, EL100V2 |
| 1500 | `AC_OUTPUT_FREQUENCY` | /10 Hz | AC70-family details; EP600, EP760, EP2000 |
| 1509 | `AC_OUTPUT_ON` | bool | AC2A |
| 1510 + 7n | `AC_P1-3_*` power, voltage, current | u16, /10, /10 | EP600 (P1-P3), EP760 (P1); EP2000 voltage only |
| 1511 | `AC_OUTPUT_VOLTAGE` | /10 V | AC70, AC70P, AC180, AC180T, Handsfree 1, EL10, EL100V2 |

"AC70-family details" = AC70, AC70P, AC180, AC180T, Handsfree 1. EP800 maps only the base fields (102, 110-119).

### 2.5 V2 extra blocks

After the reading, a V2 station is asked these blocks over the same connection (`V2Extras`). They are never asked of a
V1 station: there registers 100-138 carry other data (105-120 are cell voltages). An exception reply marks a block not
supported at once, 2 unanswered polls in a row too; a block not supported is not asked again until the service
restarts. `/api/health` shows each block's state as `stationBlocks` ([api.md](api.md)).

| Block | Request | Registers | Decoding |
|---|---|---|---|
| `battery` | 100+2 | 100 pack voltage, 101 pack current | u16 /10 V, u16 /10 A |
| `stationTime` | 104+2 | 104 time to full, 105 time to empty | u16, taken as minutes (unit not confirmed on a device) |
| `alarms` | 122+17 | 122 low byte `invPowerType`, 126-129 warnings, 133-138 faults | section 3 |
| `temperatures` | 1151+3 | 1151 ambient, 1152 inverter max, 1153 PV DC-DC max | u16 - 40 °C, 0 = no sensor |

## 3. Warning and fault codes

From the `alarms` block, decoded as the official app does (`V2Extras.DecodeAlarms`):

- Word i (1-based), bit k (0 = least significant) set = code `prefix + (first + 16 x (i - 1) + k)`.
- `invPowerType` 3 = high-power table: prefix `B`, warnings start at 97. Any other value = low-power table: prefix `E`,
  warnings start at 113. Faults start at 1 in both.
- Example: low-power station, bit 0 of warning word 1 = `E113`; bit 2 of fault word 2 = `E19`.
- Every set bit becomes a code. The app drops a bit whose table entry is empty; those tables and the code texts are not
  in the watcher.
