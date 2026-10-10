using System.Buffers.Binary;
using System.Security.Cryptography;

namespace GlassLink.Core.Flash;

/// <summary>An error the chip's bootloader answered, or no answer at all; the message is shown to the user.</summary>
public sealed class FlashException(string message) : IOException(message);

/// <summary>
/// GlassLink's own flasher for the ESP32-P4's ROM serial bootloader (Espressif's documented serial protocol; facts
/// checked against esptool v5.5.0, which GlassLink does not ship): reset into download mode through the NANO's
/// DTR/RTS circuit, identify the chip, write flash in 1 KB blocks and check each part with the ROM's MD5. No stub is
/// loaded and nothing is compressed, as esptool does with the ROM alone. The board's NVS is never touched.
/// </summary>
public sealed class EspLoader(IBoardLink link, Action<string>? log = null)
{
    private const byte Sync = 0x08, ReadRegister = 0x0A, WriteRegister = 0x09, SpiAttach = 0x0D, SpiSetParams = 0x0B,
        FlashBegin = 0x02, FlashData = 0x03, FlashMd5 = 0x13, ChangeBaud = 0x0F, SecurityInfo = 0x14;
    private const int Block = 0x400;                                  // the ROM's flash write block
    private const uint P4ChipId = 18;
    private const uint EfuseMac0 = 0x5012D044, EfuseMac1 = 0x5012D048, EfuseVersion = 0x5012D04C, EfuseDownloadXpd = 0x5012D034;
    private readonly Slip _slip = new();
    private readonly Queue<byte[]> _packets = new();
    private ChipFacts? _facts;

