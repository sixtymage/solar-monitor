using System.Net.Sockets;

namespace SolarMonitor.Core.Solarman;

/// <summary>
/// Minimal client for the SolarmanV5 protocol spoken by Solarman LSW-3 Wi-Fi loggers on TCP 8899.
/// Wraps Modbus RTU in an outer frame addressed by the logger's serial number. Read-only: only
/// function 0x03 (read holding registers) is implemented, deliberately.
///
/// Request frame:
///   [0]      0xA5                start
///   [1–2]    payload length (LE) = 15 + modbus length
///   [3–4]    control 0x10 0x45   request
///   [5–6]    sequence (LE)
///   [7–10]   logger serial (LE, uint32)
///   [11]     0x02                frame type
///   [12–13]  sensor type         0x0000
///   [14–25]  three uint32 times  zeroed
///   [26..]   Modbus RTU frame incl. CRC
///   [n-1]    checksum            sum(frame[1..n-2]) &amp; 0xFF
///   [n]      0x15                end
///
/// Response frame: same outer header with control 0x10 0x15; the payload is a 14-byte inner
/// header (frame type, status, three uint32 timestamps) followed by the Modbus RTU response.
/// </summary>
public sealed class SolarmanV5Client : IAsyncDisposable, IDisposable
{
    public const int DefaultPort = 8899;

    private const byte FrameStart = 0xA5;
    private const byte FrameEnd = 0x15;
    private const byte ResponseControlLo = 0x10;
    private const byte ResponseControlHi = 0x15;
    private const int OuterHeaderLength = 11;
    private const int ResponseInnerHeaderLength = 14;

    private readonly string _host;
    private readonly int _port;
    private readonly uint _loggerSerial;
    private readonly TimeSpan _timeout;
    private readonly Action<string>? _trace;
    private ushort _sequence = (ushort)Random.Shared.Next(1, 0xFFFF);
    private TcpClient? _tcp;
    private NetworkStream? _stream;

    /// <param name="trace">Optional sink for wire-level diagnostics (hex dumps). Null in production.</param>
    public SolarmanV5Client(string host, uint loggerSerial, int port = DefaultPort, TimeSpan? timeout = null,
        Action<string>? trace = null)
    {
        _host = host;
        _loggerSerial = loggerSerial;
        _port = port;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
        _trace = trace;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        using var cts = Linked(ct);
        _tcp = new TcpClient { NoDelay = true };
        await _tcp.ConnectAsync(_host, _port, cts.Token);
        _stream = _tcp.GetStream();
        _trace?.Invoke($"TCP connected to {_host}:{_port}");
    }

