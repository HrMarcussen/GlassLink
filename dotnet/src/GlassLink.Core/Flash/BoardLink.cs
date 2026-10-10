using System.IO.Ports;
using System.Runtime.Versioning;

namespace GlassLink.Core.Flash;

/// <summary>The flasher's line to a board: bytes both ways, the baud rate, and the DTR/RTS lines that the NANO's
/// auto-download circuit turns into reset (EN) and boot-mode (GPIO35) signals. A fake chip implements it in the tests.</summary>
public interface IBoardLink : IDisposable
{
    int BaudRate { get; set; }

    void Write(ReadOnlySpan<byte> data);

    /// <summary>What arrived within the timeout; empty if nothing did.</summary>
    byte[] Read(int timeoutMs);

    /// <summary>DTR asserted (true) pulls the NANO's GPIO35 (boot mode) low, unless RTS is asserted too.</summary>
    void SetDtr(bool on);

    /// <summary>RTS asserted (true) pulls the NANO's EN (reset) low, unless DTR is asserted too.</summary>
    void SetRts(bool on);

    void DiscardInput();
}

/// <summary>A COM port of a NANO (only one <see cref="BoardPorts"/> lists).</summary>
[SupportedOSPlatform("windows")]
public sealed class SerialBoardLink : IBoardLink
{
    private readonly SerialPort _port;
    private readonly byte[] _buffer = new byte[16 * 1024];

    public SerialBoardLink(string port)
    {
        if (!BoardPorts.IsBoard(port))
        {
            throw new InvalidOperationException($"{port} is not an ESP32-P4-NANO's serial port");      // never another serial device
        }

        // both lines released on opening: neither reset nor boot mode until the flasher asks
        _port = new SerialPort(port, 115200, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None, DtrEnable = false, RtsEnable = false, ReadTimeout = 100, WriteTimeout = 3000, ReadBufferSize = 64 * 1024,
        };
        _port.Open();
    }

    public int BaudRate
    {
        get => _port.BaudRate;
        set => _port.BaudRate = value;
    }

    public void Write(ReadOnlySpan<byte> data) => _port.BaseStream.Write(data);

    public byte[] Read(int timeoutMs)
    {
        _port.ReadTimeout = Math.Max(1, timeoutMs);
        try
        {
            var n = _port.Read(_buffer, 0, _buffer.Length);
            return _buffer[..n];
        }
        catch (TimeoutException)
        {
            return [];
        }
    }

    public void SetDtr(bool on) => _port.DtrEnable = on;

    /// <summary>RTS, then DTR written again with the same value (never toggled: a pulse on DTR is a pulse on the boot
    /// pin): with Windows' usbser.sys an RTS change alone does not reach the lines (esptool does the same).</summary>
    public void SetRts(bool on)
    {
        _port.RtsEnable = on;
        _port.DtrEnable = _port.DtrEnable;
    }

    public void DiscardInput() => _port.DiscardInBuffer();

    public void Dispose()
    {
        try
        {
            _port.DtrEnable = false;                         // leave the board running, not held in reset or boot mode
            _port.RtsEnable = false;
        }
        catch (IOException)
        {
            // the board went (unplugged): nothing to release
        }

        _port.Dispose();
    }
}
