using System.Buffers.Binary;
using System.Security.Cryptography;
using GlassLink.Core.Flash;

namespace GlassLink.Core.Tests;

/// <summary>
/// A pretend ESP32-P4-NANO for the flasher tests: the auto-download circuit (RTS resets, DTR held low at the release
/// starts the ROM bootloader, else the app), and the ROM's answers with a 16 MB NOR flash (erased to FF, written by AND).
/// </summary>
internal sealed class FakeP4Rom : IBoardLink
{
    public readonly byte[] Flash = Enumerable.Repeat((byte)0xFF, 16 * 1024 * 1024).ToArray();
    public readonly Dictionary<uint, uint> Registers = [];
    public readonly List<string> Seen = [];
    public uint ChipId = 18;
    public bool TakesBaud = true;
    public int GarbleBlock = -1;                       // this block arrives garbled once (the ROM's checksum error)
    public bool CorruptFlash;                          // a bit of every first block does not stick
    public bool Running;                               // the app was started
    private readonly Slip _slip = new();
    private readonly List<byte> _out = [];
    private bool _dtr, _rts, _held, _download;
    private int _romBaud = 115200;
    private uint _offset;

    public FakeP4Rom(int revision, byte[] mac)
    {
        Registers[0x5012D04C] = (uint)((revision / 100 & 4) << 21 | (revision / 100 & 3) << 4 | revision % 100);
        Registers[0x5012D044] = BinaryPrimitives.ReadUInt32BigEndian(mac.AsSpan(2));
        Registers[0x5012D048] = (uint)(mac[0] << 8 | mac[1]);
    }

    public int BaudRate { get; set; } = 115200;

    public void SetDtr(bool on) => _dtr = on;

    public void SetRts(bool on)
    {
        if (on && !_dtr)
        {
            _held = true;                              // EN low: in reset
            _download = Running = false;
            _out.Clear();
        }
        else if (!on && _rts && _held)
        {
            _held = false;                             // EN released: GPIO35 low (DTR) means the ROM bootloader
            _download = _dtr;
            Running = !_dtr;
            _romBaud = 115200;
            _slip.Reset();
            _out.AddRange("ESP-ROM:esp32p4-eco2-20240710\r\nwaiting for download\r\n"u8.ToArray());
        }

        _rts = on;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (!_download || BaudRate != _romBaud)
        {
            return;                                    // nobody listening, or bytes at another rate: lost
        }

        foreach (var packet in _slip.Feed(data))
        {
            Handle(packet);
        }
    }

    public byte[] Read(int timeoutMs)
    {
        if (_out.Count == 0)
        {
            Thread.Sleep(Math.Min(timeoutMs, 5));
            return [];
        }

        byte[] bytes = [.. _out];
        _out.Clear();
        return bytes;
    }

    public void DiscardInput() => _out.Clear();

    public void Dispose()
    {
    }

    private void Handle(byte[] p)
    {
        var command = p[1];
        var checksum = BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4));
        var data = p.AsSpan(8);
        uint Word(int i) => BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(8 + i * 4));
        switch (command)
        {
            case 0x08:
                for (var i = 0; i < 8; i++)
                {
                    Reply(command, 0, []);                 // the ROM answers a SYNC several times
                }

                break;
            case 0x14:
                var security = new byte[20];
                BinaryPrimitives.WriteUInt32LittleEndian(security.AsSpan(12), ChipId);
                Reply(command, 0, security);
                break;
            case 0x0A:
                Reply(command, Registers.GetValueOrDefault(Word(0)), []);
                break;
            case 0x09:
                Registers[Word(0)] = (Registers.GetValueOrDefault(Word(0)) & ~Word(2)) | (Word(1) & Word(2));
                Seen.Add($"write {Word(0):X8}");
                Reply(command, 0, []);
                break;
            case 0x0D:
                Seen.Add("attach");
                Reply(command, 0, []);
                break;
            case 0x0B:
                Seen.Add($"params {Word(1)}");
                Reply(command, 0, []);
                break;
            case 0x0F:
                Reply(command, 0, []);                     // at the old rate
                if (TakesBaud)
                {
                    _romBaud = (int)Word(0);
                }

                break;
            case 0x02:
                _offset = Word(3);
                var end = (Word(3) + Word(0) + 0xFFF) & ~0xFFFu;
                Array.Fill(Flash, (byte)0xFF, (int)_offset, (int)(end - _offset));     // erases whole sectors
                Seen.Add($"begin {_offset:x} {Word(1)} blocks");
                Reply(command, 0, []);
                break;
            case 0x03:
                var (length, seq) = ((int)Word(0), (int)Word(1));
                var block = data.Slice(16, length);
                byte sum = 0xEF;
                foreach (var b in block)
                {
                    sum ^= b;
                }

                if (sum != checksum || seq == GarbleBlock)
                {
                    GarbleBlock = -1;
                    Fail(command, 0x07);
                    break;
                }

                var at = (int)_offset + seq * 0x400;
                for (var i = 0; i < length; i++)
                {
                    Flash[at + i] &= block[i];
                }

                if (CorruptFlash && seq == 0)
                {
                    Flash[at] |= 1;
                }

                Reply(command, 0, []);
                break;
            case 0x13:
                var md5 = MD5.HashData(Flash.AsSpan((int)Word(0), (int)Word(1)));
                Reply(command, 0, System.Text.Encoding.ASCII.GetBytes(Convert.ToHexStringLower(md5)));
                break;
            default:
                Fail(command, 0x05);
                break;
        }
    }

    private void Reply(byte command, uint value, ReadOnlySpan<byte> body, byte status = 0, byte error = 0)
    {
        var packet = new byte[8 + body.Length + 4];
        packet[0] = 0x01;
        packet[1] = command;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)(body.Length + 4));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), value);
        body.CopyTo(packet.AsSpan(8));
        packet[^4] = status;
        packet[^3] = error;
        _out.AddRange(Slip.Encode(packet));
    }

    private void Fail(byte command, byte error) => Reply(command, 0, [], 1, error);
}
