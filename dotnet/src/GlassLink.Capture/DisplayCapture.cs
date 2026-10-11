using GlassLink.Core.Config;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlassLink.Capture.Windows;
using GlassLink.Core.Du;

namespace GlassLink.Capture;

/// <summary>Capture settings: the "capture" section of config.json, with a display's own overrides on top.</summary>
public sealed record CaptureSettings(double Fps, double IdleFps, double IdleAfterSeconds, int Quality, bool Subsample420, int MaxSize)
{
    public static CaptureSettings From(JsonObject? capture, JsonObject? display)
    {
        double Num(string key, double fallback) =>
            display?[key] is { } d && d.GetValueKind() == JsonValueKind.Number ? d.AsDouble()
            : capture?[key] is { } c && c.GetValueKind() == JsonValueKind.Number ? c.AsDouble() : fallback;
        var subsampling = capture?["subsampling"] is { } s && s.GetValueKind() == JsonValueKind.String ? s.GetValue<string>() : "420";
        return new CaptureSettings(Num("fps", 40), Num("idle_fps", 1), Num("idle_after_s", 5), (int)Num("quality", 85), subsampling != "444", (int)Num("max_size", 0));
    }
}

public sealed record DisplayCounters(long Received, long Skipped, long Unchanged, long Published, double EncodeMs, int JpegBytes);

/// <summary>How a display's window is found and treated: the match, a frame the sim draws inside the window (X-Plane
/// draws one of 15 px around its pop-outs: it is cropped off, and the window is made that much larger so the picture
/// keeps the configured size) and whether the window is kept out of Alt+Tab and the taskbar.</summary>
public sealed record WindowRule(WindowMatch Match, int Frame = 0, bool ToolWindow = false);

/// <summary>
/// One display: finds its window, sizes and parks it, captures it, and publishes every picture that differs from the
/// last one as a JPEG. A display nobody is using costs next to nothing: its frames are refused before they are copied
/// from the GPU, except for a preview picture now and then.
/// </summary>
public sealed class DisplayCapture : IDisposable
{
    private readonly JsonObject _display;
    private readonly CaptureSettings _settings;
    private readonly FrameSlot _slot;
    private readonly Func<bool> _inUse;
    private readonly Action<string>? _log;
    private readonly Func<WindowRule?> _alternative;
    private readonly JpegEncoder _encoder;
    private readonly Timer _watch;
    private readonly object _gate = new();
    // The JPEG of a picture only a band reads is made when someone asks for it, on the asker's thread and under the
    // slot's lock (which the capture's _gate must never wait behind): so it has an encoder and a lock of its own.
    private readonly object _lazyGate = new();
    private JpegEncoder? _lazyEncoder;
    private WindowCapture? _capture;
    private WindowRule? _rule;
    private List<Rect> _monitors = [];
    private volatile int _crop;
    private byte[] _previous = [];
    private byte[] _current = [];
    private (int W, int H) _size;
    private long _lastInUse = long.MinValue / 2;
    private long _lastTaken;
    private long _received, _skipped, _unchanged, _published;
    private double _encodeMs;
    private int _jpegBytes;
    private bool _disposed;

    public string Name { get; }

    public WindowInfo? Window { get; private set; }

    /// <summary>True while the display's window is there and being captured.</summary>
    public bool HasWindow => _capture is not null && Window is not null;

    public string Error { get; private set; } = "";

    public CaptureSettings Settings => _settings;

    /// <summary>The rate frames are taken at right now: full while in use, the preview rate otherwise.</summary>
    public double CurrentFps => Stopwatch.GetElapsedTime(_lastInUse).TotalSeconds > _settings.IdleAfterSeconds ? _settings.IdleFps : _settings.Fps;

    public DisplayCounters Counters => new(_received, _skipped, _unchanged, _published, _encodeMs, _jpegBytes);

