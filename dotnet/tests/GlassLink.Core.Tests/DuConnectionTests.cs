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
                    _toHost.Add(Wire.Pack(MessageType.Info, Encoding.UTF8.GetBytes(
                        "{\"fw\":\"0.5.0\",\"build\":\"abc1234\",\"hw\":\"fake\",\"panel\":[768,768],\"slot\":\"ota_0\",\"uptime_s\":5}")));
                    break;
                case MessageType.Frame:
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
        Until(() => conn.HealthReasons.Count == 2);
        Assert.Contains("2 dropped", conn.HealthReasons);
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
