namespace GlassLink.Core.Protocol;

/// <summary>
/// Reassembles messages from bulk-IN transfers. If the stream is out of step (an earlier host session ended in the
/// middle of a message, so its tail is still in the DU's send buffer), the garbage is skipped up to the next valid
/// header instead of failing: the same idea as the byte-wise resync in the DU firmware.
/// </summary>
public sealed class MessageReader
{
    private byte[] _buf = new byte[64 * 1024];
    private readonly bool _fromDu;

    /// <param name="fromDu">Reading what a DU sends: only DU-to-host types (0x80 and up) and at most 64 KB count as a
    /// header, so a stale host message or a fake header in old bytes cannot swallow READYs (#34).</param>
    public MessageReader(bool fromDu = false) => _fromDu = fromDu;
    private int _len;

    /// <summary>How many times garbage had to be skipped.</summary>
    public int Resyncs { get; private set; }

    /// <summary>Bytes dropped while doing so.</summary>
    public long Skipped { get; private set; }

    public void Reset() => _len = 0;

    public IReadOnlyList<Message> Feed(ReadOnlySpan<byte> chunk)
    {
        Append(chunk);
        var found = new List<Message>();
        var pos = 0;
        while (_len - pos >= Wire.HeaderSize)
        {
            if (!ValidAt(pos))
            {
                // Slide to the next place where a plausible header starts; keep a possible partial header at the end.
                var limit = _len - Wire.HeaderSize;
                var i = pos + 1;
                while (i <= limit && !ValidAt(i))
                {
                    var next = IndexOfMagic(i + 1);
                    i = next >= 0 ? next : limit + 1;
                }

                if (i > limit)
                {
                    i = Math.Min(i, _len - (Wire.HeaderSize - 1));
                }

                Resyncs++;
                Skipped += i - pos;
                pos = i;
                continue;
            }

            Wire.TryParseHeader(_buf.AsSpan(pos, Wire.HeaderSize), out var type, out var length, out var seq, out var arg);
            var total = Wire.HeaderSize + length;
            if (_len - pos < total)
            {
                break;
            }

            found.Add(new Message(type, seq, arg, _buf.AsSpan(pos + Wire.HeaderSize, length).ToArray()));
            pos += total;
        }

        if (pos > 0)
        {
            Buffer.BlockCopy(_buf, pos, _buf, 0, _len - pos);
            _len -= pos;
        }

        return found;
    }

    private void Append(ReadOnlySpan<byte> chunk)
    {
        if (_len + chunk.Length > _buf.Length)
        {
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + chunk.Length));
        }

        chunk.CopyTo(_buf.AsSpan(_len));
        _len += chunk.Length;
    }

    /// <summary>A header with a known message type, so payload bytes cannot fake one.</summary>
    private bool ValidAt(int i) =>
        _len - i >= Wire.HeaderSize
        && Wire.TryParseHeader(_buf.AsSpan(i, Wire.HeaderSize), out var type, out var length, out _, out _)
        && Enum.IsDefined(type)
        && (!_fromDu || ((byte)type >= 0x80 && length <= 64 * 1024));

    private int IndexOfMagic(int from)
    {
        for (var i = from; i < _len - 1; i++)
        {
            if (_buf[i] == Wire.Magic0 && _buf[i + 1] == Wire.Magic1)
            {
                return i;
            }
        }

        return -1;
    }
}
