using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace GlassLink.Core.Protocol;

/// <summary>Message types of the DU link (docs/usb-protocol.md). Protocol version 1.</summary>
public enum MessageType : byte
{
    // host -> DU
    Frame = 0x01,
    GetInfo = 0x02,
    SetBrightness = 0x03,
    SetRotation = 0x04,
    ShowIdent = 0x05,
    Ping = 0x06,
    SetAssigned = 0x07,
    SetPanelPower = 0x08,
    SetMode = 0x09,             // HDMI mode 0..3; the DU stores it and restarts
    SetLayout = 0x0A,           // tiles, 8 bytes each (uint16 x, y, w, h); arg bit 0 = show test cards
    Tile = 0x0B,                // a JPEG for one tile (arg = tile index); answered with READY like Frame       // reserved for the hardware track (usb-protocol.md 6a); nothing sends it yet
    OtaBegin = 0x10,
    OtaData = 0x11,
    OtaEnd = 0x12,
    Reboot = 0x20,

    // DU -> host
    Ready = 0x81,
    Info = 0x82,
    Stats = 0x83,
    Pong = 0x84,
    Log = 0x85,
    OtaResult = 0x90,
    OtaProgress = 0x91,
}

/// <summary>One message as it travels over the link: a 16-byte header and a payload.</summary>
public sealed record Message(MessageType Type, uint Seq, uint Arg, ReadOnlyMemory<byte> Payload)
{
    public string Name => Enum.IsDefined(Type) ? Type.ToString() : $"0x{(byte)Type:x2}";

    /// <summary>The payload as JSON (INFO, STATS); null if it is not valid JSON.</summary>
    public JsonElement? Json()
    {
        try
        {
            return JsonDocument.Parse(Payload).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string Text() => Encoding.UTF8.GetString(Payload.Span);

    public override string ToString() => $"<{Name} seq={Seq} arg={Arg} len={Payload.Length}>";
}

public static class Wire
{
    public const int HeaderSize = 16;
    public const int MaxPayload = 4 * 1024 * 1024;
    public const int UsbPacket = 512;
    public const int OtaChunk = 32 * 1024;
    public const byte ProtocolVersion = 1;
    public const byte Magic0 = (byte)'X';
    public const byte Magic1 = (byte)'D';

    /// <summary>Header layout, little endian: 'X','D', version, type, length, seq, arg.</summary>
    public static byte[] Pack(MessageType type, ReadOnlySpan<byte> payload = default, uint seq = 0, uint arg = 0)
    {
        if (payload.Length > MaxPayload)
        {
            throw new ArgumentException("payload too large", nameof(payload));
        }

        var buf = new byte[HeaderSize + payload.Length];
        buf[0] = Magic0;
        buf[1] = Magic1;
        buf[2] = ProtocolVersion;
        buf[3] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), seq);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(12), arg);
        payload.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }

    /// <summary>Parses a header. Returns false for anything that cannot be one (magic, version, size).</summary>
    public static bool TryParseHeader(ReadOnlySpan<byte> buf, out MessageType type, out int length, out uint seq, out uint arg)
    {
        type = 0;
        length = 0;
        seq = arg = 0;
        if (buf.Length < HeaderSize || buf[0] != Magic0 || buf[1] != Magic1 || buf[2] != ProtocolVersion)
        {
            return false;
        }

        var len = BinaryPrimitives.ReadUInt32LittleEndian(buf[4..]);
        if (len > MaxPayload)
        {
            return false;
        }

        type = (MessageType)buf[3];
        length = (int)len;
        seq = BinaryPrimitives.ReadUInt32LittleEndian(buf[8..]);
        arg = BinaryPrimitives.ReadUInt32LittleEndian(buf[12..]);
        return true;
    }
}
