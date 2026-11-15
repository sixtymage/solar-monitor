# Solar Monitor

Local monitoring for a three-phase Deye hybrid installation (three SUN-8K-SG01LP1 single-phase
inverters, one per phase, sharing one battery bank). It reads each inverter through its Solarman
LSW-3 Wi-Fi logger on the LAN, stores everything in TimescaleDB and shows a small web page. It needs
no Solarman cloud and no internet.

**Read-only.** Only Modbus function 0x03 (read holding registers) is ever sent. The logger's port
8899 also accepts writes, with no authentication, so keep it that way unless writes are designed
deliberately.

```
3 × LSW-3 logger (TCP 8899, SolarmanV5)
        │  every 10 s, aligned to the wall clock
   poller (C#)  ──►  TimescaleDB  ◄──  web (ASP.NET Core + static page)  ──►  browser :8090
```

| Part | What | Where |
|---|---|---|
| `src/SolarMonitor.Core` | SolarmanV5 client; Deye register map and decoder | |
| `src/SolarMonitor.Poller` | Worker: polls each inverter, writes readings, keeps settings snapshots | image `solar-monitor-poller` |
| `src/SolarMonitor.Api` | `/api/live`, `/api/history`, and the page in `wwwroot` | image `solar-monitor-web` |
| `src/SolarMonitor.Probe` | Console tool: decoded reading, or a raw register dump | |
| `docker/` | Compose for the NAS, plus a build override for local development | |
| `docs/` | `architecture.md`, `register-map.md` (the evidence for every register) | |

## What gets stored

- **`reading`**: one row per inverter per tick. Decoded columns (PV, grid, load, battery, temperatures,
  daily and lifetime energy counters) **plus the raw registers 59–199**, so a decoding mistake can
  be fixed retrospectively. It's a hypertable with 7-day chunks, compressed after 7 days.
- **`settings_snapshot`**: registers 200–280 (battery limits, time-of-use schedule), read hourly
  and stored only when something changed. That gives a history of who changed what, and when.
- **`inverter`**: keyed by the **inverter's** serial (registers 3–7), not by IP or logger. Loggers
  get replaced.
- **`poll_error`**: every failed poll, to judge logger reliability.

Conventions: grid **+ import / − export**; battery **+ discharge / − charge**. Battery power is
per inverter (each one's DC flow into the shared bank), so **sum it** across inverters. Battery
voltage and state of charge are bus-wide, so **don't**.

At 10 s, three inverters write about 26 k rows a day: roughly 20 MB a day before compression.

## Run it on the NAS

Images are built by GitHub Actions on every push to `main` and published to GHCR
(`ghcr.io/sixtymage/solar-monitor-poller`, `…-web`, tags `latest` and `sha-<commit>`).

1. On the NAS, make a folder, e.g. `/volume1/docker/solar-monitor`. Put `docker/docker-compose.yml`
   there, and a `.env` made from `.env.example`.
2. From an administrator SSH session:

   ```bash
   cd /volume1/docker/solar-monitor
   sudo docker-compose pull
   sudo docker-compose up -d
   sudo docker-compose logs -f poller      # expect "inverter … is id …", then quiet
   ```

   DSM 7.1's Docker package doesn't always put `docker-compose` on the `PATH`. Try
   `/usr/local/bin/docker-compose`, or `/var/packages/Docker/target/usr/bin/docker-compose`.
3. Open `http://<nas>:8090/`.

To update: `sudo docker-compose pull && sudo docker-compose up -d`. To pin a build, set
`TAG=sha-<commit>` in `.env`.

If `pull` says *unauthorized*, the GHCR packages are private: GitHub → Packages →
`solar-monitor-poller` / `-web` → Package settings → Change visibility → Public.

## Develop locally

```bash
# Database only (TimescaleDB on localhost:5433):
docker compose --env-file .env -f docker/docker-compose.yml up -d timescaledb

# Whole stack, built from source:
docker compose --env-file .env -f docker/docker-compose.yml -f docker/docker-compose.build.yml up -d --build

# One-off reads:
dotnet run --project src/SolarMonitor.Probe -- <logger-ip> <logger-serial>           # decoded JSON
dotnet run --project src/SolarMonitor.Probe -- <logger-ip> <logger-serial> 200 81    # raw registers

dotnet test
```

The poller and API take their configuration from the usual .NET sources. Compose maps `.env` onto
`ConnectionStrings__SolarMonitor` and `Inverters__<n>__Host`, `__LoggerSerial`, `__Name` and
`__InverterSerial`.

> **TLS-scanning antivirus** (e.g. Norton Web/Mail Shield) breaks `dotnet restore` *inside*
> `docker build`. The container doesn't trust the antivirus's root CA. Either turn the scanning
> off while building, or run the poller and API on the host with `dotnet run` against the
> containerised database. CI and the NAS aren't affected.

## Not yet

No authentication (LAN only, by design for v1); no alerting; no per-inverter charts; no retention
policy (compression makes years affordable). See `docs/architecture.md` for where it could go.
