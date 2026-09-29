using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using GlassLink.Core.Protocol;
using GlassLink.Core.Usb;

namespace GlassLink.Core.Du;

/// <summary>Reading a DU's JSON: a value of the wrong kind is a default, never an exception (#29).</summary>
internal static class DuJson
{
    public static double Num(JsonElement j, string name, double fallback = 0) =>
        j.ValueKind == JsonValueKind.Object && j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : fallback;

    public static string Str(JsonElement j, string name) =>
        j.ValueKind == JsonValueKind.Object && j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static int Int(JsonElement v) => v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
}

/// <summary>What a DU says about itself (INFO message).</summary>
public sealed record DuInfo(string Firmware, string Build, string Hardware, int PanelWidth, int PanelHeight, string Slot, long UptimeSeconds)
{
    public static DuInfo? From(JsonElement? json)
    {
        if (json is not { ValueKind: JsonValueKind.Object } j)
        {
            return null;
        }

        var (w, h) = (0, 0);
        if (j.TryGetProperty("panel", out var p) && p.ValueKind == JsonValueKind.Array && p.GetArrayLength() == 2)
        {
            (w, h) = (DuJson.Int(p[0]), DuJson.Int(p[1]));
        }

        return new DuInfo(DuJson.Str(j, "fw"), DuJson.Str(j, "build"), DuJson.Str(j, "hw"), w, h, DuJson.Str(j, "slot"), (long)DuJson.Num(j, "uptime_s"));
    }
}

/// <summary>What a DU measures (STATS message, every 2 s).</summary>
public sealed record DuStats(double Fps, double DecodeMs, double DrawMs, double RxMs, long Dropped, bool Ident)
{
    public static DuStats? From(JsonElement? json)
    {
        if (json is not { ValueKind: JsonValueKind.Object } j)
        {
            return null;
        }

        double Num(string name) => DuJson.Num(j, name);
        return new DuStats(Num("fps"), Num("decode_ms"), Num("draw_ms"), Num("rx_ms"), (long)Num("dropped"), Num("ident") != 0);
    }
}

public enum OtaState
{
    Idle,
    Running,
    Ok,
    Error,
}

public sealed record OtaStatus(OtaState State, double Progress, string Message);

/// <summary>
/// The link to one DU: a thread that answers every READY with the newest frame of the assigned display (one frame in
/// flight, always the newest: the DU is the clock), carries commands, keeps INFO and STATS, watches the DU's health
/// and installs firmware. A straight port of the Python reference (glasslink/modules.py, ModuleWorker).
/// </summary>
public sealed class DuConnection : IDisposable
{
    private readonly IDuTransport _transport;
    private readonly Action<string>? _log;
    private readonly MessageReader _reader = new(fromDu: true);
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stop = new();
    /// <summary>What the DU is to show: one display, or a layout with a display per tile. Replaced as a whole under
    /// <see cref="_screenGate"/>, never changed in place: the DU's thread works from one snapshot per READY (#25).</summary>
    private sealed record Screen(IFrameSource? Single, Tile[] Layout, IFrameSource?[] Sources, bool Cards)
    {
        public static readonly Screen Empty = new(null, [], [], false);

        public bool HasSource => Single is not null || Sources.Any(s => s is not null);
    }

    private readonly object _screenGate = new();     // screen changes, and the frame sends that depend on them
    private volatile Screen _screen = Screen.Empty;

    // the DU thread's own bookkeeping (no other thread touches these)
    private Screen? _served;
    private IFrameSource?[] _tileLast = [];
    private uint[] _tileSeqSent = [];
    private int _tileNext;
    private IFrameSource? _lastSource;
    private uint _lastSeqSent;
    private long _oversizeLogged;
    private int _badChecks, _goodChecks;
    private bool _readyPending;
    private long _assignedSentAt;
    private byte[]? _otaImage;
    private uint? _otaAcked;
    private int? _otaResult;
    private uint _pingNonce;
    private long _pingSentAt;
    private long _histFrames;
    private long _histTicks = Stopwatch.GetTimestamp();
    private long? _histDropped;

