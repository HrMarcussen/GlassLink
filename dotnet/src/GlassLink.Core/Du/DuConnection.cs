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
            (w, h) = (p[0].GetInt32(), p[1].GetInt32());
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

    public long FramesSent { get; private set; }

    public long BytesSent { get; private set; }

    public double? PingMs { get; private set; }

    public OtaStatus Ota { get; private set; } = new(OtaState.Idle, 0, "");

    /// <summary>Reasons from the last health check; empty when the DU keeps up.</summary>
    public IReadOnlyList<string> HealthReasons { get; private set; } = [];

    public int Resyncs => _reader.Resyncs;

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
                if (_readyPending && Source is { } src)
                {
                    if (ServeReady(src, 20))
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

        foreach (var m in _reader.Feed(chunk))
        {
            OnMessage(m);
        }
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
                Info = DuInfo.From(m.Json());
                _log?.Invoke($"DU {Short} info: {m.Text()}");
                Send(MessageType.SetAssigned, arg: Source is null ? 0u : 1u);
                break;
            case MessageType.Stats:
                Stats = DuStats.From(m.Json());
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
