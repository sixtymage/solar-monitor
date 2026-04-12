using System.Net.Sockets;

namespace SolarMonitor.Probe;

/// <summary>
/// Minimal TCP client for the SolarmanV5 protocol.
///
/// The Solarman WiFi logger dongle listens on port 8899 and speaks a custom
/// framing protocol that wraps standard Modbus RTU over TCP. The outer frame
/// identifies the logger by serial number; the inner frame is a standard
/// Modbus RTU request/response, including CRC.
///
/// Frame layout (request):
///   [0]      0xA5                start byte
///   [1–2]    payload_len (LE)    byte count from offset 11 to end-2 (i.e. 14 + modbus_len)
///   [3–4]    control [0x10,0x45] request type
///   [5–6]    sequence (LE)       increments per request
///   [7–10]   serial (LE)         logger serial number (uint32)
///   [11]     0x02                frame type
///   [12]     0x00                sensor type
///   [13–16]  delivery time (LE)  zeroed — device doesn't care
///   [17–20]  power on time (LE)  zeroed
///   [21–24]  offset time (LE)    zeroed
///   [25 ..]  Modbus RTU frame    device_addr + fn_code + data + CRC16
///   [n-1]    checksum            sum(frame[1..n-2]) &amp; 0xFF
///   [n]      0x15                end byte
/// </summary>
public sealed class SolarmanV5Client : IDisposable
{
    private const int DefaultPort = 8899;
    private const byte FrameStart = 0xA5;
    private const byte FrameEnd = 0x15;

    // Control code bytes for a data request: 0x1045 in LE = [0x10, 0x45]
    private static readonly byte[] RequestControl = [0x10, 0x45];

    private readonly string _host;
    private readonly int _port;
    private readonly uint _serialNumber;
    private ushort _sequence;
    private TcpClient? _tcp;
    private NetworkStream? _stream;

    public SolarmanV5Client(string host, uint serialNumber, int port = DefaultPort)
    {
        _host = host;
        _serialNumber = serialNumber;
        _port = port;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _tcp = new TcpClient { ReceiveTimeout = 10_000, SendTimeout = 10_000 };
        await _tcp.ConnectAsync(_host, _port, ct);
        _stream = _tcp.GetStream();
        Console.WriteLine($"  TCP connected to {_host}:{_port}");
    }

    /// <summary>
    /// Reads <paramref name="count"/> holding registers starting at <paramref name="startRegister"/>
    /// from the Modbus device at <paramref name="deviceAddress"/> (typically 1 for Deye).
    /// Returns register values in the order they appear in the response (big-endian, converted to ushort).
    /// </summary>
    public async Task<ushort[]> ReadHoldingRegistersAsync(
        byte deviceAddress,
        ushort startRegister,
        ushort count,
        CancellationToken ct = default)
    {
        if (_stream is null)
            throw new InvalidOperationException("Not connected — call ConnectAsync first.");

        var modbusRequest = BuildModbusReadRequest(deviceAddress, startRegister, count);
        var frame = BuildV5Frame(modbusRequest);

        Console.WriteLine($"  → Sending request: device={deviceAddress}, reg={startRegister}, count={count}");
        Console.WriteLine($"    Frame ({frame.Length} bytes): {BytesToHex(frame)}");

        await _stream.WriteAsync(frame, ct);
        await _stream.FlushAsync(ct);

        var modbusResponse = await ReadV5ResponseAsync(ct);

        Console.WriteLine($"  ← Modbus response ({modbusResponse.Length} bytes): {BytesToHex(modbusResponse)}");

        return ParseModbusReadResponse(modbusResponse);
    }

    // ─── Frame construction ───────────────────────────────────────────────────

    private static byte[] BuildModbusReadRequest(byte deviceAddress, ushort startRegister, ushort count)
    {
        // Modbus RTU: read holding registers (function 0x03)
        // [addr][0x03][reg_hi][reg_lo][count_hi][count_lo][crc_lo][crc_hi]
        var frame = new byte[8];
        frame[0] = deviceAddress;
        frame[1] = 0x03;
        frame[2] = (byte)(startRegister >> 8);
        frame[3] = (byte)(startRegister & 0xFF);
        frame[4] = (byte)(count >> 8);
        frame[5] = (byte)(count & 0xFF);
        var crc = ComputeModbusCrc(frame.AsSpan(0, 6));
        frame[6] = (byte)(crc & 0xFF);   // CRC low byte first (Modbus convention)
        frame[7] = (byte)(crc >> 8);
        return frame;
    }

