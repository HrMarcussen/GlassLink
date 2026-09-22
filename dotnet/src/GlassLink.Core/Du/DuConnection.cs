using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using GlassLink.Core.Protocol;
using GlassLink.Core.Usb;

namespace GlassLink.Core.Du;

/// <summary>What a DU says about itself (INFO message).</summary>
public sealed record DuInfo(string Firmware, string Build, string Hardware, int PanelWidth, int PanelHeight, string Slot, long UptimeSeconds)
{
    public static DuInfo? From(JsonElement? json)
    {
        if (json is not { ValueKind: JsonValueKind.Object } j)
        {
            return null;
        }

        string Str(string name) => j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var (w, h) = (0, 0);
        if (j.TryGetProperty("panel", out var p) && p.ValueKind == JsonValueKind.Array && p.GetArrayLength() == 2)
        {
            (w, h) = (p[0].TryGetInt32(out var pw) ? pw : 0, p[1].TryGetInt32(out var ph) ? ph : 0);
        }

        var up = j.TryGetProperty("uptime_s", out var u) && u.TryGetInt64(out var s) ? s : 0;
        return new DuInfo(Str("fw"), Str("build"), Str("hw"), w, h, Str("slot"), up);
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

        double Num(string name) => j.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : 0;
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
    private readonly MessageReader _reader = new();
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sourceGate = new();
    private IFrameSource?[] _tileSources = [];
    private IFrameSource?[] _tileLast = [];
    private uint[] _tileSeqSent = [];
    private int _tileNext;
    private bool _cards;
    private IFrameSource? _source;
    private IFrameSource? _lastSource;
    private uint _lastSeqSent;
    private bool _readyPending;
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

    public int Resyncs => _reader.Resyncs;

    /// <summary>One tile of a layout: a rectangle of the DU's screen that shows one display.</summary>
    public sealed record Tile(int X, int Y, int Width, int Height);

    /// <summary>The DU's layout (a screen with several displays on it); empty for a single-display DU.</summary>
    public IReadOnlyList<Tile> Layout { get; private set; } = [];

    /// <summary>True when the DU's firmware knows SET_LAYOUT and TILE (INFO caps).</summary>
    public bool SupportsTiles => Has("tiles");

    /// <summary>True when the DU's firmware knows SET_MODE.</summary>
    public bool SupportsMode => Has("mode");

    /// <summary>The HDMI mode the DU runs (INFO), null before the first INFO.</summary>
    public int? Mode => InfoJson is { } j && j.TryGetProperty("mode", out var m) && m.TryGetInt32(out var v) ? v : null;

    /// <summary>Test cards instead of pictures on the tiles (the last SET_LAYOUT).</summary>
    public bool Cards => _cards;

    private bool Has(string cap) => InfoJson is { } j && j.TryGetProperty("caps", out var caps) && caps.ValueKind == JsonValueKind.Array
                                    && caps.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == cap);

    /// <summary>Gives the DU a layout (or takes it away with an empty list) and, with <paramref name="cards"/>, shows
    /// the tiles as test cards for lining them up with the panel's cutouts. The sources per tile follow separately
    /// (<see cref="TileSources"/>). Sent again after every INFO, so a DU that restarts gets it back.</summary>
    public void SetLayout(IReadOnlyList<Tile> tiles, bool cards = false)
    {
        lock (_sourceGate)
        {
            Layout = tiles;
            _cards = cards;
            if (_tileSources.Length != tiles.Count)
            {
                _tileSources = new IFrameSource?[tiles.Count];
                _tileSeqSent = new uint[tiles.Count];
                _tileLast = new IFrameSource?[tiles.Count];
            }
        }

        SendLayout();
    }

    /// <summary>The display of every tile (null = nothing there yet), in layout order.</summary>
    public IFrameSource?[] TileSources
    {
        get
        {
            lock (_sourceGate)
            {
                return (IFrameSource?[])_tileSources.Clone();
            }
        }

        set
        {
            lock (_sourceGate)
            {
                for (var i = 0; i < _tileSources.Length && i < value.Length; i++)
                {
                    _tileSources[i] = value[i];
                }
            }

            Send(MessageType.SetAssigned, arg: value.Any(v => v is not null) ? 1u : 0u);
        }
    }

