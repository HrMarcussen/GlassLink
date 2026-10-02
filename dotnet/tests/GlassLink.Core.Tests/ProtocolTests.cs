using GlassLink.Core.Protocol;

namespace GlassLink.Core.Tests;

public class ProtocolTests
{
    [Fact]
    public void Header_is_16_bytes_little_endian_as_in_the_protocol_document()
    {
        var packed = Wire.Pack(MessageType.Frame, [1, 2, 3], seq: 0x01020304, arg: 7);
        // struct.pack("<2sBBIII", b"XD", 1, 0x01, 3, 0x01020304, 7) + b"\x01\x02\x03"
        byte[] expected = [0x58, 0x44, 0x01, 0x01, 3, 0, 0, 0, 0x04, 0x03, 0x02, 0x01, 7, 0, 0, 0, 1, 2, 3];
        Assert.Equal(expected, packed);
        Assert.True(Wire.TryParseHeader(packed, out var type, out var length, out var seq, out var arg));
        Assert.Equal((MessageType.Frame, 3, 0x01020304u, 7u), (type, length, seq, arg));
    }

    [Fact]
    public void Bad_headers_are_refused()
    {
        var ok = Wire.Pack(MessageType.Ready);
        Assert.False(Wire.TryParseHeader(ok.AsSpan(0, 15), out _, out _, out _, out _));
        var badMagic = (byte[])ok.Clone();
        badMagic[0] = (byte)'Y';
        Assert.False(Wire.TryParseHeader(badMagic, out _, out _, out _, out _));
        var badVersion = (byte[])ok.Clone();
        badVersion[2] = 2;
        Assert.False(Wire.TryParseHeader(badVersion, out _, out _, out _, out _));
        var tooLong = (byte[])ok.Clone();
        tooLong[7] = 0x7f;
        Assert.False(Wire.TryParseHeader(tooLong, out _, out _, out _, out _));
    }

    [Fact]
    public void Reader_reassembles_messages_split_across_transfers()
    {
        var stream = Wire.Pack(MessageType.Info, "{\"fw\":\"0.5.0\"}"u8).Concat(Wire.Pack(MessageType.Ready, seq: 9)).ToArray();
        var reader = new MessageReader();
        var got = new List<Message>();
        foreach (var piece in stream.Chunk(5))
        {
            got.AddRange(reader.Feed(piece));
        }

        Assert.Equal([MessageType.Info, MessageType.Ready], got.Select(m => m.Type));
        Assert.Equal("0.5.0", got[0].Json()!.Value.GetProperty("fw").GetString());
        Assert.Equal(9u, got[1].Seq);
        Assert.Equal(0, reader.Resyncs);
    }

    [Fact]
    public void Reader_skips_the_tail_of_a_stale_message_and_finds_the_next_header()
    {
        // what a DU still had in its send buffer when the previous DMC went away, then a fresh INFO and READY
        var garbage = Enumerable.Range(0, 300).Select(i => (byte)(i * 7)).ToArray();
        garbage[40] = (byte)'X';
        garbage[41] = (byte)'D';            // magic inside the garbage must not fool it
        var stream = garbage.Concat(Wire.Pack(MessageType.Info, "{}"u8)).Concat(Wire.Pack(MessageType.Ready)).ToArray();
        var reader = new MessageReader();
        var got = new List<Message>();
        foreach (var piece in stream.Chunk(64))
        {
            got.AddRange(reader.Feed(piece));
        }

        Assert.Equal([MessageType.Info, MessageType.Ready], got.Select(m => m.Type));
        Assert.True(reader.Resyncs >= 1);
        Assert.Equal(300, reader.Skipped);
    }

    [Fact]
    public void A_header_with_an_unknown_type_is_not_a_header()
    {
        var fake = Wire.Pack((MessageType)0x55, [9, 9]);
        var reader = new MessageReader();
        var got = reader.Feed(fake.Concat(Wire.Pack(MessageType.Pong, arg: 3)).ToArray());
        Assert.Single(got);
        Assert.Equal(MessageType.Pong, got[0].Type);
    }
}