    public DuConnection(IDuTransport transport, Action<string>? log = null)
    {
        _transport = transport;
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = $"du-{Short}" };
    }

    public string Serial => _transport.Serial;

    public string Short => Serial[..Math.Min(8, Serial.Length)];

    public bool Alive { get; private set; } = true;

    public string Error { get; private set; } = "";

    public DuInfo? Info { get; private set; }

    public DuStats? Stats { get; private set; }

    /// <summary>INFO and STATS as the DU sent them, for the status page (it shows fields this class does not need).</summary>
    public JsonElement? InfoJson { get; private set; }

    public JsonElement? StatsJson { get; private set; }

    public DateTime ConnectedAt { get; } = DateTime.UtcNow;

    public DateTime LastMessageAt { get; private set; } = DateTime.UtcNow;

    public string Description => _transport.Description;

    public long FramesSent { get; private set; }

    public long BytesSent { get; private set; }

    public double? PingMs { get; private set; }

    public OtaStatus Ota { get; private set; } = new(OtaState.Idle, 0, "");

    /// <summary>Reasons from the last health check; empty when the DU keeps up.</summary>
    public IReadOnlyList<string> HealthReasons { get; private set; } = [];

    /// <summary>Below this a DU counts as too slow when its pictures are waiting for it (#62).</summary>
    public const double MinFps = 20;
    private int _sendsAll, _sendsWaiting;               // pictures sent, and of those the ones that were already waiting

    public int Resyncs => _reader.Resyncs;

    /// <summary>One tile of a layout: a rectangle of the DU's screen that shows one display.</summary>
    public sealed record Tile(int X, int Y, int Width, int Height);

    /// <summary>The DU's layout (a screen with several displays on it); empty for a single-display DU.</summary>
    public IReadOnlyList<Tile> Layout => _screen.Layout;

    /// <summary>The largest JPEG this DU takes (INFO max_frame; 512 KB for firmware that does not say).</summary>
    public int MaxFrame { get; private set; } = 512 * 1024;

    /// <summary>Frames not sent because they were larger than <see cref="MaxFrame"/>.</summary>
    public long Oversize { get; private set; }

    /// <summary>Raised on the DU's thread after every INFO (a new connection, or the DU restarted): the manager brings
    /// the DU's screen mode and layout in line at once instead of at the next scan.</summary>
    public event Action<DuConnection>? InfoReceived;

    /// <summary>True when the DU's firmware knows SET_LAYOUT and TILE (INFO caps).</summary>
    public bool SupportsTiles => Has("tiles");

    /// <summary>True when the DU's firmware knows SET_MODE.</summary>
    public bool SupportsMode => Has("mode");

    /// <summary>The firmware decodes a picture as wide as its screen straight into the frame buffer (0.6.0), so several
    /// displays are best sent as one band instead of a TILE each.</summary>
    public bool SupportsBand => Has("band");

    /// <summary>The HDMI mode the DU runs (INFO), null before the first INFO.</summary>
    public int? Mode => InfoJson is { } j && j.ValueKind == JsonValueKind.Object && j.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out var v) ? v : null;

    /// <summary>Test cards instead of pictures on the tiles (the last SET_LAYOUT).</summary>
    public bool Cards => _screen.Cards;

    private bool Has(string cap) => InfoJson is { } j && j.ValueKind == JsonValueKind.Object && j.TryGetProperty("caps", out var caps) && caps.ValueKind == JsonValueKind.Array
                                    && caps.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == cap);

    /// <summary>Gives the DU a layout (or takes it away with an empty list) and, with <paramref name="cards"/>, shows
    /// the tiles as test cards for lining them up with the panel's cutouts. A layout replaces the single display; the
    /// sources per tile follow separately (<see cref="TileSources"/>), sources of tiles that stay are kept.</summary>
    public void SetLayout(IReadOnlyList<Tile> tiles, bool cards = false)
    {
        lock (_screenGate)
        {
            var old = _screen;
            var sources = new IFrameSource?[tiles.Count];
            Array.Copy(old.Sources, sources, Math.Min(old.Sources.Length, sources.Length));
            _screen = new Screen(tiles.Count > 0 ? null : old.Single, [.. tiles], sources, cards);
            SendLayoutLocked();
        }
    }

    /// <summary>The display of every tile (null = nothing there yet), in layout order.</summary>
    public IFrameSource?[] TileSources
    {
        get => (IFrameSource?[])_screen.Sources.Clone();
        set
        {
            bool before, after;
            lock (_screenGate)
            {
                var old = _screen;
                if (value.Length != old.Layout.Length)
                {
                    _log?.Invoke($"DU {Short}: {value.Length} tile source(s) for a layout of {old.Layout.Length}");
                }

                var sources = new IFrameSource?[old.Layout.Length];
                Array.Copy(value, sources, Math.Min(value.Length, sources.Length));
                _screen = old with { Sources = sources };
                (before, after) = (old.HasSource, _screen.HasSource);
            }

            if (before != after)
            {
                SendAssigned();
            }
        }
    }

    /// <summary>The layout as the DU gets it. Called under <see cref="_screenGate"/>, so no frame chosen for an older
    /// screen can follow it.</summary>
    private void SendLayoutLocked()
    {
        var screen = _screen;
        var payload = new byte[screen.Layout.Length * 8];
        for (var i = 0; i < screen.Layout.Length; i++)
        {
            var t = screen.Layout[i];
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(i * 8), (ushort)t.X);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(i * 8 + 2), (ushort)t.Y);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(i * 8 + 4), (ushort)t.Width);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(i * 8 + 6), (ushort)t.Height);
        }

        Send(MessageType.SetLayout, payload, arg: screen.Cards ? 1u : 0u);
    }

    private bool HasSource => _screen.HasSource;

    /// <summary>The display this DU shows; null = not assigned (the DU shows its own NOT ASSIGNED screen). Ignored by
    /// the DU's thread while a layout is set.</summary>
    public IFrameSource? Source
    {
        get => _screen.Single;
        set
        {
            lock (_screenGate)
            {
                _screen = _screen with { Single = value };
            }

            SendAssigned();
        }
    }

    /// <summary>The DU's name on the status page ("DU1"); it shows it on its own screens (#81).</summary>
    public string Label { get; set; } = "";

    /// <summary>What the DU is to show, with its label and the display's name (#81): 0 nothing assigned, 1 its pictures
    /// come, 2 assigned but its display has no window (the sim is not showing it: the DU says it waits, instead of
    /// keeping an old picture up). Repeated every 2 s, which also tells the DU that a DMC is there.</summary>
    private void SendAssigned()
    {
        var screen = _screen;
        var sources = (screen.Layout.Length > 0 ? screen.Sources : [screen.Single]).OfType<IFrameSource>().ToList();
        var state = sources.Count == 0 ? 0u : sources.Any(s => s.Live) ? 1u : 2u;
        var names = string.Join(" · ", sources.Select(s => s.Title).Distinct());
        _assignedSentAt = Environment.TickCount64;
        Send(MessageType.SetAssigned, Encoding.UTF8.GetBytes($"{Label}\n{names}"), arg: state);
    }

    /// <summary>The DMC is quitting: the DU shows "waiting for the DMC" at once and keeps no picture of this session.</summary>
    public void SayBye() => Send(MessageType.Bye);

    public void Start() => _thread.Start();

    // -- commands (any thread) ---------------------------------------------------------------------
    public void Send(MessageType type, ReadOnlySpan<byte> payload = default, uint seq = 0, uint arg = 0)
    {
        if (!Alive)
        {
            return;
        }

        try
        {
            Span<byte> header = stackalloc byte[Wire.HeaderSize];
            Wire.PackHeader(header, type, payload.Length, seq, arg);
            _transport.Write(header, payload);            // no copy of the JPEG into a new array (#34)
        }
        catch (IOException ex)
        {
            Fail(ex.Message);
        }
        catch (ObjectDisposedException)
        {
            Fail("closed");
        }
    }

    /// <summary>Stamps "IDENT label" on the DU for some seconds; 0 switches it off.</summary>
    public void Ident(string label, int seconds)
    {
        var bytes = Encoding.UTF8.GetBytes(label ?? "");
        Send(MessageType.ShowIdent, bytes.AsSpan(0, Math.Min(bytes.Length, 31)), arg: (uint)Math.Max(0, seconds));
    }

    public void SetBrightness(int percent) => Send(MessageType.SetBrightness, arg: (uint)Math.Clamp(percent, 0, 100));

    public void Ping()
    {
        _pingNonce = unchecked(_pingNonce + 1);
        _pingSentAt = Stopwatch.GetTimestamp();
        Send(MessageType.Ping, arg: _pingNonce);
    }

    public void Reboot() => Send(MessageType.Reboot);

    /// <summary>The DU's HDMI mode (0 768x768, 1 1024x768, 2 800x600, 3 1280x720, 4 1920x1080 at 30 Hz); it restarts into it.</summary>
    public void SetMode(int mode) => Send(MessageType.SetMode, arg: (uint)Math.Clamp(mode, 0, 4));

    /// <summary>Installs a firmware image; runs on the DU's own thread, progress in <see cref="Ota"/>.</summary>
    public void BeginUpdate(byte[] image)
    {
        Ota = new OtaStatus(OtaState.Running, 0, "queued");
        Volatile.Write(ref _otaImage, image);
    }

    public void Dispose()
    {
        Alive = false;                                   // nobody sends from here on (#31)
        _stop.Cancel();
        _transport.Dispose();                            // aborts a write in progress and waits for it before freeing the handle
        if (_thread.IsAlive)
        {
            _thread.Join(2000);
        }

        _stop.Dispose();
    }

    // -- the DU's thread ---------------------------------------------------------------------------
    private void Run()
    {
        _log?.Invoke($"DU {Serial} connected ({_transport.Description})");
        try
        {
            Send(MessageType.GetInfo);
            var infoAsked = Environment.TickCount64;
            while (!_stop.IsCancellationRequested && Alive)
            {
                if (Info is null && Environment.TickCount64 - infoAsked > 2000)
                {
                    // the answer can be lost behind the tail of a stale message from an earlier host session
                    Send(MessageType.GetInfo);
                    infoAsked = Environment.TickCount64;
                }

                if (Interlocked.Exchange(ref _otaImage, null) is { } image)
                {
                    DoUpdate(image);
                    continue;
                }

                if (Info is not null && Environment.TickCount64 - _assignedSentAt >= 2000)
                {
                    SendAssigned();                         // the DU's sign that a DMC is there, and whether its sim is (#81)
                }

                // With a READY pending, wait for the next picture rather than for the DU (it has nothing to say until it
                // gets one); still drain what the DU sent. Without one, the DU is the only thing to wait for.
                var screen = _screen;
                if (_readyPending && screen.HasSource)
                {
                    if (Serve(screen, 5))                     // short, so that a PONG or STATS is not left waiting
                    {
                        continue;
                    }

                    Pump(0);
                }
                else
                {
                    Pump(250);
                }
            }
        }
        catch (IOException ex)
        {
            Fail(ex.Message);
        }
        catch (ObjectDisposedException)
        {
            Fail("closed");                                 // disposed while a write was still timing out
        }
        catch (Exception ex)                                 // a DU's thread must never take the DMC down
        {
            Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Alive = false;
        }
    }

    private void Pump(int timeoutMs)
    {
        if (_transport.ReadChunk(timeoutMs) is not { } chunk)
        {
            return;
        }

        LastMessageAt = DateTime.UtcNow;
        foreach (var m in _reader.Feed(chunk))
        {
            OnMessage(m);
        }
    }

    /// <summary>Sends the next picture for this screen snapshot, if there is one. The per-tile bookkeeping follows the
    /// snapshot, so a layout that shrinks while a frame is chosen can never index past its end (#25).</summary>
    private bool Serve(Screen screen, int waitMs)
    {
        if (!ReferenceEquals(screen, _served))
        {
            if (_served is null || _served.Layout.Length != screen.Layout.Length)
            {
                _tileLast = new IFrameSource?[screen.Layout.Length];
                _tileSeqSent = new uint[screen.Layout.Length];
                _tileNext = 0;
            }

            _served = screen;
        }

        return screen.Layout.Length > 0 ? ServeTile(screen, waitMs) : screen.Single is { } src && ServeReady(screen, src, waitMs);
    }

    /// <summary>Tiles take turns: after a READY the first tile (from the one after the last sent) with a newer frame
    /// goes; if none has one, the first tile with a source is waited on briefly (never a busy loop, #26).</summary>
    private bool ServeTile(Screen screen, int waitMs)
    {
        var n = screen.Layout.Length;
        var waitOn = -1;
        for (var k = 1; k <= n; k++)
        {
            var i = (_tileNext + k) % n;
            if (screen.Sources[i] is not { } src)
            {
                continue;
            }

            if (!ReferenceEquals(src, _tileLast[i]))
            {
                (_tileLast[i], _tileSeqSent[i]) = (src, 0);  // another display on this tile: its sequence numbers are unrelated
            }

            if (src.Latest is { } frame && frame.Seq > _tileSeqSent[i])
            {
                return SendPicture(screen, MessageType.Tile, frame, (uint)i, seq => { _tileSeqSent[i] = seq; _tileNext = i; }, waiting: true);
            }

            if (waitOn < 0)
            {
                waitOn = i;
            }
        }

        if (waitOn < 0)
        {
            Thread.Sleep(waitMs);                             // a layout whose tiles have no display yet
            return false;
        }

        var w = waitOn;
        return screen.Sources[w]!.WaitNewer(_tileSeqSent[w], waitMs) is { } next
               && SendPicture(screen, MessageType.Tile, next, (uint)w, seq => { _tileSeqSent[w] = seq; _tileNext = w; });
    }

    private bool ServeReady(Screen screen, IFrameSource src, int waitMs)
    {
        if (!ReferenceEquals(src, _lastSource))
        {
            _lastSource = src;               // another display: its sequence numbers are unrelated, send its newest frame
            _lastSeqSent = 0;
        }

        var frame = src.Latest;
        var waiting = frame is not null && frame.Seq > _lastSeqSent;
        if (!waiting)
        {
            frame = src.WaitNewer(_lastSeqSent, waitMs);
            if (frame is null)
            {
                return false;
            }
        }

        return SendPicture(screen, MessageType.Frame, frame!, 0, seq => _lastSeqSent = seq, waiting);
    }

    /// <summary>Sends a FRAME or TILE, but only if the screen it was chosen for is still the current one (a new layout
    /// on another thread must not be followed by a picture meant for the old one), and only if the DU can take it.</summary>
    private bool SendPicture(Screen screen, MessageType type, Frame frame, uint arg, Action<uint> sent, bool waiting = false)
    {
        if (frame.Jpeg.Length > MaxFrame)
        {
            sent(frame.Seq);                                 // skip it; the next frame may fit (#16)
            Oversize++;
            if (Environment.TickCount64 - _oversizeLogged > 30_000)
            {
                _oversizeLogged = Environment.TickCount64;
                _log?.Invoke($"DU {Short}: a picture of {frame.Jpeg.Length / 1024} KB is larger than the {MaxFrame / 1024} KB this DU takes: not sent (lower the display's quality or size)");
            }

            return false;
        }

        lock (_screenGate)
        {
            if (!ReferenceEquals(_screen, screen))
            {
                return false;
            }

            Send(type, frame.Jpeg.Span, seq: frame.Seq, arg: arg);
        }

        FramesSent++;
        BytesSent += frame.Jpeg.Length;
        Interlocked.Increment(ref _sendsAll);
        if (waiting)
        {
            Interlocked.Increment(ref _sendsWaiting);          // a newer picture was there when the DU asked: the DU sets the pace
        }
        sent(frame.Seq);
        _readyPending = false;
        return true;
    }

    private void OnMessage(Message m)
    {
        switch (m.Type)
        {
            case MessageType.Ready:
                _readyPending = true;
                break;
            case MessageType.Info:
                InfoJson = m.Json();
                Info = DuInfo.From(InfoJson);
                if (InfoJson is { } info && DuJson.Num(info, "max_frame") is var max and >= 64 * 1024)
                {
                    MaxFrame = (int)Math.Min(max, Wire.MaxPayload);
                }

                _log?.Invoke($"DU {Short} info: {m.Text()}");
                if (SupportsTiles)
                {
                    lock (_screenGate)
                    {
                        SendLayoutLocked();                  // also when empty: ends a layout left from an earlier session (#17)
                    }
                }

                SendAssigned();
                InfoReceived?.Invoke(this);
                break;
            case MessageType.Stats:
                StatsJson = m.Json();
                Stats = DuStats.From(StatsJson);
                HealthCheck();
                break;
            case MessageType.Pong when m.Arg == _pingNonce:
                PingMs = Stopwatch.GetElapsedTime(_pingSentAt).TotalMilliseconds;
                break;
            case MessageType.Log:
                _log?.Invoke($"DU {Short}: {m.Text()}");
                break;
            case MessageType.OtaProgress:
                _otaAcked = m.Arg;
                break;
            case MessageType.OtaResult:
                _otaResult = (int)m.Arg;
                break;
        }
    }

    /// <summary>A still picture is normal; only a DU that falls behind a moving one, slow transfers or decodes, or new
    /// drops are worth a warning. Drops from before this session are not news.</summary>
    private void HealthCheck()
    {
        var now = Stopwatch.GetTimestamp();
        var dt = Math.Max(Stopwatch.GetElapsedTime(_histTicks, now).TotalSeconds, 1e-3);
        var sentRate = (FramesSent - _histFrames) / dt;
        (_histFrames, _histTicks) = (FramesSent, now);
        if (Stats is not { } s)
        {
            return;
        }

        _histDropped ??= s.Dropped;
        var newDrops = s.Dropped - _histDropped.Value;
        _histDropped = s.Dropped;
        var reasons = new List<string>();
        var (all, waiting) = (Interlocked.Exchange(ref _sendsAll, 0), Interlocked.Exchange(ref _sendsWaiting, 0));
        if (HasSource)
        {
            // Pictures were nearly always waiting and the DU still shows fewer than 20 a second: it is the DU that is
            // slow, not the display (a display that changes slowly leaves the DU waiting instead).
            if (all >= 10 && waiting >= all * 0.8 && s.Fps < MinFps)
            {
                reasons.Add($"shows {s.Fps:0} fps: its pictures come faster than it draws them");
            }

            if (s.RxMs > 15)
            {
                reasons.Add($"transfer {s.RxMs:0.#} ms");
            }

            if (s.DecodeMs > 30)
            {
                reasons.Add($"decode {s.DecodeMs:0.#} ms");
            }

            if (s.DrawMs > 30)
            {
                reasons.Add($"draw {s.DrawMs:0.#} ms");
            }

            if (newDrops > 0)
            {
                reasons.Add($"{newDrops} dropped");
            }
        }

        // Two bad checks in a row before a warning, three good ones before it goes: one dropped frame must not make the
        // status page flicker (#11).
        (_badChecks, _goodChecks) = reasons.Count > 0 ? (_badChecks + 1, 0) : (0, _goodChecks + 1);
        if (reasons.Count > 0 && _badChecks >= 2)
        {
            if (HealthReasons.Count == 0)
            {
                _log?.Invoke($"DU {Short} stall: {string.Join("; ", reasons)} (sending {sentRate:0.#} fps)");
            }

            HealthReasons = reasons;
        }
        else if (reasons.Count == 0 && _goodChecks >= 3)
        {
            HealthReasons = [];
        }
    }

    // -- firmware update: stop and wait, because flash writes block the DU ---------------------------
    private void DoUpdate(byte[] image)
    {
        _otaAcked = null;
        _otaResult = null;
        Ota = new OtaStatus(OtaState.Running, 0, "preparing flash");
        _log?.Invoke($"DU {Short}: firmware update started ({image.Length} bytes)");
        try
        {
            Send(MessageType.OtaBegin, arg: (uint)image.Length);
            if (!WaitOta(() => _otaAcked == 0, 40_000))
            {
                throw new IOException("no answer to the update request (firmware too old for updates over USB?)");
            }

            var sent = 0;
            while (sent < image.Length && _otaResult is null)
            {
                var part = image.AsSpan(sent, Math.Min(Wire.OtaChunk, image.Length - sent));
                Send(MessageType.OtaData, part, arg: (uint)sent);
                sent += part.Length;
                var expect = (uint)sent;
                if (!WaitOta(() => _otaAcked == expect, 15_000))
                {
                    throw new IOException($"no acknowledgement at {sent} of {image.Length} bytes");
                }

                Ota = new OtaStatus(OtaState.Running, (double)sent / image.Length, $"{sent / 1024} of {image.Length / 1024} KB");
            }

            if (_otaResult is null)
            {
                Send(MessageType.OtaEnd, arg: Crc32.HashToUInt32(image));
                WaitOta(() => false, 30_000);
            }

            Ota = _otaResult switch
            {
                0 => new OtaStatus(OtaState.Ok, 1, "installed, DU is restarting"),
                null => throw new IOException("no result from the DU"),
                var code => throw new IOException(OtaError(code.Value)),
            };
        }
        catch (IOException ex)
        {
            Ota = new OtaStatus(OtaState.Error, Ota.Progress, ex.Message);
            _log?.Invoke($"DU {Short}: firmware update failed: {ex.Message}");
        }
        finally
        {
            _lastSource = null;              // send a fresh frame afterwards
        }
    }

    private bool WaitOta(Func<bool> done, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline && !_stop.IsCancellationRequested && Alive)
        {
            if (_otaResult is not null || done())
            {
                return true;
            }

            Pump(200);
        }

        return _otaResult is not null || done();
    }

    public static string OtaError(int code) => code switch
    {
        1 => "the DU could not start the update",
        2 => "flash write failed on the DU",
        3 => "the DU rejected the image",
        4 => "checksum mismatch",
        5 => "size mismatch",
        6 => "the DU timed out waiting for data",
        7 => "data arrived out of order",
        _ => $"error code {code}",
    };

    private void Fail(string message)
    {
        if (Alive)
        {
            Alive = false;
            Error = message;
            _log?.Invoke($"DU {Short} disconnected: {message}");
        }
    }
}
