using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Text;
using GlassLink.Core.Du;
using GlassLink.Core.Protocol;
using GlassLink.Core.Usb;

namespace GlassLink.Core.Tests;

/// <summary>An in-process DU: answers GET_INFO, sends READY after every FRAME, acknowledges updates.</summary>
internal sealed class FakeDu : IDuTransport
{
    private readonly BlockingCollection<byte[]> _toHost = new();
    private readonly MessageReader _fromHost = new();
    public readonly List<Message> Received = [];
    public readonly MemoryStream Flashed = new();
    public bool AnswerInfo = true;
    public string InfoJson = "{\"fw\":\"0.5.0\",\"build\":\"abc1234\",\"hw\":\"fake\",\"panel\":[768,768],\"mode\":3,\"caps\":[\"mode\",\"tiles\"],\"slot\":\"ota_0\",\"uptime_s\":5}";
    public int OtaFailAtChunk = -1;
    private int _otaChunks;

    public string Serial => "aabbccddeeff001122334455";

    public string Description => "fake DU";

    public FakeDu(byte[]? staleBytes = null)
    {
        if (staleBytes is not null)
        {
            _toHost.Add(staleBytes);
        }

        _toHost.Add(Wire.Pack(MessageType.Ready));
    }

    private volatile bool _unplugged;

    /// <summary>Pulls the cable: the next read fails like a real transport's does.</summary>
    public void Unplug() => _unplugged = true;

    public byte[]? ReadChunk(int timeoutMs)
    {
        if (_unplugged)
        {
            throw new IOException("device not connected");
        }

        return _toHost.TryTake(out var chunk, Math.Min(timeoutMs, 50)) ? chunk : null;
    }

    private readonly object _writeGate = new();

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_writeGate)                   // like the real transport: commands come from several threads
        {
            Handle(_fromHost.Feed(data));
        }
    }

    private void Handle(IReadOnlyList<Message> messages)
    {
        foreach (var m in messages)
        {
            lock (Received)
            {
                Received.Add(m);
            }

            switch (m.Type)
            {
                case MessageType.GetInfo when AnswerInfo:
                    _toHost.Add(Wire.Pack(MessageType.Info, Encoding.UTF8.GetBytes(InfoJson)));
                    break;
                case MessageType.Frame:
                case MessageType.Tile:                       // a tile is answered like a frame
                    _toHost.Add(Wire.Pack(MessageType.Ready, seq: m.Seq));
                    break;
                case MessageType.Ping:
                    _toHost.Add(Wire.Pack(MessageType.Pong, arg: m.Arg));
                    break;
                case MessageType.OtaBegin:
                    _toHost.Add(Wire.Pack(MessageType.OtaProgress, arg: 0));
                    break;
                case MessageType.OtaData:
                    if (_otaChunks++ == OtaFailAtChunk)
                    {
                        _toHost.Add(Wire.Pack(MessageType.OtaResult, arg: 2));
                        break;
                    }

                    Flashed.Write(m.Payload.Span);
                    _toHost.Add(Wire.Pack(MessageType.OtaProgress, arg: (uint)Flashed.Length));
                    break;
                case MessageType.OtaEnd:
                    _toHost.Add(Wire.Pack(MessageType.OtaResult, arg: m.Arg == Crc32.HashToUInt32(Flashed.ToArray()) ? 0u : 4u));
                    break;
            }
        }
    }

    public void SendStats(string json) => _toHost.Add(Wire.Pack(MessageType.Stats, Encoding.UTF8.GetBytes(json)));

    public void SendInfo(string json) => _toHost.Add(Wire.Pack(MessageType.Info, Encoding.UTF8.GetBytes(json)));

    public List<Message> Of(MessageType type)
    {
        lock (Received)
        {
            return Received.Where(m => m.Type == type).ToList();
        }
    }

    public void Dispose()
    {
    }
}

public class DuConnectionTests
{
    private static void Until(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(5);
        }

