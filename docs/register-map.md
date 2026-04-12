# Deye SUN-8K-SG01LP1-EU — Modbus Register Map

## Overview

Three-phase installation with three single-phase 8kW inverters (one per phase).
Each inverter is polled independently at its own IP via the Solarman LSW-3 WiFi
dongle (SolarmanV5 protocol, port 8899, Modbus device address 1).

**Battery:** Dyness LiFePO4, 48V nominal, communicating via Li-BMS (CAN).

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
| 182 | Battery temperature | °C | raw × 0.1 − 100 | U_WORD | ✅ | Display confirmed 33.4°C exactly |
| 183 | Battery voltage | V | × 0.01 | U_WORD | ✅ | Display confirmed 49.81V (probe: 49.80V) |
| 184 | Battery SOC | % | × 1 | U_WORD | ✅ | Display 93% / probe 94% — 5-min drift |
| 185 | Battery power (alt?) | W | × 1 | S_WORD | ❓ | May duplicate reg 190; sign convention unverified |
| 189 | Battery status | — | — | U_WORD | 🔵 | 0 = normal; full status bitmask TBD |
| 190 | Battery power | W | × 1 | S_WORD | ⚡ | reg[190] × 1W = reg[191] × 0.01A × reg[183] × 0.01V ✓ |
| 191 | Battery current | A | × 0.01 | S_WORD | ⚡ | Sign convention: positive = **discharging** (to confirm vs display) |

> **Temperature encoding:** Deye stores temperatures as `(°C + 100) × 10`.
> Formula: `actual_temp = raw × 0.1 − 100`. Allows representation of sub-zero values.
>
> **Current sign convention:** Probe data shows positive current while SOC was
> declining (discharging). Needs one more side-by-side verification to confirm
> positive = discharge, negative = charge.

---

## PV registers

| Reg | Name | Unit | Scale | Type | Status | Notes |
|-----|------|------|-------|------|--------|-------|
| 109 | PV1 voltage | V | × 0.1 | U_WORD | 🔵 | 340V in sun, 91V in low light — plausible |
| 110 | PV1 current | A | × 0.1 | U_WORD | 🔵 | Varies with irradiance as expected |
| 111 | PV2 voltage | V | × 0.1 | U_WORD | 🔵 | Near zero in current readings |
| 112 | PV2 current | A | × 0.1 | U_WORD | 🔵 | Near zero in current readings |
| 186 | PV1 power | W | × 1 | U_WORD | 🔵 | 15W in low light; 118W in partial sun — trend plausible |
| 187 | PV2 power | W | × 1 | U_WORD | 🔵 | 0W — PV2 string may be absent or unconnected |
| 188 | PV3 power | W | × 1 | U_WORD | 🔵 | 0W |

---

## Grid / AC registers

| Reg | Name | Unit | Scale | Type | Status | Notes |
|-----|------|------|-------|------|--------|-------|
| 150 | Grid voltage L1 | V | × 0.1 | U_WORD | 🔵 | Not yet read in probe range |
| 167 | CT L1 power | W | × 1 | S_WORD | 🔵 | Not yet read in probe range |
| 169 | Total grid power | W | × 10 | S_WORD | 🔵 | Not yet read; negative = exporting |
| 173 | Inverter L1 power | W | × 1 | S_WORD | 🔵 | Not yet read |
| 175 | Total load power | W | × 1 | S_WORD | 🔵 | Not yet read |
| 192 | Grid frequency | Hz | × 0.01 | U_WORD | ✅ | reg = 5000 / 5001 → 50.00 / 50.01 Hz ✓ |

---

## Production / energy registers

| Reg | Name | Unit | Scale | Type | Status | Notes |
|-----|------|------|-------|------|--------|-------|
| 96–97 | Total production | kWh | × 0.1 | U_DWORD | 🔵 | Not yet read (outside current probe range) |
| 108 | Daily production | kWh | × 0.1 | U_WORD | 🔵 | reg = 79 → 7.9 kWh; consistent with late-afternoon reading |
| 70 | Daily battery charge | kWh | × 0.1 | U_WORD | 🔵 | Not yet read |
| 71 | Daily battery discharge | kWh | × 0.1 | U_WORD | 🔵 | Not yet read |
| 76 | Daily energy bought | kWh | × 0.1 | U_WORD | 🔵 | Not yet read |
| 77 | Daily energy sold | kWh | × 0.1 | U_WORD | 🔵 | Not yet read |

---

## Temperature / system registers

| Reg | Name | Unit | Scale | Type | Status | Notes |
|-----|------|------|-------|------|--------|-------|
| 90 | DC (radiator) temperature | °C | × 0.1 − 100 | S_WORD | 🔵 | Same encoding as battery temp assumed |
| 91 | AC temperature | °C | × 0.1 − 100 | S_WORD | 🔵 | Same encoding as battery temp assumed |

---

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

The probe currently reads two ranges: 100–139 and 180–219. The following ranges
contain known registers that need to be added:

| Range | Contains |
|-------|---------|
| 70–82 | Daily/total energy bought/sold, battery charge/discharge |
| 90–91 | Inverter temperatures |
| 96–97 | Total production (DWORD) |
| 150–175 | Grid voltages, CT power, AC currents, inverter power |

---

## Hardware reference

Three inverters, one per phase. Each has a dedicated Solarman LSW-3 dongle with
a reserved DHCP address (assigned by MAC in the router).

IPs, serial numbers, and MAC addresses are **not committed to source control**.
Configure them via environment variables or `appsettings.local.json` (gitignored).
See `docker/docker-compose.yml` for the expected environment variable names.

> Dongles reset occasionally under repeated failed Modbus polls — a production
> poller must use connection retry with exponential backoff.
