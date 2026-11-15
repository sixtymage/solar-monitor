using Microsoft.Extensions.Options;
using SolarMonitor.Core.Deye;
using SolarMonitor.Core.Solarman;

namespace SolarMonitor.Poller;

/// <summary>
/// Polls every configured inverter on a shared wall-clock schedule. Each inverter has its own loop,
/// so a dead logger only delays itself. Each poll opens a fresh TCP connection: the LSW-3 drops idle
/// sockets, and reconnecting every 10 s costs nothing.
/// </summary>
public sealed class PollerService(
    Database db,
    IOptions<PollerOptions> options,
    IReadOnlyList<InverterOptions> inverters,
    TimeProvider clock,
    ILogger<PollerService> logger) : BackgroundService
{
    private const byte DeviceAddress = 1;

    private readonly PollerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (inverters.Count == 0)
        {
            logger.LogError("No inverters configured (Inverters:0:Host, Inverters:0:LoggerSerial, ...). Nothing to do");
            return;
        }

        await db.EnsureSchemaAsync(stoppingToken);
        await Task.WhenAll(inverters.Select(i => RunAsync(i, stoppingToken)));
    }

    private async Task RunAsync(InverterOptions cfg, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(_options.IntervalSeconds);
        var settingsInterval = TimeSpan.FromMinutes(_options.SettingsIntervalMinutes);
        var inverterId = await IdentifyAsync(cfg, ct);
        var nextSettingsRead = DateTimeOffset.MinValue;
        var failures = 0;

        while (!ct.IsCancellationRequested)
        {
            var tick = NextTick(clock.GetUtcNow() + Backoff(failures), interval);
            var wait = tick - clock.GetUtcNow();
            await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, clock, ct);

            try
            {
                await using var client = NewClient(cfg);
                await client.ConnectAsync(ct);

                var regs = new RegisterSet();
                foreach (var block in DeyeRegisters.Live)
                {
                    regs.Add(block.Start, await client.ReadHoldingRegistersAsync(DeviceAddress, block.Start, block.Count, ct));
                }

                int[]? settings = null;
                if (tick >= nextSettingsRead)
                {
                    var s = DeyeRegisters.Settings;
                    settings = Array.ConvertAll(await client.ReadHoldingRegistersAsync(DeviceAddress, s.Start, s.Count, ct), v => (int)v);
                }

                var reading = DeyeRegisters.DecodeLive(regs);
                await db.InsertReadingAsync(inverterId, tick, reading, regs.Slice(DeyeRegisters.LiveBase, DeyeRegisters.LiveCount), ct);

                if (settings is not null)
                {
                    if (await db.SaveSettingsIfChangedAsync(inverterId, tick, settings, ct))
                    {
                        logger.LogInformation("{Host}: settings changed, snapshot stored", cfg.Host);
                    }

                    nextSettingsRead = tick + settingsInterval;
                }

                if (failures > 0)
                {
                    logger.LogInformation("{Host}: recovered after {Failures} failed polls", cfg.Host, failures);
                }

                failures = 0;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failures++;
                var message = Describe(ex);
                logger.LogWarning("{Host}: poll failed ({Failures} in a row): {Message}", cfg.Host, failures, message);
                await db.TryInsertErrorAsync(cfg.Host, inverterId, message, ct);
            }
        }
    }

    /// <summary>Reads the device block and registers the inverter, retrying with back-off until it answers.</summary>
    private async Task<short> IdentifyAsync(InverterOptions cfg, CancellationToken ct)
    {
        for (var failures = 0; ; failures++)
        {
            try
            {
                await using var client = NewClient(cfg);
                await client.ConnectAsync(ct);
                var regs = new RegisterSet();
                var block = DeyeRegisters.Device;
                regs.Add(block.Start, await client.ReadHoldingRegistersAsync(DeviceAddress, block.Start, block.Count, ct));

                var device = DeyeRegisters.DecodeDevice(regs);
                if (!string.IsNullOrWhiteSpace(cfg.InverterSerial) && cfg.InverterSerial != device.Serial)
                {
                    logger.LogWarning("{Host}: configured inverter serial {Configured} but the inverter reports {Actual}; using {Actual}",
                        cfg.Host, cfg.InverterSerial, device.Serial, device.Serial);
                }

                var id = await db.UpsertInverterAsync(cfg, device, regs.Slice(block.Start, block.Count), ct);
                logger.LogInformation("{Host}: inverter {Serial} ({RatedPower} W) is id {Id}",
                    cfg.Host, device.Serial, device.RatedPowerW, id);
                return id;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var message = Describe(ex);
                logger.LogWarning("{Host}: could not identify inverter: {Message}", cfg.Host, message);
                await db.TryInsertErrorAsync(cfg.Host, null, message, ct);
                await Task.Delay(Backoff(failures + 1), clock, ct);
            }
        }
    }

    private SolarmanV5Client NewClient(InverterOptions cfg) =>
        new(cfg.Host, cfg.LoggerSerial, cfg.Port, TimeSpan.FromSeconds(_options.TimeoutSeconds));

    /// <summary>0 after a success; then interval × 2^(n−1), capped.</summary>
    private TimeSpan Backoff(int failures)
    {
        if (failures == 0)
        {
            return TimeSpan.Zero;
        }

        var seconds = _options.IntervalSeconds * Math.Pow(2, Math.Min(failures - 1, 16));
        return TimeSpan.FromSeconds(Math.Min(seconds, _options.MaxBackoffSeconds));
    }

    /// <summary>The first multiple of <paramref name="interval"/> (since the epoch) strictly after <paramref name="after"/>.</summary>
    internal static DateTimeOffset NextTick(DateTimeOffset after, TimeSpan interval)
    {
        var ticks = after.UtcTicks;
        var next = ((ticks / interval.Ticks) + 1) * interval.Ticks;
        return new DateTimeOffset(next, TimeSpan.Zero);
    }

    private static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException => "timed out",
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };
}
