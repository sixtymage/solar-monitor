using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("SolarMonitor")
    ?? throw new InvalidOperationException("ConnectionStrings:SolarMonitor is not set.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok("ok"));

// Latest reading per inverter (from the last 15 minutes) plus whole-house totals.
// Battery voltage and state of charge are bus-wide, so they're not summed; battery power is per
// inverter (each one's own DC flow into the shared bank), so it is.
app.MapGet("/api/live", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    const string sql = """
        SELECT DISTINCT ON (r.inverter_id)
               i.name, i.serial, r.time, r.running_state,
               r.pv1_w + r.pv2_w, r.pv1_v, r.grid_v, r.grid_w, r.load_w, r.inverter_w,
               r.batt_v, r.batt_w, r.batt_soc, r.batt_temp_c, r.dc_temp_c, r.ac_temp_c,
               r.pv_today_kwh, r.load_today_kwh, r.grid_buy_today_kwh, r.grid_sell_today_kwh,
               r.batt_chg_today_kwh, r.batt_dis_today_kwh
        FROM reading r JOIN inverter i ON i.id = r.inverter_id
        WHERE r.time > now() - interval '15 minutes'
        ORDER BY r.inverter_id, r.time DESC
        """;
    await using var cmd = db.CreateCommand(sql);
    await using var rd = await cmd.ExecuteReaderAsync(ct);

    var phases = new List<Phase>();
    while (await rd.ReadAsync(ct))
    {
        phases.Add(new Phase(
            Name: rd.GetString(0), Serial: rd.GetString(1),
            Time: rd.GetFieldValue<DateTimeOffset>(2), RunningState: rd.GetInt16(3),
            PvW: rd.GetInt32(4), Pv1V: R(rd.GetFloat(5)), GridV: R(rd.GetFloat(6)), GridW: rd.GetInt32(7),
            LoadW: rd.GetInt32(8), InverterW: rd.GetInt32(9),
            BatteryV: R(rd.GetFloat(10), 2), BatteryW: rd.GetInt32(11), BatterySoc: rd.GetInt16(12),
            BatteryTempC: rd.IsDBNull(13) ? null : R(rd.GetFloat(13)),
            DcTempC: R(rd.GetFloat(14)), AcTempC: R(rd.GetFloat(15)),
            Today: new Energy(R(rd.GetFloat(16)), R(rd.GetFloat(17)), R(rd.GetFloat(18)), R(rd.GetFloat(19)),
                R(rd.GetFloat(20)), R(rd.GetFloat(21)))));
    }

    if (phases.Count == 0)
    {
        return Results.Ok(new { phases, total = (object?)null });
    }

    phases.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
    var total = new Total(
        Time: phases.Min(p => p.Time),
        PvW: phases.Sum(p => p.PvW),
        LoadW: phases.Sum(p => p.LoadW),
        GridW: phases.Sum(p => p.GridW),
        BatteryW: phases.Sum(p => p.BatteryW),
        BatterySoc: phases.Max(p => p.BatterySoc),
        BatteryV: Math.Round(phases.Average(p => p.BatteryV), 2),
        BatteryTempC: phases.Select(p => p.BatteryTempC).FirstOrDefault(t => t is not null),
        Today: new Energy(
            Sum(phases, p => p.Today.Pv), Sum(phases, p => p.Today.Load),
            Sum(phases, p => p.Today.GridBuy), Sum(phases, p => p.Today.GridSell),
            Sum(phases, p => p.Today.BatteryCharge), Sum(phases, p => p.Today.BatteryDischarge)));

    return Results.Ok(new { phases, total });
});

// Whole-house series, columnar for uPlot: average per inverter per bucket, then summed across inverters.
app.MapGet("/api/history", async (NpgsqlDataSource db, CancellationToken ct, int hours = 24, int bucketSeconds = 120) =>
{
    hours = Math.Clamp(hours, 1, 24 * 7);
    bucketSeconds = Math.Clamp(bucketSeconds, 10, 3600);

    const string sql = """
        WITH per AS (
            SELECT time_bucket(make_interval(secs => @bucket), time) AS t, inverter_id,
                   avg(pv1_w + pv2_w) AS pv, avg(load_w) AS load, avg(grid_w) AS grid,
                   avg(batt_w) AS batt, avg(batt_soc) AS soc
            FROM reading
            WHERE time > now() - make_interval(hours => @hours)
            GROUP BY 1, 2)
        SELECT extract(epoch FROM t)::bigint, sum(pv)::float8, sum(load)::float8, sum(grid)::float8,
               sum(batt)::float8, max(soc)::float8
        FROM per
        GROUP BY t
        ORDER BY t
        """;
    await using var cmd = db.CreateCommand(sql);
    cmd.Parameters.AddWithValue("bucket", (double)bucketSeconds);
    cmd.Parameters.AddWithValue("hours", hours);
    await using var rd = await cmd.ExecuteReaderAsync(ct);

    var series = new History([], [], [], [], [], []);
    while (await rd.ReadAsync(ct))
    {
        series.T.Add(rd.GetInt64(0));
        series.Pv.Add(Math.Round(rd.GetDouble(1)));
        series.Load.Add(Math.Round(rd.GetDouble(2)));
        series.Grid.Add(Math.Round(rd.GetDouble(3)));
        series.Battery.Add(Math.Round(rd.GetDouble(4)));
        series.Soc.Add(Math.Round(rd.GetDouble(5), 1));
    }

    return Results.Ok(series);
});

app.Run();

static double R(double v, int digits = 1) => Math.Round(v, digits);

static double Sum(List<Phase> phases, Func<Phase, double> f) => Math.Round(phases.Sum(f), 1);

internal sealed record Energy(double Pv, double Load, double GridBuy, double GridSell, double BatteryCharge,
    double BatteryDischarge);

internal sealed record Phase(
    string Name, string Serial, DateTimeOffset Time, int RunningState,
    int PvW, double Pv1V, double GridV, int GridW, int LoadW, int InverterW,
    double BatteryV, int BatteryW, int BatterySoc, double? BatteryTempC,
    double DcTempC, double AcTempC, Energy Today);

internal sealed record Total(
    DateTimeOffset Time, int PvW, int LoadW, int GridW, int BatteryW, int BatterySoc, double BatteryV,
    double? BatteryTempC, Energy Today);

internal sealed record History(List<long> T, List<double> Pv, List<double> Load, List<double> Grid,
    List<double> Battery, List<double> Soc);