    private void SendLayout()
    {
        Tile[] tiles;
        bool cards;
        lock (_sourceGate)
        {
            (tiles, cards) = ([.. Layout], _cards);
        }

        var payload = new byte[tiles.Length * 8];
        for (var i = 0; i < tiles.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(i * 8), (ushort)tiles[i].X);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(i * 8 + 2), (ushort)tiles[i].Y);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(i * 8 + 4), (ushort)tiles[i].Width);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(i * 8 + 6), (ushort)tiles[i].Height);
        }

        Send(MessageType.SetLayout, payload, arg: cards ? 1u : 0u);
    }

    private bool HasSource
    {
        get
        {
            lock (_sourceGate)
            {
                return _source is not null || _tileSources.Any(s => s is not null);
            }
        }
    }

    /// <summary>The display this DU shows; null = not assigned (the DU shows its own NOT ASSIGNED screen).</summary>
    public IFrameSource? Source
    {
        get
        {
            lock (_sourceGate)
            {
                return _source;
            }
        }

        set
        {
            lock (_sourceGate)
            {
                _source = value;
            }

            Send(MessageType.SetAssigned, arg: value is null ? 0u : 1u);
        }
    }

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
            _transport.Write(Wire.Pack(type, payload, seq, arg));
        }
        catch (IOException ex)
        {
            Fail(ex.Message);
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
        _stop.Cancel();
        if (_thread.IsAlive)
        {
            _thread.Join(2000);
        }

        _transport.Dispose();
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

                // With a READY pending, wait for the next picture rather than for the DU (it has nothing to say until it
                // gets one); still drain what the DU sent. Without one, the DU is the only thing to wait for.
                if (_readyPending && HasSource)
                {
                    if (Layout.Count > 0 ? ServeTile(5) : Source is { } src && ServeReady(src, 5))     // short, so that a PONG or STATS is not left waiting
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

    /// <summary>Tiles take turns: after a READY the first tile (from the one after the last sent) with a newer frame
    /// goes; if none has one, one tile is waited on briefly and the loop comes round again.</summary>
    private bool ServeTile(int waitMs)
    {
        IFrameSource?[] sources;
        lock (_sourceGate)
        {
            sources = (IFrameSource?[])_tileSources.Clone();
        }

        var n = sources.Length;
        for (var k = 1; k <= n; k++)
        {
            var i = (_tileNext + k) % n;
            if (sources[i] is not { } src)
            {
                continue;
            }

            if (!ReferenceEquals(src, _tileLast[i]))
            {
                (_tileLast[i], _tileSeqSent[i]) = (src, 0);  // another display on this tile: its sequence numbers are unrelated
            }

            var frame = src.Latest;
            if (frame is null || frame.Seq <= _tileSeqSent[i])
            {
                if (k < n)
                {
                    continue;                                // look at the other tiles first
                }

                frame = src.WaitNewer(_tileSeqSent[i], waitMs);
                if (frame is null)
                {
                    return false;
                }
            }

            Send(MessageType.Tile, frame.Jpeg.Span, seq: frame.Seq, arg: (uint)i);
            FramesSent++;
            BytesSent += frame.Jpeg.Length;
            _tileSeqSent[i] = frame.Seq;
            _tileNext = i;
            _readyPending = false;
            return true;
        }

        return false;
    }

    private bool ServeReady(IFrameSource src, int waitMs)
    {
        if (!ReferenceEquals(src, _lastSource))
        {
            _lastSource = src;               // another display: its sequence numbers are unrelated, send its newest frame
            _lastSeqSent = 0;
        }

        var frame = src.Latest;
        if (frame is null || frame.Seq <= _lastSeqSent)
        {
            frame = src.WaitNewer(_lastSeqSent, waitMs);
            if (frame is null)
            {
                return false;
            }
        }

        Send(MessageType.Frame, frame.Jpeg.Span, seq: frame.Seq);
        FramesSent++;
        BytesSent += frame.Jpeg.Length;
        _lastSeqSent = frame.Seq;
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
                _log?.Invoke($"DU {Short} info: {m.Text()}");
                if (Layout.Count > 0)
                {
                    SendLayout();                            // a DU that restarted has forgotten it
                }

                Send(MessageType.SetAssigned, arg: HasSource ? 1u : 0u);
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
        if (Source is not null)
        {
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

        if (reasons.Count > 0 && HealthReasons.Count == 0)
        {
            _log?.Invoke($"DU {Short} stall: {string.Join("; ", reasons)} (sending {sentRate:0.#} fps)");
        }

        HealthReasons = reasons;
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
