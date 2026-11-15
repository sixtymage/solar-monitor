using Npgsql;
using SolarMonitor.Poller;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

builder.Services.Configure<PollerOptions>(builder.Configuration.GetSection(PollerOptions.Section));

// Entries without a host are ignored, so a half-filled .env doesn't crash the poller.
IReadOnlyList<InverterOptions> inverters = (builder.Configuration.GetSection(InverterOptions.Section)
        .Get<List<InverterOptions>>() ?? [])
    .Where(i => !string.IsNullOrWhiteSpace(i.Host) && i.LoggerSerial != 0)
    .ToList();
builder.Services.AddSingleton(inverters);

var connectionString = builder.Configuration.GetConnectionString("SolarMonitor")
    ?? throw new InvalidOperationException("ConnectionStrings:SolarMonitor is not set.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Database>();
builder.Services.AddHostedService<PollerService>();

await builder.Build().RunAsync();
