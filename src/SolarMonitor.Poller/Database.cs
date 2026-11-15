using Npgsql;
using SolarMonitor.Core.Deye;

namespace SolarMonitor.Poller;

public sealed class Database(NpgsqlDataSource dataSource, ILogger<Database> logger)
{
    /// <summary>Applies Schema.sql, retrying until the database is reachable.</summary>
    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        await using var stream = typeof(Database).Assembly.GetManifestResourceStream("SolarMonitor.Poller.Schema.sql")
            ?? throw new InvalidOperationException("Schema.sql is not embedded.");
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(ct);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var cmd = dataSource.CreateCommand(sql);
                await cmd.ExecuteNonQueryAsync(ct);
                logger.LogInformation("Schema ready");
                return;
            }
            catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException && attempt < 30)
            {
                logger.LogWarning("Database not ready (attempt {Attempt}): {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(2 * attempt, 30)), ct);
            }
        }
    }

    public async Task<short> UpsertInverterAsync(InverterOptions cfg, DeviceInfo device, int[] deviceRaw,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO inverter (serial, name, host, logger_serial, rated_power_w, device_raw)
            VALUES ($1, $2, $3, $4, $5, $6)
            ON CONFLICT (serial) DO UPDATE SET
                name = EXCLUDED.name, host = EXCLUDED.host, logger_serial = EXCLUDED.logger_serial,
                rated_power_w = EXCLUDED.rated_power_w, device_raw = EXCLUDED.device_raw, last_seen = now()
            RETURNING id
            """;
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.Add(new() { Value = device.Serial });
        cmd.Parameters.Add(new() { Value = string.IsNullOrWhiteSpace(cfg.Name) ? $"Inverter {device.Serial}" : cfg.Name });
        cmd.Parameters.Add(new() { Value = cfg.Host });
        cmd.Parameters.Add(new() { Value = (long)cfg.LoggerSerial });
        cmd.Parameters.Add(new() { Value = device.RatedPowerW });
        cmd.Parameters.Add(new() { Value = deviceRaw });
        return (short)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task InsertReadingAsync(short inverterId, DateTimeOffset time, InverterReading r, int[] raw,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO reading (
                time, inverter_id, running_state,
                pv1_v, pv1_a, pv1_w, pv2_v, pv2_a, pv2_w,
                grid_v, grid_w, ac_hz, inverter_w, load_w,
                batt_v, batt_a, batt_w, batt_soc, batt_temp_c, dc_temp_c, ac_temp_c,
                pv_today_kwh, load_today_kwh, grid_buy_today_kwh, grid_sell_today_kwh,
                batt_chg_today_kwh, batt_dis_today_kwh,
                pv_total_kwh, load_total_kwh, grid_buy_total_kwh, grid_sell_total_kwh,
                batt_chg_total_kwh, batt_dis_total_kwh,
                raw_base, raw)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20,
                    $21, $22, $23, $24, $25, $26, $27, $28, $29, $30, $31, $32, $33, $34, $35)
            ON CONFLICT (inverter_id, time) DO NOTHING
            """;
        await using var cmd = dataSource.CreateCommand(sql);
        object?[] values =
        [
            time.UtcDateTime, inverterId, (short)r.RunningState,
            (float)r.Pv1V, (float)r.Pv1A, r.Pv1W, (float)r.Pv2V, (float)r.Pv2A, r.Pv2W,
            (float)r.GridV, r.GridW, (float)r.AcHz, r.InverterW, r.LoadW,
            (float)r.BatteryV, (float)r.BatteryA, r.BatteryW, (short)r.BatterySoc,
            r.BatteryTempC is { } bt ? (float)bt : null, (float)r.DcTempC, (float)r.AcTempC,
            (float)r.PvTodayKwh, (float)r.LoadTodayKwh, (float)r.GridBuyTodayKwh, (float)r.GridSellTodayKwh,
            (float)r.BatteryChargeTodayKwh, (float)r.BatteryDischargeTodayKwh,
            r.PvTotalKwh, r.LoadTotalKwh, r.GridBuyTotalKwh, r.GridSellTotalKwh,
            r.BatteryChargeTotalKwh, r.BatteryDischargeTotalKwh,
            (short)DeyeRegisters.LiveBase, raw,
        ];
        foreach (var v in values)
        {
            cmd.Parameters.Add(new() { Value = v ?? DBNull.Value });
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <returns>True if the settings differed from the last snapshot and were stored.</returns>
    public async Task<bool> SaveSettingsIfChangedAsync(short inverterId, DateTimeOffset time, int[] raw,
        CancellationToken ct)
    {
        const string sql = """
            INSERT INTO settings_snapshot (time, inverter_id, raw_base, raw)
            SELECT $1, $2, $3, $4
            WHERE $4 IS DISTINCT FROM (SELECT raw FROM settings_snapshot
                                       WHERE inverter_id = $2 ORDER BY time DESC LIMIT 1)
            """;
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.Add(new() { Value = time.UtcDateTime });
        cmd.Parameters.Add(new() { Value = inverterId });
        cmd.Parameters.Add(new() { Value = (short)DeyeRegisters.Settings.Start });
        cmd.Parameters.Add(new() { Value = raw });
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>Best effort: a failure to record an error must not take the poll loop down.</summary>
    public async Task TryInsertErrorAsync(string host, short? inverterId, string message, CancellationToken ct)
    {
        try
        {
            await using var cmd = dataSource.CreateCommand(
                "INSERT INTO poll_error (host, inverter_id, message) VALUES ($1, $2, $3)");
            cmd.Parameters.Add(new() { Value = host });
            cmd.Parameters.Add(new() { Value = inverterId is { } id ? id : DBNull.Value });
            cmd.Parameters.Add(new() { Value = message });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug("Could not record poll error: {Message}", ex.Message);
        }
    }
}