    /// <summary>
    /// Reads <paramref name="count"/> holding registers from <paramref name="startRegister"/>.
    /// Retries when the logger reports its RS485 bus busy (it shares the bus with its cloud upload).
    /// </summary>
    public async Task<ushort[]> ReadHoldingRegistersAsync(byte deviceAddress, ushort startRegister, ushort count,
        CancellationToken ct = default)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("Not connected; call ConnectAsync first.");
        }

        if (count is 0 or > 125)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "Modbus allows 1–125 registers per read.");
        }

        const int maxAttempts = 5;
        var retryDelay = TimeSpan.FromMilliseconds(1500);

        for (var attempt = 1; ; attempt++)
        {
            var request = BuildV5Frame(BuildModbusReadRequest(deviceAddress, startRegister, count), out var seq);
            _trace?.Invoke($"→ reg {startRegister}+{count} attempt {attempt}: {Hex(request)}");

            using var cts = Linked(ct);
            await _stream.WriteAsync(request, cts.Token);
            var modbus = await ReadResponseAsync(seq, cts.Token);
            _trace?.Invoke($"← modbus ({modbus.Length} bytes): {Hex(modbus)}");

            // A bare 2-byte payload (0x05 0x00) instead of a Modbus frame: the bus is busy or the
            // inverter didn't answer. Back off and retry, like pysolarmanv5.
            if (modbus.Length < 5)
            {
                if (attempt < maxAttempts)
                {
                    await Task.Delay(retryDelay, ct);
                    continue;
                }

                throw new SolarmanException(
                    $"No Modbus response after {maxAttempts} attempts (payload {Hex(modbus)}); logger bus busy?");
            }

            return ParseModbusReadResponse(modbus, deviceAddress, count);
        }
    }

    // ─── Frame construction ───────────────────────────────────────────────────

    private static byte[] BuildModbusReadRequest(byte deviceAddress, ushort startRegister, ushort count)
    {
        var frame = new byte[8];
        frame[0] = deviceAddress;
        frame[1] = 0x03;
        frame[2] = (byte)(startRegister >> 8);
        frame[3] = (byte)startRegister;
        frame[4] = (byte)(count >> 8);
        frame[5] = (byte)count;
        var crc = ModbusCrc(frame.AsSpan(0, 6));
        frame[6] = (byte)crc;          // CRC is little-endian on the wire
        frame[7] = (byte)(crc >> 8);
        return frame;
    }

    private byte[] BuildV5Frame(byte[] modbusFrame, out ushort seq)
    {
        var payloadLength = 15 + modbusFrame.Length;
        var frame = new byte[13 + payloadLength];
        var i = 0;

        frame[i++] = FrameStart;
        frame[i++] = (byte)payloadLength;
        frame[i++] = (byte)(payloadLength >> 8);
        frame[i++] = 0x10;
        frame[i++] = 0x45;

        seq = unchecked(++_sequence);
        frame[i++] = (byte)seq;
        frame[i++] = (byte)(seq >> 8);

        frame[i++] = (byte)_loggerSerial;
        frame[i++] = (byte)(_loggerSerial >> 8);
        frame[i++] = (byte)(_loggerSerial >> 16);
        frame[i++] = (byte)(_loggerSerial >> 24);

        // 15-byte payload header, all zero after the frame type. (April's probe found that
        // dropping a byte here makes the logger answer 0x05 0x00: the sensor type is 2 bytes.)
        frame[i++] = 0x02;  // frame type
        i += 2;             // sensor type
        i += 12;            // total working, power-on and offset times

        modbusFrame.CopyTo(frame, i);
        i += modbusFrame.Length;

        byte checksum = 0;
        for (var c = 1; c < i; c++)
        {
            checksum += frame[c];
        }

        frame[i++] = checksum;
        frame[i] = FrameEnd;
        return frame;
    }

    // ─── Response parsing ─────────────────────────────────────────────────────

    /// <summary>
    /// Reads frames until one is a data response to <paramref name="seq"/>. Frames with another
    /// control code or a stale sequence number (e.g. the answer to an earlier, timed-out request)
    /// are skipped.
    /// </summary>
    private async Task<byte[]> ReadResponseAsync(ushort seq, CancellationToken ct)
    {
        for (var skipped = 0; skipped < 5; skipped++)
        {
            var header = new byte[OuterHeaderLength];
            await _stream!.ReadExactlyAsync(header, ct);

            if (header[0] != FrameStart)
            {
                throw new SolarmanException($"Bad frame start 0x{header[0]:X2}.");
            }

            var payloadLength = header[1] | (header[2] << 8);
            var rest = new byte[payloadLength + 2];
            await _stream.ReadExactlyAsync(rest, ct);

            if (rest[^1] != FrameEnd)
            {
                throw new SolarmanException($"Bad frame end 0x{rest[^1]:X2}.");
            }

            byte checksum = 0;
            for (var i = 1; i < OuterHeaderLength; i++)
            {
                checksum += header[i];
            }

            for (var i = 0; i < payloadLength; i++)
            {
                checksum += rest[i];
            }

            if (checksum != rest[payloadLength])
            {
                throw new SolarmanException(
                    $"V5 checksum mismatch: got 0x{rest[payloadLength]:X2}, expected 0x{checksum:X2}.");
            }

            var isDataResponse = header[3] == ResponseControlLo && header[4] == ResponseControlHi;
            var sameSequence = header[5] == (byte)seq;   // loggers echo only the low byte reliably
            if (!isDataResponse || !sameSequence)
            {
                _trace?.Invoke($"skipping frame control={header[4]:X2}{header[3]:X2} seq={header[5]:X2}");
                continue;
            }

            return payloadLength <= ResponseInnerHeaderLength
                ? []
                : rest.AsSpan(ResponseInnerHeaderLength, payloadLength - ResponseInnerHeaderLength).ToArray();
        }

        throw new SolarmanException("No matching response frame.");
    }

    private static ushort[] ParseModbusReadResponse(byte[] frame, byte deviceAddress, ushort count)
    {
        if ((frame[1] & 0x80) != 0)
        {
            throw new SolarmanException($"Modbus exception 0x{frame[2]:X2}.");
        }

        if (frame[0] != deviceAddress || frame[1] != 0x03)
        {
            throw new SolarmanException($"Unexpected Modbus header {frame[0]:X2} {frame[1]:X2}.");
        }

        int byteCount = frame[2];
        if (byteCount != count * 2)
        {
            throw new SolarmanException($"Asked for {count} registers, got {byteCount / 2}.");
        }

        // Some LSW-3 firmware appends a second CRC; only the first 3 + n + 2 bytes are the frame.
        var length = 3 + byteCount + 2;
        if (frame.Length < length)
        {
            throw new SolarmanException($"Truncated Modbus frame ({frame.Length} of {length} bytes).");
        }

        var crc = ModbusCrc(frame.AsSpan(0, length - 2));
        if (frame[length - 2] != (byte)crc || frame[length - 1] != (byte)(crc >> 8))
        {
            throw new SolarmanException("Modbus CRC mismatch.");
        }

        var registers = new ushort[count];
        for (var r = 0; r < count; r++)
        {
            registers[r] = (ushort)((frame[3 + (r * 2)] << 8) | frame[4 + (r * 2)]);
        }

        return registers;
    }

    // ─── Utilities ────────────────────────────────────────────────────────────

    /// <summary>Async socket reads ignore TcpClient.ReceiveTimeout, so every operation gets a token deadline.</summary>
    private CancellationTokenSource Linked(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        return cts;
    }

    /// <summary>Modbus CRC-16: polynomial 0xA001 (reflected 0x8005), initial value 0xFFFF.</summary>
    internal static ushort ModbusCrc(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
        }

        return crc;
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    public void Dispose()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class SolarmanException(string message) : Exception(message);