    /// <param name="alternative">Another window this display may be, asked while the configured one is not there (the
    /// X-Plane aircraft profile's pop-out for it); null for none.</param>
    public DisplayCapture(string name, JsonObject display, JsonObject? captureSection, FrameSlot slot, Func<bool> inUse, Action<string>? log = null,
        Func<WindowRule?>? alternative = null)
    {
        (Name, _display, _slot, _inUse, _log) = (name, display, slot, inUse, log);
        _alternative = alternative ?? (() => null);
        _slot.Live = false;                                  // until the window is found (#81)
        _settings = CaptureSettings.From(captureSection, display);
        _encoder = new JpegEncoder(_settings.Quality, _settings.Subsample420);
        _watch = new Timer(_ => Watch(), null, 0, 1000);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _watch.Dispose();
            Retire();
            _encoder.Dispose();                              // a frame still on its way checks _disposed under the lock first
        }

        DisposeRetired();
        lock (_lazyGate)
        {
            _lazyEncoder?.Dispose();
            _lazyEncoder = null;
        }
    }

    /// <summary>Once a second: is the window there, is the capture running? Skipped while the last look still runs: a
    /// look that hangs must not have a new thread queue up behind it every second (11 Oct 2026: 3,800 of them, and
    /// with no thread left the status page and every request hung).</summary>
    private void Watch()
    {
        if (!Monitor.TryEnter(_gate))
        {
            return;
        }

        try
        {
            WatchLocked();
        }
        finally
        {
            Monitor.Exit(_gate);
        }

        DisposeRetired();
    }

    private void WatchLocked()
    {
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                WindowCapture.CheckDevice();                 // a lost device makes every capture stale (review C1)
                if (_capture is { Stale: true } stale)
                {
                    Stop($"capture of '{Name}' restarts: {(stale.FailReason.Length > 0 ? stale.FailReason : "the graphics device was reset")}");   // #32
                }

                if (_capture is not null && (Window is null || !WindowFinder.IsAlive(Window.Handle)
                    || WindowFinder.Describe(Window.Handle) is not { } now || _rule is null || !_rule.Match.Matches(now)))
                {
                    Stop($"window of '{Name}' is gone");     // closed, hidden by the sim before it destroys it, or renamed
                }

                if (_capture is null && FindWindow() is ({ } found, { } rule))
                {
                    (_rule, _crop) = (rule, rule.Frame);
                    Window = ApplyGeometry(found, rule);
                    var capture = _capture = new WindowCapture(Window.Handle, OnPixels, _settings.Fps) { WantFrame = WantFrame };
                    capture.Closed += () => ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            lock (_gate)
                            {
                                if (!_disposed && ReferenceEquals(_capture, capture))   // not a late Closed from a capture Watch has replaced already
                                {
                                    Stop($"window of '{Name}' was closed");
                                }
                            }

                            DisposeRetired();
                        }
                        catch (Exception ex)                 // on a pool thread an exception would end the DMC with its captures open
                        {
                            _log?.Invoke($"[{Name}] stopping the capture: {ex.GetType().Name}: {ex.Message}");
                        }
                    });
                    Error = "";
                    _slot.Live = true;
                    _log?.Invoke($"[{Name}] capturing 0x{Window.Handle:x} '{Window.Title}' ({Window.Process}, client {Window.Client.Width}x{Window.Client.Height})");
                }
                else if (_capture is null)
                {
                    Error = WindowMatch.From(_display["match"] as JsonObject).Problem ?? "window not found";
                    _slot.Live = false;
                }

                // a screen added or changed later must not show a parked pop-out: parked again by the same rule
                var monitors = WindowFinder.Monitors();
                if (_capture is not null && Window is not null && _monitors.Count > 0 && !monitors.SequenceEqual(_monitors)
                    && Pair(_display["position"]) is { } park)
                {
                    WindowFinder.Move(Window.Handle, park.A, park.B);
                    Window = WindowFinder.Describe(Window.Handle) ?? Window;
                    _log?.Invoke($"[{Name}] the screens changed: window parked again at {Window.Window.Left},{Window.Window.Top}");
                }

                _monitors = monitors;
            }
            catch (Exception ex)                                // a timer callback must never take the process down
            {
                Error = $"{ex.GetType().Name}: {ex.Message}";
                _log?.Invoke($"[{Name}] {Error}");
                Retire();
                Window = null;                               // e.g. the window was closed while the capture was being set up
            }
        }
    }

    // Captures that were stopped under _gate, disposed once it is released. Disposing one under _gate deadlocked (11 Oct
    // 2026, the sim quitting): the session's Close waits for the frame callback that is running, and that callback's
    // encode step waits for _gate (OnPixels' action).
    private readonly List<WindowCapture> _retired = [];

    /// <summary>Under _gate: the capture is no longer this display's; disposed by <see cref="DisposeRetired"/>.</summary>
    private void Retire()
    {
        if (_capture is not null)
        {
            _retired.Add(_capture);
            _capture = null;
        }
    }

    /// <summary>Outside _gate: closes the captures stopped meanwhile.</summary>
    private void DisposeRetired()
    {
        List<WindowCapture> retired;
        lock (_gate)
        {
            if (_retired.Count == 0)
            {
                return;
            }

            retired = [.. _retired];
            _retired.Clear();
        }

        foreach (var capture in retired)
        {
            capture.Dispose();
        }
    }

    private void Stop(string why)
    {
        if (_capture is null)
        {
            return;
        }

        Retire();
        Window = null;
        _rule = null;
        Error = "window not found";
        _slot.Live = false;
        _log?.Invoke($"[{Name}] {why}");
    }

    /// <summary>The configured window first; else the alternative one (a pop-out of another sim).</summary>
    private (WindowInfo Window, WindowRule Rule)? FindWindow()
    {
        var configured = new WindowRule(WindowMatch.From(_display["match"] as JsonObject),
            _display["crop"] is { } c && c.GetValueKind() == JsonValueKind.Number ? Math.Max(0, (int)c.AsDouble()) : 0,
            _display["tool_window"] is { } t && t.GetValueKind() == JsonValueKind.True);
        foreach (var rule in new[] { configured, _alternative() })
        {
            if (rule is not null && WindowFinder.Find(rule.Match) is { } found)
            {
                return (found, rule);
            }
        }

        return null;
    }

    private WindowInfo ApplyGeometry(WindowInfo w, WindowRule rule)
    {
        if (rule.ToolWindow && WindowFinder.SetToolWindow(w.Handle))
        {
            _log?.Invoke($"[{Name}] window kept out of Alt+Tab and the taskbar");
            w = WindowFinder.Describe(w.Handle) ?? w;       // the frame may have changed with the style
        }

        var size = Pair(_display["client_size"]) is { } configured ? (A: configured.A + 2 * rule.Frame, B: configured.B + 2 * rule.Frame) : ((int A, int B)?)null;
        var position = Pair(_display["position"]);
        if (size is { } s && (w.Client.Width, w.Client.Height) != s)
        {
            WindowFinder.SetClientSize(w, s.A, s.B, position?.A, position?.B);
        }
        else if (position is { } p && (w.Window.Left, w.Window.Top) != p)
        {
            WindowFinder.Move(w.Handle, p.A, p.B);
        }

        var noActivate = _display["no_activate"] is not { } n || n.GetValueKind() != JsonValueKind.False;
        if (noActivate && w.Title.StartsWith("GlassLink:", StringComparison.Ordinal) && WindowFinder.SetNoActivate(w.Handle))
        {
            _log?.Invoke($"[{Name}] window marked as never-activated (keeps the focus on the sim's main window)");
        }

        return WindowFinder.Describe(w.Handle) ?? w;
    }

    private static (int A, int B)? Pair(JsonNode? node) => node.Pair();

    /// <summary>Full rate while something shows this display (and a few seconds after), a preview picture otherwise.</summary>
    private bool WantFrame()
    {
        _received++;
        var now = Stopwatch.GetTimestamp();
        if (_inUse())
        {
            _lastInUse = now;
        }

        var idle = Stopwatch.GetElapsedTime(_lastInUse, now).TotalSeconds > _settings.IdleAfterSeconds;
        if (idle && Stopwatch.GetElapsedTime(_lastTaken, now).TotalSeconds < 1.0 / Math.Max(_settings.IdleFps, 0.05))
        {
            _skipped++;
            return false;
        }

        _lastTaken = now;
        return true;
    }

    /// <summary>Under the GPU lock: compare with the last picture and take a copy only if it differs.</summary>
    private Action? OnPixels(CapturedPixels pixels)
    {
        var crop = _crop;
        if (pixels.Width <= 2 * crop || pixels.Height <= 2 * crop)
        {
            crop = 0;
        }

        var (w, h) = (pixels.Width - 2 * crop, pixels.Height - 2 * crop);
        var bytes = w * h * 4;
        if (_size != (w, h))
        {
            (_previous, _current, _size) = (new byte[bytes], new byte[bytes], (w, h));
            _previous.AsSpan().Fill(1);                        // cannot equal a real picture: alpha is 255
        }

        var source = pixels.Span;
        if (crop == 0 && pixels.Stride == w * 4)
        {
            if (source[..bytes].SequenceEqual(_previous))
            {
                _unchanged++;
                return null;
            }

            source[..bytes].CopyTo(_current);
        }
        else
        {
            CopyInside(source, pixels.Stride, crop, w, h, _current);
            if (_current.AsSpan().SequenceEqual(_previous))
            {
                _unchanged++;
                return null;
            }
        }

        (_previous, _current) = (_current, _previous);
        var picture = _previous;
        return () =>
        {
            lock (_gate)                                     // Dispose (settings saved, display removed) waits for this encode
            {
                if (_disposed)
                {
                    return;
                }

                try
                {
                    EncodeAndPublish(picture, w, h);
                }
                catch (Exception ex)
                {
                    // the picture was not published, so it must not be the one the next frames are compared with: the
                    // same picture again would count as unchanged until the instrument moves (review C1)
                    picture.AsSpan().Fill(1);
                    if (ex.Message != _encodeError)
                    {
                        _encodeError = ex.Message;
                        _log?.Invoke($"[{Name}] encode failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        };
    }

    private string _encodeError = "";

    /// <summary>The width x height BGRA picture inside a frame of <paramref name="crop"/> pixels, row by row, into a
    /// packed buffer (stride width x 4).</summary>
    public static void CopyInside(ReadOnlySpan<byte> source, int stride, int crop, int width, int height, Span<byte> destination)
    {
        for (var y = 0; y < height; y++)
        {
            source.Slice((y + crop) * stride + crop * 4, width * 4).CopyTo(destination[(y * width * 4)..]);
        }
    }

    private void EncodeAndPublish(byte[] picture, int w, int h)
    {
        var (pixels, pw, ph) = Downscale.Fit(picture, w, h, w * 4, _settings.MaxSize) ?? (picture, w, h);      // max_size: a smaller picture, sent as such
        if (_slot.WantsPixels)
        {
            // a band on a DU reads the raw pixels: the JPEG is made only if someone asks (a DU showing the display on
            // its own, a viewer, a snapshot), not for every picture (review U1)
            _slot.PublishLazy(EncodeLater, pw, ph, pixels.AsSpan(0, pw * ph * 4));
        }
        else
        {
            var started = Stopwatch.GetTimestamp();
            var jpeg = _encoder.Encode(pixels, pw, ph, pw * 4);
            Encoded(started, jpeg.Length);
            _slot.Publish(jpeg, pw, ph);
        }

        _published++;
    }

    /// <summary>The JPEG of a picture only a band read so far: under the slot's lock, with the pixels it keeps.</summary>
    private ReadOnlyMemory<byte> EncodeLater(ReadOnlySpan<byte> pixels, (int Width, int Height) size)
    {
        lock (_lazyGate)
        {
            if (_disposed)
            {
                return default;                              // the display went away: nothing to send
            }

            try
            {
                var started = Stopwatch.GetTimestamp();
                _lazyEncoder ??= new JpegEncoder(_settings.Quality, _settings.Subsample420);
                var jpeg = _lazyEncoder.Encode(pixels, size.Width, size.Height, size.Width * 4);
                Encoded(started, jpeg.Length);
                return jpeg;
            }
            catch (Exception ex)                             // on the asker's thread (a DU's, a viewer's): an empty picture, not a crash
            {
                if (ex.Message != _encodeError)
                {
                    _encodeError = ex.Message;
                    _log?.Invoke($"[{Name}] encode failed: {ex.GetType().Name}: {ex.Message}");
                }

                return default;
            }
        }
    }

    private void Encoded(long started, int bytes)
    {
        _encodeMs = _encodeMs * 0.9 + Stopwatch.GetElapsedTime(started).TotalMilliseconds * 0.1;
        _jpegBytes = bytes;
    }
}
