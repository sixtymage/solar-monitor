using System.Text.Json;
using SolarMonitor.Core.Deye;
using SolarMonitor.Core.Solarman;

// ─── Usage ────────────────────────────────────────────────────────────────────
//   SolarMonitor.Probe <ip> <logger-serial>               decoded device + live reading, as JSON
//   SolarMonitor.Probe <ip> <logger-serial> <start> <n>   raw dump of n registers from start
//   add --trace to either form for wire-level hex dumps
//
// The logger serial is the LSW-3 web UI's "Device serial number", not the inverter's serial.
// Read-only: only Modbus function 0x03 is ever sent.
// ─────────────────────────────────────────────────────────────────────────────

var trace = args.Contains("--trace");
var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();

if (positional.Length is not (2 or 4) || !uint.TryParse(positional[1], out var serial))
{
    Console.Error.WriteLine("Usage: SolarMonitor.Probe <ip> <logger-serial> [<start> <count>] [--trace]");
    return 1;
}

var host = positional[0];
await using var client = new SolarmanV5Client(host, serial, trace: trace ? Console.Error.WriteLine : null);

try
{
    await client.ConnectAsync();

    if (positional.Length == 4)
    {
        var start = ushort.Parse(positional[2]);
        var count = ushort.Parse(positional[3]);
        var values = await client.ReadHoldingRegistersAsync(1, start, count);
        Console.WriteLine($"{"Reg",4}  {"Dec",6}  {"Hex",6}  {"Signed",7}");
        for (var i = 0; i < values.Length; i++)
        {
            Console.WriteLine($"{start + i,4}  {values[i],6}  0x{values[i]:X4}  {(short)values[i],7}");
        }

        return 0;
    }

    var regs = new RegisterSet();
    foreach (var block in DeyeRegisters.Live.Prepend(DeyeRegisters.Device))
    {
        regs.Add(block.Start, await client.ReadHoldingRegistersAsync(1, block.Start, block.Count));
    }

    var json = new JsonSerializerOptions { WriteIndented = true };
    Console.WriteLine(JsonSerializer.Serialize(DeyeRegisters.DecodeDevice(regs), json));
    Console.WriteLine(JsonSerializer.Serialize(DeyeRegisters.DecodeLive(regs), json));
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"{host}: {ex.GetType().Name}: {ex.Message}");
    return 1;
}
