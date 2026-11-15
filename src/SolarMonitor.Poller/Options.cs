namespace SolarMonitor.Poller;

public sealed class PollerOptions
{
    public const string Section = "Poller";

    /// <summary>Seconds between polls. Ticks are aligned to the wall clock, so all inverters share timestamps.</summary>
    public int IntervalSeconds { get; init; } = 10;

    /// <summary>How often to re-read the settings registers.</summary>
    public int SettingsIntervalMinutes { get; init; } = 60;

    /// <summary>Timeout for each TCP operation against a logger.</summary>
    public int TimeoutSeconds { get; init; } = 10;

    /// <summary>Upper bound for the exponential back-off after consecutive failures.</summary>
    public int MaxBackoffSeconds { get; init; } = 300;
}

public sealed class InverterOptions
{
    public const string Section = "Inverters";

    public string? Name { get; init; }

    public string Host { get; init; } = "";

    public int Port { get; init; } = 8899;

    /// <summary>The logger's serial (LSW-3 web UI → Device serial number), not the inverter's.</summary>
    public uint LoggerSerial { get; init; }

    /// <summary>Optional. If set, a mismatch with the serial the inverter reports is logged as a warning.</summary>
    public string? InverterSerial { get; init; }
}
