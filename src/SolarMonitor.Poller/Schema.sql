-- Applied by the poller at every start. Must stay idempotent.

CREATE EXTENSION IF NOT EXISTS timescaledb;

-- One row per physical inverter, keyed by the inverter's own serial (registers 3–7).
-- Host and logger serial are just how we reach it today: loggers get replaced.
CREATE TABLE IF NOT EXISTS inverter (
    id              smallint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    serial          text        NOT NULL UNIQUE,
    name            text        NOT NULL,
    host            text        NOT NULL,
    logger_serial   bigint      NOT NULL,
    rated_power_w   integer,
    device_raw      integer[],                      -- registers 0–58 at the last start
    first_seen      timestamptz NOT NULL DEFAULT now(),
    last_seen       timestamptz NOT NULL DEFAULT now()
);

-- One row per inverter per poll. `time` is the scheduled tick, shared by all inverters,
-- so whole-house totals are a GROUP BY time. Signs: grid + import; battery + discharge.
-- `raw` keeps registers raw_base.. as read, so anything can be re-decoded later.
CREATE TABLE IF NOT EXISTS reading (
    time                    timestamptz NOT NULL,
    inverter_id             smallint    NOT NULL REFERENCES inverter (id),
    running_state           smallint,
    pv1_v                   real,
    pv1_a                   real,
    pv1_w                   integer,
    pv2_v                   real,
    pv2_a                   real,
    pv2_w                   integer,
    grid_v                  real,
    grid_w                  integer,
    ac_hz                   real,
    inverter_w              integer,
    load_w                  integer,
    batt_v                  real,
    batt_a                  real,
    batt_w                  integer,
    batt_soc                smallint,
    batt_temp_c             real,
    dc_temp_c               real,
    ac_temp_c               real,
    pv_today_kwh            real,
    load_today_kwh          real,
    grid_buy_today_kwh      real,
    grid_sell_today_kwh     real,
    batt_chg_today_kwh      real,
    batt_dis_today_kwh      real,
    pv_total_kwh            double precision,
    load_total_kwh          double precision,
    grid_buy_total_kwh      double precision,
    grid_sell_total_kwh     double precision,
    batt_chg_total_kwh      double precision,
    batt_dis_total_kwh      double precision,
    raw_base                smallint    NOT NULL,
    raw                     integer[]   NOT NULL,
    UNIQUE (inverter_id, time)
);

SELECT create_hypertable('reading', by_range('time', INTERVAL '7 days'), if_not_exists => TRUE);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM timescaledb_information.hypertables
                   WHERE hypertable_name = 'reading' AND compression_enabled) THEN
        ALTER TABLE reading SET (timescaledb.compress,
                                 timescaledb.compress_segmentby = 'inverter_id',
                                 timescaledb.compress_orderby = 'time DESC');
    END IF;
END $$;

SELECT add_compression_policy('reading', INTERVAL '7 days', if_not_exists => TRUE);

-- Settings registers (200–280), stored only when they differ from the previous snapshot.
CREATE TABLE IF NOT EXISTS settings_snapshot (
    time        timestamptz NOT NULL,
    inverter_id smallint    NOT NULL REFERENCES inverter (id),
    raw_base    smallint    NOT NULL,
    raw         integer[]   NOT NULL,
    PRIMARY KEY (inverter_id, time)
);

-- Failed polls, to judge logger reliability. inverter_id is null if the inverter was never identified.
CREATE TABLE IF NOT EXISTS poll_error (
    time        timestamptz NOT NULL DEFAULT now(),
    host        text        NOT NULL,
    inverter_id smallint    REFERENCES inverter (id),
    message     text        NOT NULL
);

CREATE INDEX IF NOT EXISTS poll_error_time ON poll_error (time DESC);
