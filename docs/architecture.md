# Solar Monitor — Architecture

## Overview

A local energy monitoring system for a three-phase Deye hybrid inverter installation,
replacing dependency on the Solarman cloud platform. All data stays on the home LAN.

## Hardware

- **3× Deye hybrid inverters** (three-phase supply, one inverter per phase)
- **3× Solarman WiFi logger dongles** — one per inverter, connected to home LAN
- **Solar panels** — multiple strings across the three inverters
- **Battery bank** — connected to the hybrid inverters
- **Synology NAS** — always-on, runs Docker, hosts the monitoring stack

## How Solarman Works (and Why We're Replacing It)

The Solarman WiFi dongle connects to the home WiFi and makes **outbound** TCP connections
to Solarman's cloud infrastructure. Data is pushed from the dongle to Chinese servers; the
phone app and web portal are thin clients to that cloud store. No port forwarding is required
on the home router — all traffic is outbound from the dongle.

The same dongle **also listens on port 8899 on the local LAN**, exposing a Modbus-over-SolarmanV5
server. This local endpoint works independently of the cloud connection and is what we use here.
The cloud connection can optionally be firewalled at the router once local monitoring is stable.

## System Components

```
[Deye Dongle ×3]  — LAN, port 8899 (SolarmanV5/Modbus TCP)
       │
[Poller Service]  — C# Worker Service, polls each inverter every 10s
       │
[TimescaleDB]     — Time-series storage (PostgreSQL + TimescaleDB extension)
       │
[ASP.NET Core API] — REST + SignalR; serves both web and mobile clients
       │
[React Web UI]    — TypeScript SPA, PWA-capable for Android install
       │
[Cloudflare Tunnel] — Exposes UI externally; same pattern as existing Plex setup
       │
  solar.thelinksys.co.za
```

## Key Design Decisions

### Local-first, no cloud dependency
All data is collected and stored locally. The Solarman cloud is bypassed entirely.
External access is via Cloudflare Tunnel (authenticated, encrypted) rather than
exposing ports on the home router.

### SolarmanV5 protocol implemented in C#
The `pysolarmanv5` Python library is the community reference, but the protocol is simple
enough to implement directly. C# is the team's primary language and gives us full control
over connection management, error handling, and the polling loop.

### TimescaleDB for storage
PostgreSQL with the TimescaleDB extension. Chosen over InfluxDB because:
- Standard SQL for queries, no proprietary query language to learn
- `time_bucket()` functions cover all aggregation needs
- Familiar tooling (pgAdmin, psql, any Postgres client)
- Hypertable compression keeps storage manageable at home-monitoring scale

### Per-inverter data granularity
Each inverter is stored with its own identity. Aggregated whole-home views are computed
at query time. This preserves diagnostic capability (per-phase imbalance, per-inverter faults).

### Read-only Phase 1
No Modbus write operations in Phase 1. Control operations (charge schedules, operating modes)
are a future phase requiring additional safety design.

### React + TypeScript for the web UI
Chosen over Blazor because:
- Charting ecosystem (ApexCharts, Recharts) is far better suited to time-series dashboards
- Blazor Server's per-connection statefulness adds unnecessary complexity
- PWA packaging is straightforward, covering Android without a native app

### ASP.NET Core API
Single API serves both the web SPA and any future mobile/native clients.
SignalR for live current-state push; REST for historical queries.

## Data Model (Sketch)

```sql
-- One row per inverter per poll cycle (~10s intervals)
CREATE TABLE inverter_readings (
    time            TIMESTAMPTZ NOT NULL,
    inverter_id     SMALLINT    NOT NULL,  -- 0, 1, 2
    pv1_power_w     INT,
    pv2_power_w     INT,
    battery_soc_pct SMALLINT,
    battery_power_w INT,                   -- negative = charging
    grid_power_w    INT,                   -- negative = exporting
    load_power_w    INT,
    inverter_temp_c SMALLINT
);
SELECT create_hypertable('inverter_readings', 'time');
```

## Build Order

1. **Probe** (`SolarMonitor.Probe`) — console app, proves SolarmanV5 connectivity, raw register dump
2. **Poller** (`SolarMonitor.Poller`) — worker service, polls all three inverters, writes to TimescaleDB
3. **API** (`SolarMonitor.Api`) — REST + SignalR, exposes data to clients
4. **Web UI** (`src/web`) — React + TypeScript dashboard
5. **PWA packaging** — manifest + service worker on top of web UI
6. **Docker Compose** — full stack on Synology
7. **Cloudflare Tunnel** — external access via solar.thelinksys.co.za

## Configuration (never committed to source control)

Sensitive values are injected via environment variables or `appsettings.local.json`:
- Inverter IP addresses
- Dongle serial numbers
- Database password