        Assert.True(condition(), "timed out");
    }

    [Fact]
    public void Sends_only_new_frames_one_per_ready()
    {
        var du = new FakeDu();
        var slot = new FrameSlot("pfd");
        using var conn = new DuConnection(du);
        conn.Start();
        conn.Source = slot;
        Until(() => conn.Info is not null);
        Assert.Equal("0.5.0", conn.Info!.Firmware);
        Assert.Equal((768, 768), (conn.Info.PanelWidth, conn.Info.PanelHeight));

        slot.Publish(new byte[] { 1 });
        Until(() => du.Of(MessageType.Frame).Count == 1);
        Thread.Sleep(80);                                           // nothing new: no resend although the DU is READY
        Assert.Single(du.Of(MessageType.Frame));

        slot.Publish(new byte[] { 2 });
        slot.Publish(new byte[] { 3 });                             // only the newest matters
        Until(() => du.Of(MessageType.Frame).Count >= 2);
        Thread.Sleep(80);
        var frames = du.Of(MessageType.Frame);
        Assert.Equal(3u, frames[^1].Seq);
        Assert.True(frames.Count <= 3);
        Assert.Equal(frames.Count, conn.FramesSent);
    }

    [Fact]
    public void A_newly_assigned_display_starts_from_its_own_newest_frame()
    {
        var du = new FakeDu();
        var pfd = new FrameSlot("pfd");
        var nd = new FrameSlot("nd");
        for (var i = 0; i < 50; i++)
        {
            pfd.Publish(new byte[] { 1 });
        }

        nd.Publish(new byte[] { 9 });                              // seq 1, far below what the DU already got from pfd
        using var conn = new DuConnection(du);
        conn.Start();
        conn.Source = pfd;
        Until(() => du.Of(MessageType.Frame).Count == 1);
        conn.Source = nd;
        Until(() => du.Of(MessageType.Frame).Count == 2);
        Assert.Equal(9, du.Of(MessageType.Frame)[1].Payload.Span[0]);
        Assert.Contains(du.Of(MessageType.SetAssigned), m => m.Arg == 1);
    }

    [Fact]
    public void Survives_stale_bytes_and_a_lost_info_answer()
    {
        var stale = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        var du = new FakeDu(stale) { AnswerInfo = false };
        using var conn = new DuConnection(du);
        conn.Start();
        Thread.Sleep(300);
        Assert.Null(conn.Info);
        du.AnswerInfo = true;                                       // the retry after 2 s gets through
        Until(() => conn.Info is not null, 4000);
        Assert.True(conn.Resyncs >= 1);
        Assert.True(du.Of(MessageType.GetInfo).Count >= 2);
    }

    [Fact]
    public void Ident_ping_and_brightness_reach_the_du()
    {
        var du = new FakeDu();
        using var conn = new DuConnection(du);
        conn.Start();
        conn.Ident("DU1", 30);
        conn.SetBrightness(140);
        conn.Ping();
        Until(() => conn.PingMs is not null);
        var ident = du.Of(MessageType.ShowIdent).Single();
        Assert.Equal(("DU1", 30u), (ident.Text(), ident.Arg));
        Assert.Equal(100u, du.Of(MessageType.SetBrightness).Single().Arg);
    }

    [Fact]
    public void Health_ignores_old_drops_and_reports_new_ones_and_slow_transfers()
    {
        var du = new FakeDu();
        using var conn = new DuConnection(du);
        conn.Start();
        conn.Source = new FrameSlot("pfd");
        du.SendStats("{\"fps\":20,\"decode_ms\":27,\"draw_ms\":0.1,\"rx_ms\":5,\"dropped\":7}");
        Until(() => conn.Stats is not null);
        Assert.Empty(conn.HealthReasons);                           // 7 drops from before this session are not news
        du.SendStats("{\"fps\":20,\"decode_ms\":27,\"draw_ms\":0.1,\"rx_ms\":22,\"dropped\":9}");
        Thread.Sleep(200);
        Assert.Empty(conn.HealthReasons);                           // one bad report is not a warning yet (no flicker)
        du.SendStats("{\"fps\":20,\"decode_ms\":27,\"draw_ms\":0.1,\"rx_ms\":22,\"dropped\":10}");
        Until(() => conn.HealthReasons.Count == 2);
        Assert.Contains("1 dropped", conn.HealthReasons);
        for (var i = 0; i < 2; i++)
        {
            du.SendStats("{\"fps\":20,\"decode_ms\":27,\"draw_ms\":0.1,\"rx_ms\":5,\"dropped\":10}");
        }

        Thread.Sleep(200);
        Assert.NotEmpty(conn.HealthReasons);                        // two good reports: still shown
        du.SendStats("{\"fps\":20,\"decode_ms\":27,\"draw_ms\":0.1,\"rx_ms\":5,\"dropped\":10}");
        Until(() => conn.HealthReasons.Count == 0);                 // the third clears it
    }

    /// <summary>A frame source that counts how often it is looked at: a busy loop shows as thousands of looks.</summary>
    private sealed class CountingSource(string name) : IFrameSource
    {
        public int Looks;

        public string Name { get; } = name;

        public Frame? Latest
        {
            get
            {
                Interlocked.Increment(ref Looks);
                return null;
            }
        }

        public Frame? WaitNewer(uint afterSeq, int timeoutMs)
        {
            Interlocked.Increment(ref Looks);
            Thread.Sleep(timeoutMs);
            return null;
        }
    }

    [Fact]
    public void Layout_changes_while_frames_flow_never_disconnect_or_mix_frames_into_tile_mode()
    {
        var du = new FakeDu();
        using var conn = new DuConnection(du);
        conn.Start();
        Until(() => conn.Info is not null);
        var (a, b, single) = (new FrameSlot("a"), new FrameSlot("b"), new FrameSlot("s"));
        using var stop = new CancellationTokenSource();
        var publisher = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                a.Publish(new byte[] { 1 });
                b.Publish(new byte[] { 2 });
                single.Publish(new byte[] { 3 });
                Thread.Sleep(1);
            }
        });
        publisher.Start();
        var two = new[] { new DuConnection.Tile(0, 0, 16, 16), new DuConnection.Tile(16, 0, 16, 16) };
        for (var round = 0; round < 150; round++)
        {
            switch (round % 3)
            {
                case 0:
                    conn.SetLayout(two);
                    conn.TileSources = [a, b];
                    break;
                case 1:
                    conn.SetLayout([two[0]]);                       // shrinks while tile 2 may be in flight
                    conn.TileSources = [a];
                    break;
                default:
                    conn.SetLayout([]);
                    conn.Source = single;
                    break;
            }

            Thread.Sleep(2);
        }

        stop.Cancel();
        publisher.Join();
        Assert.True(conn.Alive, conn.Error);
        var inTiles = false;
        foreach (var m in du.Received.ToList())
        {
            if (m.Type == MessageType.SetLayout)
            {
                inTiles = m.Payload.Length > 0;
            }

            Assert.False(inTiles && m.Type == MessageType.Frame, "a plain FRAME after a SET_LAYOUT with tiles");
            Assert.False(!inTiles && m.Type == MessageType.Tile, "a TILE outside tile mode");
        }

        Assert.NotEmpty(du.Of(MessageType.Tile));
        Assert.NotEmpty(du.Of(MessageType.Frame));
    }

    [Fact]
    public void A_tile_without_a_picture_is_waited_on_not_polled()
    {
        var du = new FakeDu();
        using var conn = new DuConnection(du);
        conn.Start();
        Until(() => conn.Info is not null);
        var quiet = new CountingSource("quiet");
        conn.SetLayout([new DuConnection.Tile(0, 0, 16, 16), new DuConnection.Tile(16, 0, 16, 16)]);
        conn.TileSources = [quiet, null];                         // the last tile in the rotation has no display
        Thread.Sleep(500);
        Assert.InRange(quiet.Looks, 1, 400);                        // a busy loop looks hundreds of thousands of times
    }

    [Fact]
    public void Frames_larger_than_the_du_takes_are_skipped()
    {
        var du = new FakeDu();
        using var conn = new DuConnection(du);
        conn.Start();
        Until(() => conn.Info is not null);
        Assert.Equal(512 * 1024, conn.MaxFrame);                     // firmware that does not say: the old limit
        var slot = new FrameSlot("pfd");
        conn.Source = slot;
        slot.Publish(new byte[600 * 1024]);
        Until(() => conn.Oversize == 1);
        slot.Publish(new byte[] { 9 });
        Until(() => du.Of(MessageType.Frame).Count == 1);
        Assert.Equal(1, du.Of(MessageType.Frame)[0].Payload.Length);

        du.SendInfo("{\"fw\":\"0.6.0\",\"panel\":[1920,1080],\"mode\":4,\"max_frame\":1048576,\"caps\":[\"mode\",\"tiles\"]}");
        Until(() => conn.MaxFrame == 1024 * 1024);
    }

    [Fact]
    public void Odd_json_from_a_du_is_read_as_defaults()
    {
        var du = new FakeDu();
        using var conn = new DuConnection(du);
        conn.Start();
        Until(() => conn.Info is not null);
        du.SendInfo("{\"fw\":5,\"panel\":[\"a\",null],\"mode\":null,\"uptime_s\":\"x\",\"caps\":\"tiles\"}");
        du.SendStats("{\"fps\":\"fast\",\"dropped\":null,\"rx_ms\":[]}");
        Until(() => conn.Stats is not null && conn.Info?.Firmware == "");
        Assert.True(conn.Alive, conn.Error);
        Assert.Null(conn.Mode);
        Assert.False(conn.SupportsTiles);
        Assert.Equal(0, conn.Info!.PanelWidth);
    }

    [Fact]
    public void Firmware_update_transfers_the_image_and_checks_the_result()
    {
        var image = new byte[100_000];
        Random.Shared.NextBytes(image);
        var du = new FakeDu();
        using var conn = new DuConnection(du);
        conn.Start();
        conn.BeginUpdate(image);
        Until(() => conn.Ota.State is OtaState.Ok or OtaState.Error, 5000);
        Assert.Equal(OtaState.Ok, conn.Ota.State);
        Assert.Equal(image, du.Flashed.ToArray());
        Assert.Equal(4, du.Of(MessageType.OtaData).Count);         // 32 KiB chunks

        var failing = new FakeDu { OtaFailAtChunk = 1 };
        using var conn2 = new DuConnection(failing);
        conn2.Start();
        conn2.BeginUpdate(image);
        Until(() => conn2.Ota.State is OtaState.Ok or OtaState.Error, 5000);
        Assert.Equal(OtaState.Error, conn2.Ota.State);
        Assert.Contains("flash write failed", conn2.Ota.Message);
    }
}
