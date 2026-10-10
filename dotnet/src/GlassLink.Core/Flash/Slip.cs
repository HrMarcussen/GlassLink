namespace GlassLink.Core.Flash;

/// <summary>
/// SLIP framing as the ESP32 serial bootloader uses it: each packet between two 0xC0 bytes, a 0xC0 inside written as
/// 0xDB 0xDC and a 0xDB as 0xDB 0xDD (Espressif's serial protocol, docs/esptool "Serial Protocol").
/// </summary>
public sealed class Slip
{
    private const byte End = 0xC0, Esc = 0xDB, EscEnd = 0xDC, EscEsc = 0xDD;
    private readonly List<byte> _packet = [];
    private bool _inPacket, _escaped;

    public static byte[] Encode(ReadOnlySpan<byte> packet)
    {
        var output = new List<byte>(packet.Length + 8) { End };
        foreach (var b in packet)
        {
            switch (b)
            {
                case End:
                    output.Add(Esc);
                    output.Add(EscEnd);
                    break;
                case Esc:
                    output.Add(Esc);
                    output.Add(EscEsc);
                    break;
                default:
                    output.Add(b);
                    break;
            }
        }

        output.Add(End);
        return [.. output];
    }

    /// <summary>Feeds received bytes; returns the packets completed by them. Bytes outside a packet (the chip's boot
    /// log while it resets) are skipped.</summary>
    public IReadOnlyList<byte[]> Feed(ReadOnlySpan<byte> bytes)
    {
        var done = new List<byte[]>();
        foreach (var b in bytes)
        {
            if (b == End)
            {
                if (_inPacket && _packet.Count > 0)
                {
                    done.Add([.. _packet]);
                    _packet.Clear();
                    _inPacket = false;
                }
                else
                {
                    _inPacket = true;                        // an opening 0xC0 (or two in a row: an empty packet)
                }

                _escaped = false;
                continue;
            }

            if (!_inPacket)
            {
                continue;
            }

            if (_escaped)
            {
                _packet.Add(b switch { EscEnd => End, EscEsc => Esc, _ => b });
                _escaped = false;
            }
            else if (b == Esc)
            {
                _escaped = true;
            }
            else
            {
                _packet.Add(b);
            }
        }

        return done;
    }

    public void Reset()
    {
        _packet.Clear();
        _inPacket = _escaped = false;
    }
}