    private byte[] BuildV5Frame(byte[] modbusFrame)
    {
        // payload = frame_type(1) + sensor_type(1) + times(12) + modbus(n) = 14 + n
        int payloadLen = 14 + modbusFrame.Length;

        // total = start(1) + len(2) + control(2) + seq(2) + serial(4) + payload + checksum(1) + end(1)
        //       = 13 + payloadLen
        var frame = new byte[13 + payloadLen];
        int i = 0;

        frame[i++] = FrameStart;

        // Payload length, little-endian
        frame[i++] = (byte)(payloadLen & 0xFF);
        frame[i++] = (byte)(payloadLen >> 8);

        // Control code
        frame[i++] = RequestControl[0];
        frame[i++] = RequestControl[1];

        // Sequence number, little-endian
        ushort seq = ++_sequence;
        frame[i++] = (byte)(seq & 0xFF);
        frame[i++] = (byte)(seq >> 8);

        // Logger serial number, little-endian
        frame[i++] = (byte)(_serialNumber & 0xFF);
        frame[i++] = (byte)((_serialNumber >> 8) & 0xFF);
        frame[i++] = (byte)((_serialNumber >> 16) & 0xFF);
        frame[i++] = (byte)((_serialNumber >> 24) & 0xFF);

        frame[i++] = 0x02; // frame type
        frame[i++] = 0x00; // sensor type

        // Delivery time, power-on time, offset time — all zeroed (12 bytes)
        for (int t = 0; t < 12; t++) frame[i++] = 0x00;

        // Modbus RTU frame
        Array.Copy(modbusFrame, 0, frame, i, modbusFrame.Length);
        i += modbusFrame.Length;

        // Checksum: sum of all bytes between start and checksum (exclusive), mod 256
        byte checksum = 0;
        for (int c = 1; c < i; c++) checksum += frame[c];
        frame[i++] = checksum;

        frame[i] = FrameEnd;

        return frame;
    }

    // ─── Response parsing ─────────────────────────────────────────────────────

    private async Task<byte[]> ReadV5ResponseAsync(CancellationToken ct)
    {
        // Read the fixed outer header (11 bytes) to learn payload length
        var header = new byte[11];
        await ReadExactlyAsync(header, ct);

        if (header[0] != FrameStart)
            throw new InvalidDataException(
                $"Expected frame start 0xA5, got 0x{header[0]:X2}. Possible mis-framing or wrong device.");

        int payloadLen = header[1] | (header[2] << 8);

        // Read remaining bytes: payload + checksum + end byte
        var rest = new byte[payloadLen + 2];
        await ReadExactlyAsync(rest, ct);

        if (rest[payloadLen + 1] != FrameEnd)
            throw new InvalidDataException(
                $"Expected frame end 0x15, got 0x{rest[payloadLen + 1]:X2}.");

        // Validate checksum: sum(header[1..10] + rest[0..payloadLen-1]) & 0xFF
        byte expected = 0;
        for (int i = 1; i < header.Length; i++) expected += header[i];
        for (int i = 0; i < payloadLen; i++) expected += rest[i];

        if (rest[payloadLen] != expected)
        {
            Console.WriteLine(
                $"  Warning: checksum mismatch (got 0x{rest[payloadLen]:X2}, expected 0x{expected:X2})");
        }

        // Dump the full raw response frame for diagnostics
        var fullFrame = new byte[header.Length + rest.Length];
        Array.Copy(header, fullFrame, header.Length);
        Array.Copy(rest, 0, fullFrame, header.Length, rest.Length);
        Console.WriteLine($"    Full V5 response ({fullFrame.Length} bytes): {BytesToHex(fullFrame)}");
        Console.WriteLine($"    Control: {header[3]:X2} {header[4]:X2}  PayloadLen: {payloadLen}");

        // Modbus response starts after the 14-byte inner header:
        //   frame_type(1) + sensor_type(1) + delivery_time(4) + power_on_time(4) + offset_time(4) = 14
        const int innerHeaderLen = 14;
        int modbusLen = payloadLen - innerHeaderLen;
        var modbusResponse = new byte[modbusLen];
        Array.Copy(rest, innerHeaderLen, modbusResponse, 0, modbusLen);
        return modbusResponse;
    }

    private static ushort[] ParseModbusReadResponse(byte[] frame)
    {
        if (frame.Length < 3)
            throw new InvalidDataException($"Modbus response too short ({frame.Length} bytes).");

        // Check for Modbus exception response (high bit set on function code)
        if ((frame[1] & 0x80) != 0)
            throw new InvalidDataException($"Modbus exception — code 0x{frame[2]:X2}.");

        if (frame[1] != 0x03)
            throw new InvalidDataException($"Unexpected Modbus function code 0x{frame[1]:X2}, expected 0x03.");

        int byteCount = frame[2];
        var registers = new ushort[byteCount / 2];

        for (int i = 0; i < registers.Length; i++)
        {
            int offset = 3 + i * 2;
            // Modbus register values are big-endian
            registers[i] = (ushort)((frame[offset] << 8) | frame[offset + 1]);
        }

        return registers;
    }

    // ─── Utilities ────────────────────────────────────────────────────────────

    private async Task ReadExactlyAsync(byte[] buffer, CancellationToken ct)
    {
        int bytesRead = 0;
        while (bytesRead < buffer.Length)
        {
            int n = await _stream!.ReadAsync(buffer.AsMemory(bytesRead), ct);
            if (n == 0)
                throw new EndOfStreamException("Connection closed by remote host mid-frame.");
            bytesRead += n;
        }
    }

    /// <summary>
    /// Modbus CRC16 — polynomial 0xA001 (bit-reversed 0x8005), init 0xFFFF.
    /// </summary>
    private static ushort ComputeModbusCrc(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                if ((crc & 0x0001) != 0)
                    crc = (ushort)((crc >> 1) ^ 0xA001);
                else
                    crc >>= 1;
            }
        }
        return crc;
    }

    private static string BytesToHex(byte[] bytes) =>
        string.Join(' ', bytes.Select(b => $"{b:X2}"));

    public void Dispose()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
    }
}
