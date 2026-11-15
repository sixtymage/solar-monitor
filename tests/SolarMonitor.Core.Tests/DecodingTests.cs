using SolarMonitor.Core.Deye;
using SolarMonitor.Core.Solarman;
using SolarMonitor.Poller;

namespace SolarMonitor.Core.Tests;

public class DecodingTests
{
    [Fact]
    public void ModbusCrc_matches_the_reference_vector()
    {
        // Read 10 holding registers from 0 on device 1: 01 03 00 00 00 0A, CRC C5 CD on the wire.
        var crc = SolarmanV5Client.ModbusCrc([0x01, 0x03, 0x00, 0x00, 0x00, 0x0A]);
        Assert.Equal(0xCDC5, crc);
    }

    [Fact]
    public void U32_is_low_word_first()
    {
        var r = new RegisterSet();
        r.Add(96, [0x1234, 0x0004]);
        Assert.Equal(0x0004_1234u, r.U32(96, 97));
    }

    [Fact]
    public void Ascii_reads_high_byte_first_and_trims_padding()
    {
        var r = new RegisterSet();
        r.Add(3, ['1' << 8 | '2', '3' << 8 | '4', '5' << 8 | '6', '7' << 8 | '8', '9' << 8 | '0']);
        Assert.Equal("1234567890", r.Ascii(3, 5));
    }

    [Fact]
    public void DecodeLive_applies_scales_and_sign_conventions()
    {
        var r = Live();
        Set(r, 169, -1500);   // grid exporting 1.5 kW
        Set(r, 190, -2035);   // battery charging
        Set(r, 191, -4002);   // −40.02 A
        Set(r, 183, 5087);    // 50.87 V
        Set(r, 184, 86);
        Set(r, 182, 0);       // no BMS link
        Set(r, 90, 1501);     // 50.1 °C
        Set(r, 78, 0x5C3B);   // grid bought total, low word…
        Set(r, 80, 0x0001);   // …high word (79 is frequency)
        Set(r, 79, 4995);

        var d = DeyeRegisters.DecodeLive(r);

        Assert.Equal(-1500, d.GridW);
        Assert.Equal(-2035, d.BatteryW);
        Assert.Equal(-40.02, d.BatteryA, 2);
        Assert.Equal(50.87, d.BatteryV, 2);
        Assert.Equal(86, d.BatterySoc);
        Assert.Null(d.BatteryTempC);
        Assert.Equal(50.1, d.DcTempC, 1);
        Assert.Equal((0x1_5C3B) / 10.0, d.GridBuyTotalKwh, 1);
    }

    [Fact]
    public void DecodeLive_needs_every_live_register()
    {
        var r = new RegisterSet();
        r.Add(59, new ushort[60]);   // second block missing
        Assert.Throws<KeyNotFoundException>(() => DeyeRegisters.DecodeLive(r));
    }

    [Theory]
    [InlineData("2026-11-15T12:00:03Z", 10, "2026-11-15T12:00:10Z")]
    [InlineData("2026-11-15T12:00:10Z", 10, "2026-11-15T12:00:20Z")]   // strictly after
    [InlineData("2026-11-15T12:00:59Z", 30, "2026-11-15T12:01:00Z")]
    public void NextTick_aligns_to_the_wall_clock(string after, int seconds, string expected)
    {
        var tick = PollerService.NextTick(DateTimeOffset.Parse(after), TimeSpan.FromSeconds(seconds));
        Assert.Equal(DateTimeOffset.Parse(expected), tick);
    }

    private static RegisterSet Live()
    {
        var r = new RegisterSet();
        foreach (var b in DeyeRegisters.Live)
        {
            r.Add(b.Start, new ushort[b.Count]);
        }

        Set(r, 90, 1000);
        Set(r, 91, 1000);
        return r;
    }

    private static void Set(RegisterSet r, ushort register, int value) =>
        r.Add(register, [unchecked((ushort)value)]);
}
