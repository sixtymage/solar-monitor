# Deye SUN-8K-SG01LP1-EU — Modbus Register Map

## Overview

Three-phase installation with three single-phase 8kW inverters (one per phase).
Each inverter is polled independently at its own IP via the Solarman LSW-3 WiFi
dongle (SolarmanV5 protocol, port 8899, Modbus device address 1).

**Topology:** All three inverters are configured as **phase masters** in a
synchronized 3-phase setup (UI "Parallel Information" field reads M1A / M3B /
M2C — Master, address, Phase A/B/C). There is no master/slave hierarchy.

**Battery:** Dyness LiFePO4, 48V nominal, one bank on a DC bus shared by all
three inverters. The BMS talks CAN to **one** inverter (the only one with a
battery temperature, reg 182); the others get state of charge over the
parallel link.

**Aggregation (corrected Nov 2026):**
- **183 (voltage) and 184 (SOC) are bus-wide.** Every inverter reports the same
  value, so don't sum them.
- **190/191 (battery power/current) are per inverter**: each inverter's own DC
  flow into or out of the bank. **Sum them** for the bank. Evidence: on each
  inverter, AC intake (175) + PV (186) ≈ its own battery power at ~95%
  conversion efficiency. And with three inverters each charging at −2,035 W
  (the 40 A grid-charge limit), SOC rose 2% in 10 minutes. That needs
  ~6.1 kW on a ~54 kWh bank, not 2 kW. The April note ("do not aggregate
  190/191, this triple-counts") was wrong for these two.

**Reference:** Register layout verified against `kbialek/deye-inverter-mqtt`
`metric_group_deye_sg02lp1` and `metric_group_deye_sg02lp1_battery`.
The SUN-8K-SG01LP1 shares the same register layout as the SG02LP1.

---

## Status legend

| Symbol | Meaning |
|--------|---------|
| ✅ | Confirmed — directly compared against inverter display reading |
| ⚡ | Internally consistent — validated by physical cross-check (e.g. P = V × I) |
| 🔵 | Probable — value is physically plausible and matches community register map |
| ❓ | Unverified — seen in data, purpose unknown |

---

## Battery registers (180–191)

| Reg | Name | Unit | Scale | Type | Status | Notes |
|-----|------|------|-------|------|--------|-------|
| 182 | Battery temperature | °C | raw × 0.1 − 100 | U_WORD | 🔵 | Phase 1 reads 30.4°C correctly; Phases 2 & 3 return literal `0` (decodes to −100°C). Sensor populated only on the inverter directly wired to it; encoding itself is correct. |
| 183 | Battery voltage | V | × 0.01 | U_WORD | ✅ | UI confirms LV-48V battery type; probe 50.31V at 59% SOC — physically consistent with LiFePO4 charging |
| 184 | Battery SOC | % | × 1 | U_WORD | ✅ | All three inverters report identical SOC via shared BMS |
| 185 | Battery power (alt?) | W | × 1 | S_WORD | ❓ | May duplicate reg 190; sign convention unverified |
| 189 | Battery status | — | — | U_WORD | 🔵 | 0 = normal; full status bitmask TBD |
| 190 | Battery power | W | × 1 | S_WORD | ✅ | reg[190] × 1W = reg[191] × 0.01A × reg[183] × 0.01V ✓ — exact match across all 3 inverters (e.g. 50.31 × −40.02 = −2013W) |
| 191 | Battery current | A | × 0.01 | S_WORD | ✅ | Sign convention **confirmed**: negative = charging, positive = discharging. Verified during grid-charge cycle (PV + grid > load → battery storing → reg 191 negative). |

> **Temperature encoding:** Deye stores temperatures as `(°C + 100) × 10`.
> Formula: `actual_temp = raw × 0.1 − 100`. Allows representation of sub-zero values.

---

## PV registers

| Reg | Name | Unit | Scale | Type | Status | Notes |
|-----|------|------|-------|------|--------|-------|
| 109 | PV1 voltage | V | × 0.1 | U_WORD | ✅ | Phase 3: probe 351.9V vs UI 352.00V — exact |
| 110 | PV1 current | A | × 0.1 | U_WORD | ✅ | Phase 3: probe 1.9A vs UI 1.90A — exact |
| 111 | PV2 voltage | V | × 0.1 | U_WORD | ✅ | Phase 3: probe 1.5V vs UI 1.50V — exact (PV2 effectively unused, just sensor noise) |
| 112 | PV2 current | A | × 0.1 | U_WORD | ✅ | Phase 1: probe 0.1A vs UI 0.10A — exact |
| 113 | PV3 voltage | V | × 0.1 | U_WORD | ✅ | All inverters: 0 — matches UI (PV3 not wired) |
| 114 | PV3 current | A | × 0.1 | U_WORD | ✅ | All inverters: 0 — matches UI (PV3 not wired) |
| 186 | PV1 power | W | × 1 | U_WORD | ✅ | Phase 3: probe 658W vs UI 670W — within minute-scale irradiance drift |
| 187 | PV2 power | W | × 1 | U_WORD | 🔵 | Always 0W in observed data — scale unconfirmable until non-zero reading |
| 188 | PV3 power | W | × 1 | U_WORD | 🔵 | Always 0W; PV3 unwired |

---

## Grid / AC registers

| Reg | Name | Unit | Scale | Type | Status | Notes |
|-----|------|------|-------|------|--------|-------|
| 150 | Grid voltage L1 | V | × 0.1 | U_WORD | ✅ | UI cross-check: probe 225.7 / 236.3 / 232.6 V vs UI 224.6 / 236.1 / 232.2 V across all 3 phases |
| 167 | Grid power, internal CT | W | × 1 | S_WORD | ⚡ | Identical to 169 in all observed data |
| 169 | **Grid power** | W | × 1 | S_WORD | ⚡ | **+ import, − export.** Energy balance on all three inverters (Nov 2026): 169 ≈ 178 − 175 within ~1% (e.g. 2106 ≈ 112 + 1979). It isn't the inverter's output, which is why it didn't match the UI's "Inverter Output Power" |
| 173 | Inverter output power L1L2 | W | × 1 | S_WORD | ✅ | UI cross-check: probe −1737 / −1728 / −1749 W vs UI −1818 / −1746 / −1673 W. **+ inverter supplying AC, − inverter taking AC in** (charging the battery from the grid). |
| 175 | Inverter output power (mirror of 173) | W | × 1 | S_WORD | ⚡ | Always identical to reg 173; used in the energy balance above |
| 178 | **Load power** | W | × 1 | S_WORD | ⚡ | Closes the balance 169 = 178 − 175 on all three inverters. This is the "total load power" 175 was once thought to be |
| 192 | Grid frequency | Hz | × 0.01 | U_WORD | ✅ | UI cross-check: probe 49.91 Hz vs UI 49.83–49.96 Hz across all 3 phases |
| 193 | Inverter frequency | Hz | × 0.01 | U_WORD | 🔵 | Always identical to reg 192 in observed data — likely the inverter-side sync frequency |

---

## Production / energy registers

| Reg | Name | Unit | Scale | Type | Status | Notes |
|-----|------|------|-------|------|--------|-------|
| 96 / 97 | Cumulative production (low / high) | kWh | × 0.1 | U_DWORD | ✅ | Little-endian word order: `total = reg[96] + reg[97] × 65536`. UI cross-check: probe 21.52 / 23.61 / 27.03 MWh vs UI 21.51 / 23.61 / 27.03 MWh — exact across all 3 inverters. |
| 108 | Daily production | kWh | × 0.1 | U_WORD | ✅ | UI cross-check across all 3 inverters: 81→8.1, 89→8.9, 121→12.1 kWh vs UI 8.1, 8.9, 12.0 kWh — exact |
| 70 | Daily battery charge | kWh | × 0.1 | U_WORD | 🔵 | Captured: 3.7 / 11.0 / 6.7 kWh. Per-inverter contributions, not BMS-totals. UI page does not surface for direct verification. |
| 71 | Daily battery discharge | kWh | × 0.1 | U_WORD | 🔵 | Captured: 7.9 / 0.3 / 8.5 kWh. P2's low value is real: P2's phase carries little load, so its inverter mostly *feeds* the shared bank, and the other two draw from it. Lifetime counters (72–75) show the same pattern |
| 72 / 73 | Total battery charge (low / high) | kWh | × 0.1 | U_DWORD | 🔵 | Per inverter. Summed over all three, discharge ÷ charge ≈ 0.88, a plausible round trip |
| 74 / 75 | Total battery discharge (low / high) | kWh | × 0.1 | U_DWORD | 🔵 | Per inverter |
| 78 / 80 | Total energy bought (low / high) | kWh | × 0.1 | U_DWORD | 🔵 | **Not contiguous**: 79 sits in between |
| 79 | Grid frequency | Hz | × 0.01 | U_WORD | 🔵 | Same value as 192 |
| 81 / 82 | Total energy sold (low / high) | kWh | × 0.1 | U_DWORD | 🔵 | |
| 84 | Daily load consumption | kWh | × 0.1 | U_WORD | 🔵 | |
| 85 / 86 | Total load consumption (low / high) | kWh | × 0.1 | U_DWORD | 🔵 | Lifetime balance PV + bought − sold − load ≈ 9.5% losses, plausible |
| 76 | Daily energy bought | kWh | × 0.1 | U_WORD | 🔵 | Captured: 11.0 / 2.8 / 12.3 kWh |
| 77 | Daily energy sold | kWh | × 0.1 | U_WORD | 🔵 | Captured: 0 / 0.5 / 0 kWh |

---

## Temperature / system registers

| Reg | Name | Unit | Scale | Type | Status | Notes |
|-----|------|------|-------|------|--------|-------|
| 90 | DC (radiator) temperature | °C | × 0.1 − 100 | S_WORD | 🔵 | Same encoding as battery temp assumed |
| 91 | AC temperature | °C | × 0.1 − 100 | S_WORD | 🔵 | Same encoding as battery temp assumed |

---

## Identity and state registers

| Reg | Name | Unit | Scale | Type | Status | Notes |
|-----|------|------|-------|------|--------|-------|
| 3–7 | Inverter serial | — | ASCII, 2 chars per register, high byte first | — | ✅ | Matches the COC and the logger's hostname on all three |
| 16 / 17 | Rated power (low / high) | W | × 0.1 | U_DWORD | 🔵 | 80000 → 8000 W |
| 59 | Running state | — | 0 standby, 1 self-check, 2 normal, 3 alarm, 4 fault | U_WORD | 🔵 | 2 on all three in normal running |

## Settings registers (200–280)

Read with function 0x03 like everything else; stored by the poller in `settings_snapshot` when they
change. Names are from the community Sunsynk/Deye single-phase map. ⚡ marks the ones that match
the inverters' observed behaviour.

| Reg | Name | Scale | Status | Notes |
|-----|------|-------|--------|-------|
| 201 / 202 | Equalisation / absorption voltage | × 0.01 V | 🔵 | 57.60 V |
| 203 | Float voltage | × 0.01 V | 🔵 | 55.20 V |
| 204 | Battery capacity | Ah | 🔵 | 1200 = 16 × 75 Ah modules |
| 205 | Empty voltage | × 0.01 V | 🔵 | 45.00 V |
| 210 | Max charge current | A | ⚡ | 40. Each inverter charges at exactly 40 A |
| 211 | Max discharge current | A | 🔵 | 185 |
| 217 / 218 / 219 | Shutdown / restart / low SOC | % | 🔵 | 20 / 25 / 25 |
| 220 / 221 / 222 | Shutdown / restart / low voltage | × 0.01 V | 🔵 | 46.00 / 52.00 / 47.50 |
| 230 | Grid charge current | A | ⚡ | 40 |
| 232 | Grid charge enabled | 0/1 | ⚡ | 1 |
| 248 | Time-of-use enable + weekdays | bitmask | 🔵 | 255 = on, every day |
| 250–255 | TOU slot start times | HHMM | 🔵 | 0100, 0500, 0900, 1300, 1700, 2100 |
| 256–261 | TOU slot power | W | 🔵 | 8000 each |
| 262–267 | TOU slot voltage | × 0.01 V | 🔵 | 49.00 each (used in voltage mode) |
| 268–273 | TOU slot SOC (floor, or grid-charge target) | % | ⚡ | 50, 40, 100, 100, 80, 60 |
| 274–279 | TOU slot charge flags | bit 0 = grid charge | ⚡ | 4, 4, 4, **5**, 4, 4: only the 13:00 slot grid-charges, which is what happens |

## Unresolved registers (seen in probe data, purpose unknown)

| Reg | Observed values | Notes |
|-----|----------------|-------|
| 100 | 2561 (0x0A01) | Constant across readings — possibly firmware version (10.01?) |
| 107 | 1200 (constant) | Possibly a configuration value (max charge current × 0.01A = 12A? or charge voltage?) |
| 119 | 7690 | Large value, changes slowly — possible total yield (×0.1 kWh = 769 kWh?) |
| 121 | 6–7 | Small integer, possibly inverter operating mode |
| 122 | 0x8F5E (–28834 signed) | Large negative signed — purpose unknown |
| 123 | 7571–7685 | Slowly varying large value — purpose unknown |
| 130 | 78–96 | Tracks SOC readings closely — possibly a second SOC field or average |
| 131 | 13556–13558 | Large slowly-varying value — possible total battery cycles or yield |
| 137 | 0xFFF5 (–11 signed) | Small negative constant — possibly a calibration offset |
| 138 | 1151–1168 | Pair with reg 139, similar values — possibly AC voltage or power readings |
| 139 | 1151–1167 | Pair with reg 138 |

---

## Register ranges not yet probed

The poller reads 0–58 at start-up, 59–199 every poll and 200–280 hourly. Raw values are stored, so
everything below can be investigated from the database.

**AC current is still unaccounted for.** Solarman UI shows 7.0–7.9 A across the
three phases, but no register in the probed ranges holds a value matching `70`,
`79`, `700`, or `790` for the appropriate phase. Likely candidates for the next
probe extension:

| Range | Rationale |
|-------|-----------|
| 0–59 | Some Deye firmware variants place grid I/O measurements at low addresses |
| 220–279 | Extended/auxiliary readings on newer firmware revisions |

---

## Hardware reference

Three inverters, one per phase. Each has a dedicated Solarman LSW-3 dongle with
a reserved DHCP address (assigned by MAC in the router).

IPs, serial numbers, and MAC addresses are **not committed to source control**.
Configure them via environment variables or `appsettings.local.json` (gitignored).
See `docker/docker-compose.yml` for the expected environment variable names.

> Dongles reset occasionally under repeated failed Modbus polls — a production
> poller must use connection retry with exponential backoff.
