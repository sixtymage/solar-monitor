using SolarMonitor.Probe;

// ─── Usage ────────────────────────────────────────────────────────────────────
// SolarMonitor.Probe <ip> <serial>
//
// <ip>     IP address of the Solarman WiFi dongle (check DHCP table on router)
// <serial> Logger serial number — 10-digit integer printed on the dongle sticker
//          and visible in the Solarman app under device details
//
// Example:
//   dotnet run --project src/SolarMonitor.Probe -- 192.168.1.100 2372034567
//
// ─── What this does ───────────────────────────────────────────────────────────
// Connects to the dongle's local port 8899 and issues Modbus read requests for
// two register ranges commonly used on Deye hybrid inverters. Prints raw values
// so you can cross-reference against the Solarman app to build your register map.
//
// Register maps vary slightly by firmware version. The ranges below are a good
// starting point based on community documentation for Deye SUN-xK-SG04LP3 and
// similar models. Once you confirm which registers match which readings in the
// app, update docs/register-map.md.
// ─────────────────────────────────────────────────────────────────────────────

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: SolarMonitor.Probe <ip> <serial>");
    Console.Error.WriteLine("  <ip>     Dongle IP address (e.g. 192.168.1.100)");
    Console.Error.WriteLine("  <serial> Dongle serial number (e.g. 2372034567)");
    return 1;
}

if (!uint.TryParse(args[1], out var serial))
{
    Console.Error.WriteLine("Serial number must be a positive integer.");
    return 1;
}

var host = args[0];

Console.WriteLine("=================================================");
Console.WriteLine("  Solar Monitor -- Connectivity Probe");
Console.WriteLine("=================================================");
Console.WriteLine($"  Host:   {host}:8899");
Console.WriteLine($"  Serial: {serial}");
Console.WriteLine();

using var client = new SolarmanV5Client(host, serial);

try
{
    await client.ConnectAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Connection failed: {ex.Message}");
    Console.Error.WriteLine("Check the IP address and that port 8899 is reachable on your LAN.");
    return 1;
}

Console.WriteLine();

// ─── Read battery and system state area (registers 100-139) ──────────────────
// Commonly documented registers in this range:
//   103 -- Battery SOC (%)
//   106 -- Battery voltage (x 0.1 V)
//   107 -- Battery current (signed, x 0.01 A)
//   168 -- Total load power (W)
await ReadAndPrint(client, "Battery / system state area", startRegister: 100, count: 40);

Console.WriteLine();

// ─── Read PV and grid area (registers 180-219) ───────────────────────────────
// Commonly documented registers in this range:
//   186 -- PV1 power (W)
//   187 -- PV2 power (W)
//   169-173 -- Grid power per phase and total
await ReadAndPrint(client, "PV / grid area", startRegister: 180, count: 40);

Console.WriteLine();
Console.WriteLine("Done. Cross-reference these values against the Solarman app");
Console.WriteLine("to confirm your register map. See docs/register-map.md.");

return 0;

// ─────────────────────────────────────────────────────────────────────────────

static async Task ReadAndPrint(
    SolarmanV5Client client,
    string label,
    ushort startRegister,
    ushort count)
{
    Console.WriteLine($"-- {label} (registers {startRegister}-{startRegister + count - 1}) --");

    ushort[] registers;
    try
    {
        registers = await client.ReadHoldingRegistersAsync(
            deviceAddress: 1,
            startRegister: startRegister,
            count: count);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Read failed: {ex.Message}");
        return;
    }

    Console.WriteLine($"  {"Reg",4}  {"Dec",6}  {"Hex",6}  {"Signed",8}");
    Console.WriteLine($"  {"---",4}  {"------",6}  {"------",6}  {"------",8}");

    for (int i = 0; i < registers.Length; i++)
    {
        ushort raw = registers[i];
        short signed = (short)raw;
        Console.WriteLine($"  {startRegister + i,4}  {raw,6}  0x{raw:X4}  {signed,8}");
    }
}
