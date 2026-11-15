using System.Text;

namespace SolarMonitor.Core.Deye;

/// <summary>
/// Register blocks and decoding for the Deye SUN-8K-SG01LP1 (single-phase hybrid, same layout as
/// the SG02LP1). See docs/register-map.md for the evidence behind each register.
/// </summary>
public static class DeyeRegisters
{
    /// <summary>Device identity: serial (3–7) and rated power (16–17). Read once at start-up.</summary>
    public static readonly RegisterBlock Device = new(0, 59);

    /// <summary>Live values and energy counters, 59–199. Read every poll, stored raw with each reading.</summary>
    public static readonly RegisterBlock[] Live = [new(59, 60), new(119, 81)];

    /// <summary>Battery and time-of-use settings, 200–280. Read occasionally, stored when they change.</summary>
    public static readonly RegisterBlock Settings = new(200, 81);

    public const ushort LiveBase = 59;
    public const ushort LiveCount = 141;

    public static DeviceInfo DecodeDevice(RegisterSet r) => new(
        Serial: r.Ascii(3, 5),
        RatedPowerW: (int)(r.U32(16, 17) / 10));

    public static InverterReading DecodeLive(RegisterSet r) => new(
        RunningState: r.U16(59),

        Pv1V: r.U16(109) / 10.0,
        Pv1A: r.U16(110) / 10.0,
        Pv1W: r.U16(186),
        Pv2V: r.U16(111) / 10.0,
        Pv2A: r.U16(112) / 10.0,
        Pv2W: r.U16(187),

        GridV: r.U16(150) / 10.0,
        GridW: r.S16(169),            // + import, − export (energy balance, 15 Nov 2026)
        AcHz: r.U16(192) / 100.0,
        InverterW: r.S16(175),        // + inverter supplying AC, − inverter taking AC in
        LoadW: r.S16(178),

        BatteryV: r.U16(183) / 100.0,
        BatteryA: r.S16(191) / 100.0, // + discharge, − charge
        BatteryW: r.S16(190),         // + discharge, − charge; per inverter, so sum across inverters
        BatterySoc: r.U16(184),
        BatteryTempC: r.U16(182) == 0 ? null : Temp(r.U16(182)), // 0 = no BMS link on this inverter

        DcTempC: Temp(r.U16(90)),
        AcTempC: Temp(r.U16(91)),

        PvTodayKwh: r.U16(108) / 10.0,
        LoadTodayKwh: r.U16(84) / 10.0,
        GridBuyTodayKwh: r.U16(76) / 10.0,
        GridSellTodayKwh: r.U16(77) / 10.0,
        BatteryChargeTodayKwh: r.U16(70) / 10.0,
        BatteryDischargeTodayKwh: r.U16(71) / 10.0,

        PvTotalKwh: r.U32(96, 97) / 10.0,
        LoadTotalKwh: r.U32(85, 86) / 10.0,
        GridBuyTotalKwh: r.U32(78, 80) / 10.0,   // 79 is grid frequency, hence the gap
        GridSellTotalKwh: r.U32(81, 82) / 10.0,
        BatteryChargeTotalKwh: r.U32(72, 73) / 10.0,
        BatteryDischargeTotalKwh: r.U32(74, 75) / 10.0);

    /// <summary>Deye encodes temperatures as (°C + 100) × 10.</summary>
    private static double Temp(ushort raw) => Math.Round((raw - 1000) / 10.0, 1);
}

public readonly record struct RegisterBlock(ushort Start, ushort Count);

/// <summary>A sparse map of register number → raw value, built from one or more block reads.</summary>
public sealed class RegisterSet
{
    private readonly Dictionary<int, ushort> _values = [];

    public void Add(ushort start, ReadOnlySpan<ushort> values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            _values[start + i] = values[i];
        }
    }

    public ushort U16(int register) =>
        _values.TryGetValue(register, out var v)
            ? v
            : throw new KeyNotFoundException($"Register {register} was not read.");

    public short S16(int register) => unchecked((short)U16(register));

    /// <summary>Deye stores 32-bit values low word first.</summary>
    public uint U32(int low, int high) => U16(low) | ((uint)U16(high) << 16);

    public string Ascii(int start, int registers)
    {
        var sb = new StringBuilder(registers * 2);
        for (var r = start; r < start + registers; r++)
        {
            var v = U16(r);
            sb.Append((char)(v >> 8)).Append((char)(v & 0xFF));
        }

        return sb.ToString().Trim('\0', ' ');
    }

    /// <summary>The contiguous run start..start+count-1 as an int array, for storage.</summary>
    public int[] Slice(int start, int count)
    {
        var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = U16(start + i);
        }

        return result;
    }
}

public sealed record DeviceInfo(string Serial, int RatedPowerW);

public sealed record InverterReading(
    int RunningState,
    double Pv1V, double Pv1A, int Pv1W,
    double Pv2V, double Pv2A, int Pv2W,
    double GridV, int GridW, double AcHz,
    int InverterW, int LoadW,
    double BatteryV, double BatteryA, int BatteryW, int BatterySoc, double? BatteryTempC,
    double DcTempC, double AcTempC,
    double PvTodayKwh, double LoadTodayKwh, double GridBuyTodayKwh, double GridSellTodayKwh,
    double BatteryChargeTodayKwh, double BatteryDischargeTodayKwh,
    double PvTotalKwh, double LoadTotalKwh, double GridBuyTotalKwh, double GridSellTotalKwh,
    double BatteryChargeTotalKwh, double BatteryDischargeTotalKwh);
