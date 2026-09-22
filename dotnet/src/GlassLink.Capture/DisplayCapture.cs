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

/// <summary>
/// One display: finds its window, sizes and parks it, captures it, and publishes every picture that differs from the
/// last one as a JPEG. A display nobody is using costs next to nothing: its frames are refused before they are copied
/// from the GPU, except for a preview picture now and then. Port of glasslink/display.py.
/// </summary>
public sealed class DisplayCapture : IDisposable
{
    private readonly JsonObject _display;
    private readonly CaptureSettings _settings;
    private readonly FrameSlot _slot;
    private readonly Func<bool> _inUse;
    private readonly Action<string>? _log;
    private readonly JpegEncoder _encoder;
    private readonly Timer _watch;
    private readonly object _gate = new();
    private WindowCapture? _capture;
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

    public DisplayCapture(string name, JsonObject display, JsonObject? captureSection, FrameSlot slot, Func<bool> inUse, Action<string>? log = null)
    {
        (Name, _display, _slot, _inUse, _log) = (name, display, slot, inUse, log);
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
            _capture?.Dispose();
            _capture = null;
            _encoder.Dispose();
        }
    }

    /// <summary>Once a second: is the window there, is the capture running?</summary>
    private void Watch()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (_capture is not null && (Window is null || !WindowFinder.IsAlive(Window.Handle)
                    || WindowFinder.Describe(Window.Handle) is not { } now || !WindowMatch.From(_display["match"] as JsonObject).Matches(now)))
                {
                    Stop($"window of '{Name}' is gone");     // closed, hidden by the sim before it destroys it, or renamed
                }

                if (_capture is null && WindowFinder.Find(WindowMatch.From(_display["match"] as JsonObject)) is { } found)
                {
                    Window = ApplyGeometry(found);
                    var capture = _capture = new WindowCapture(Window.Handle, OnPixels, _settings.Fps) { WantFrame = WantFrame };
                    capture.Closed += () => ThreadPool.QueueUserWorkItem(_ =>
                    {
                        lock (_gate)
                        {
                            if (ReferenceEquals(_capture, capture))   // not a late Closed from a capture Watch has replaced already
                            {
                                Stop($"window of '{Name}' was closed");
                            }
                        }
                    });
                    Error = "";
                    _log?.Invoke($"[{Name}] capturing 0x{Window.Handle:x} '{Window.Title}' ({Window.Process}, client {Window.Client.Width}x{Window.Client.Height})");
                }
                else if (_capture is null)
                {
                    Error = "window not found";
                }
            }
            catch (Exception ex)                                // a timer callback must never take the process down
            {
                Error = $"{ex.GetType().Name}: {ex.Message}";
                _log?.Invoke($"[{Name}] {Error}");
                _capture?.Dispose();
                _capture = null;
                Window = null;                               // e.g. the window was closed while the capture was being set up
            }
        }
    }

    private void Stop(string why)
    {
        if (_capture is null)
        {
            return;
        }

        _capture.Dispose();
        _capture = null;
        Window = null;
        Error = "window not found";
        _log?.Invoke($"[{Name}] {why}");
    }

    private WindowInfo ApplyGeometry(WindowInfo w)
    {
        var size = Pair(_display["client_size"]);
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

    private static (int A, int B)? Pair(JsonNode? node) =>
        node is JsonArray { Count: 2 } a ? ((int)a[0]!.AsDouble(), (int)a[1]!.AsDouble()) : null;

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
        var (w, h) = (pixels.Width, pixels.Height);
        var bytes = w * h * 4;
        if (_size != (w, h))
        {
            (_previous, _current, _size) = (new byte[bytes], new byte[bytes], (w, h));
            _previous.AsSpan().Fill(1);                        // cannot equal a real picture: alpha is 255
        }

        var source = pixels.Span;
        if (pixels.Stride == w * 4)
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
            for (var y = 0; y < h; y++)
            {
                source.Slice(y * pixels.Stride, w * 4).CopyTo(_current.AsSpan(y * w * 4));
            }

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

                EncodeAndPublish(picture, w, h);
            }
        };
    }

    private void EncodeAndPublish(byte[] picture, int w, int h)
    {
        {
            var started = Stopwatch.GetTimestamp();
            var (pixels, pw, ph) = Downscale.Fit(picture, w, h, w * 4, _settings.MaxSize) ?? (picture, w, h);      // max_size: a smaller picture, sent as such
            var jpeg = _encoder.Encode(pixels, pw, ph, pw * 4);
            _encodeMs = _encodeMs * 0.9 + Stopwatch.GetElapsedTime(started).TotalMilliseconds * 0.1;
            _jpegBytes = jpeg.Length;
            _slot.Publish(jpeg, pw, ph);
            _published++;
        }
    }
}