    /// <summary>Resets the board into its bootloader and finds out what it is: an ESP32-P4, its revision and MAC.
    /// Then the flash is powered (v3.x) and attached, ready to be written.</summary>
    public ChipFacts Connect()
    {
        if (!Synced())
        {
            throw new FlashException("the board does not answer in its bootloader: is it a NANO on its USB-C socket, and no serial monitor open on the port?");
        }

        var security = Command(SecurityInfo, [], timeoutMs: 3000).Data;
        var chip = security.Length >= 16 ? BinaryPrimitives.ReadUInt32LittleEndian(security.AsSpan(12)) : 0;
        if (chip != P4ChipId)
        {
            throw new FlashException($"the board is no ESP32-P4 (chip id {chip})");
        }

        if (security.Length >= 4 && (BinaryPrimitives.ReadUInt32LittleEndian(security) & 0b100) != 0)
        {
            throw new FlashException("the chip is in secure download mode: GlassLink cannot write it");
        }

        var w = Read(EfuseVersion);
        var revision = (int)(((w >> 23 & 1) << 2 | (w >> 4 & 3)) * 100 + (w & 0xF));
        var (w0, w1) = (Read(EfuseMac0), Read(EfuseMac1));
        byte[] mac = [(byte)(w1 >> 8), (byte)w1, (byte)(w0 >> 24), (byte)(w0 >> 16), (byte)(w0 >> 8), (byte)w0];
        _facts = new ChipFacts(revision, mac);
        log?.Invoke($"ESP32-P4 {_facts.RevisionName}, MAC {Convert.ToHexString(mac)}");

        PowerOnFlash(revision);
        Command(SpiAttach, new byte[8], timeoutMs: 3000);                              // the ROM takes 8 bytes: 0, then 4 x 0
        var p = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), 16u * 1024 * 1024);      // id 0, total 16 MB (the NANO's flash)
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), 0x10000);                 // block
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), 0x1000);                 // sector
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(16), 0x100);                  // page
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(20), 0xFFFF);                 // status mask
        Command(SpiSetParams, p, timeoutMs: 3000);
        return _facts;
    }

    /// <summary>Faster than the ROM's 115200 if the board takes it; else stays at 115200 (false).</summary>
    public bool SetBaud(int baud)
    {
        var old = link.BaudRate;
        try
        {
            var p = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(p, (uint)baud);                     // second word 0: the ROM's form
            Command(ChangeBaud, p, timeoutMs: 3000);
            link.BaudRate = baud;
            Thread.Sleep(50);
            link.DiscardInput();
            _slip.Reset();
            _packets.Clear();
            Read(EfuseVersion);                                                         // does it answer at the new rate?
            log?.Invoke($"{baud} baud");
            return true;
        }
        catch (FlashException)
        {
            // the ROM did not take it, or not cleanly: from the start at 115200 (a reset brings the ROM back to it)
            link.BaudRate = old;
            log?.Invoke($"{baud} baud did not work: staying at {old}");
            Connect();
            return false;
        }
    }

    /// <summary>Writes one part (erased first by the ROM) and checks it with the ROM's MD5.</summary>
    public void Write(FlashPart part, Action<double>? progress = null)
    {
        var data = Pad(part.Data, 4);
        var blocks = (data.Length + Block - 1) / Block;
        var begin = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(begin, (uint)data.Length);            // erase size
        BinaryPrimitives.WriteUInt32LittleEndian(begin.AsSpan(4), (uint)blocks);
        BinaryPrimitives.WriteUInt32LittleEndian(begin.AsSpan(8), Block);
        BinaryPrimitives.WriteUInt32LittleEndian(begin.AsSpan(12), part.Offset);
        // fifth word 0: not encrypted (the P4's ROM expects it)
        Command(FlashBegin, begin, timeoutMs: PerMegabyte(data.Length, 40_000));
        for (var seq = 0; seq < blocks; seq++)
        {
            var chunk = new byte[Block];
            Array.Fill(chunk, (byte)0xFF);
            var take = Math.Min(Block, data.Length - seq * Block);
            Array.Copy(data, seq * Block, chunk, 0, take);
            var payload = new byte[16 + Block];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, Block);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), (uint)seq);
            chunk.CopyTo(payload, 16);
            Retry(() => Command(FlashData, payload, Checksum(chunk), timeoutMs: 3000), 3);
            progress?.Invoke((seq + 1) / (double)blocks);
        }

        var md5 = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(md5, part.Offset);
        BinaryPrimitives.WriteUInt32LittleEndian(md5.AsSpan(4), (uint)data.Length);
        var answer = Command(FlashMd5, md5, timeoutMs: PerMegabyte(data.Length, 8_000)).Data;
        var theirs = System.Text.Encoding.ASCII.GetString(answer.AsSpan(0, Math.Min(32, answer.Length)));
        var ours = Convert.ToHexString(MD5.HashData(data));
        if (!string.Equals(theirs, ours, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlashException($"{part.Name} did not arrive intact (MD5 {theirs} instead of {ours.ToLowerInvariant()})");
        }

        log?.Invoke($"0x{part.Offset:x} {part.Name}: {data.Length} bytes written and checked");
    }

    /// <summary>Lets the board run what is in its flash (an RTS pulse on EN, DTR released).</summary>
    public void HardReset()
    {
        link.SetDtr(false);
        link.SetRts(true);
        Thread.Sleep(100);
        link.SetRts(false);
    }

    /// <summary>Reset into the ROM bootloader (esptool's "classic reset") and SYNC: 7 attempts, holding the reset
    /// 50 ms and 550 ms in turn, 5 SYNCs each.</summary>
    private bool Synced()
    {
        for (var attempt = 0; attempt < 7; attempt++)
        {
            link.SetDtr(false);                              // IO0 high
            link.SetRts(true);                               // EN low: reset
            Thread.Sleep(100);
            link.SetDtr(true);                               // IO0 low
            link.SetRts(false);                              // EN high: the chip starts, in download mode
            Thread.Sleep(attempt % 2 == 0 ? 50 : 550);
            link.SetDtr(false);
            for (var i = 0; i < 5; i++)
            {
                link.DiscardInput();
                _slip.Reset();
                _packets.Clear();
                try
                {
                    byte[] payload = [0x07, 0x07, 0x12, 0x20, .. Enumerable.Repeat((byte)0x55, 32)];
                    Command(Sync, payload, timeoutMs: 100);
                    Thread.Sleep(100);                       // the ROM answers a SYNC several times: let the rest come and go
                    link.DiscardInput();
                    _slip.Reset();
                    _packets.Clear();
                    return true;
                }
                catch (FlashException)
                {
                    Thread.Sleep(50);
                }
            }
        }

        return false;
    }

    /// <summary>The flash of a v3.1 / v3.2 chip is not powered in download mode: esptool's power_on_flash
    /// (targets/esp32p4.py, v5.5.0), register for register. Not needed on v1.x.</summary>
    private void PowerOnFlash(int revision)
    {
        if (revision is not (301 or 302))
        {
            return;
        }

        if (revision == 302 && (Read(EfuseDownloadXpd) >> 16 & 1) == 1)
        {
            var pmu = Read(0x501153FC);                      // ECO7 ROM bug: a second download session after a UART reset
            if ((pmu & 3) == 3)
            {
                Write(0x501153FC, pmu & ~3u);
            }

            return;
        }

        Write(0x5011010C, 1);
        Thread.Sleep(10);
        Modify(0x501151BC, v => v | 1u << 27);
        Modify(0x501151B8, v => v | 1u << 7);
        Modify(0x501153FC, v => v | 3);
        Thread.Sleep(1);
        Modify(0x501151BC, v => v & ~(1u << 27));
        Modify(0x501151B8, v => v & ~(0xFFu << 23));
        Modify(0x501151B8, v => v | 0x80);
        Modify(0x501151B8, v => v & ~0x80u);
        Thread.Sleep(2);
    }

    private uint Read(uint address)
    {
        var p = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(p, address);
        return Command(ReadRegister, p, timeoutMs: 3000).Value;
    }

    private void Write(uint address, uint value)
    {
        var p = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(p, address);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), value);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), 0xFFFFFFFF);           // mask: all bits; no delay
        Command(WriteRegister, p, timeoutMs: 3000);
    }

    private void Modify(uint address, Func<uint, uint> change) => Write(address, change(Read(address)));

    /// <summary>Sends a command and waits for its answer: the 4-byte value, and the data without the ROM's 4 status
    /// bytes. Packets that answer something else are skipped (esptool reads up to 100).</summary>
    private (uint Value, byte[] Data) Command(byte command, ReadOnlySpan<byte> data, uint checksum = 0, int timeoutMs = 3000)
    {
        var packet = new byte[8 + data.Length];
        packet[0] = 0x00;
        packet[1] = command;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), checksum);
        data.CopyTo(packet.AsSpan(8));
        link.Write(Slip.Encode(packet));

        var deadline = Environment.TickCount64 + timeoutMs;
        for (var seen = 0; seen < 100;)
        {
            if (_packets.Count == 0)
            {
                var left = deadline - Environment.TickCount64;
                if (left <= 0)
                {
                    break;
                }

                foreach (var p in _slip.Feed(link.Read((int)Math.Min(left, 100))))
                {
                    _packets.Enqueue(p);
                }

                continue;
            }

            var r = _packets.Dequeue();
            seen++;
            if (r.Length < 8 || r[0] != 0x01 || r[1] != command)
            {
                continue;
            }

            var size = BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(2));
            var value = BinaryPrimitives.ReadUInt32LittleEndian(r.AsSpan(4));
            var body = r.AsSpan(8, Math.Min(size, r.Length - 8));
            if (body.Length < 4)
            {
                throw new FlashException($"command 0x{command:x2}: an answer without status");
            }

            var (status, error) = (body[^4], body[^3]);       // the ROM's 4 status bytes: status, error, 2 reserved
            if (status != 0)
            {
                throw new FlashException($"command 0x{command:x2} failed: {Error(error)}");
            }

            return (value, body[..^4].ToArray());
        }

        throw new FlashException($"command 0x{command:x2}: no answer from the board");
    }

    private static string Error(byte code) => code switch
    {
        0x05 => "the message was invalid", 0x06 => "the chip could not do it", 0x07 => "checksum error",
        0x08 => "flash write error (the read-back differed)", 0x09 => "flash read error", 0x0A => "flash read length error",
        0x0B => "deflate error", _ => $"ROM error 0x{code:x2}",
    };

    private static uint Checksum(ReadOnlySpan<byte> data)
    {
        byte c = 0xEF;
        foreach (var b in data)
        {
            c ^= b;
        }

        return c;
    }

    private static byte[] Pad(byte[] data, int multiple)
    {
        var length = (data.Length + multiple - 1) / multiple * multiple;
        if (length == data.Length)
        {
            return data;
        }

        var padded = new byte[length];
        Array.Fill(padded, (byte)0xFF);
        data.CopyTo(padded, 0);
        return padded;
    }

    private static int PerMegabyte(int bytes, int msPerMb) => Math.Max(3000, (int)((long)msPerMb * bytes / (1024 * 1024)));

    private static void Retry(Action action, int times)
    {
        for (var i = 1; ; i++)
        {
            try
            {
                action();
                return;
            }
            catch (FlashException) when (i < times)
            {
                // a block that did not arrive: sent again with the same sequence number
            }
        }
    }
}
