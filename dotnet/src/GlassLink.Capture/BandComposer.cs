using System.Diagnostics;
using System.Runtime.InteropServices;
using GlassLink.Core.Du;
using TurboJpegWrapper;

namespace GlassLink.Capture;

/// <summary>
/// Several displays as one picture as wide as the DU's screen: the tiles of a layout side by side on black, a band
/// covering the rows they use. The DU decodes a picture of its own width straight into its frame buffer, while every
/// separate tile had to be decoded and then copied into place; in 1080p two 768 x 768 tiles went from 12 fps each to
/// 25-30 fps for both (28 Sept 2026). Every display is drawn from its raw capture when the slot has it, otherwise
/// from its JPEG; a new band is encoded when any of them changed, at most <c>maxFps</c> times a second, so changes of
/// two displays close together go out as one picture.
/// </summary>
public sealed class BandComposer : IFrameSource, IDisposable
{
    private readonly FrameSlot _slot;
    private readonly BandPart[] _parts;
    private readonly int _width, _height;
    private readonly byte[] _canvas;
    private readonly uint[] _drawn;
    private readonly JpegEncoder _encoder;
    private readonly TJDecompressor _decoder = new();
    private readonly int _intervalMs;
    private readonly Thread _thread;
    private volatile bool _stop;
    private double _composeMs;
    private readonly AutoResetEvent _wake = new(false);
    private readonly bool _finerTimer;

    public BandComposer(string name, int width, int height, IReadOnlyList<BandPart> parts, int quality = 85, double maxFps = 30)
    {
        if (width % 16 != 0 || height % 16 != 0 || width <= 0 || height <= 0)
        {
            throw new ArgumentException($"a band must be whole 16-pixel blocks, not {width}x{height}");
        }

        (_width, _height, _parts) = (width, height, [.. parts]);
        _slot = new FrameSlot(name);
        _canvas = new byte[width * height * 4];                      // black, alpha ignored by the encoder
        _drawn = new uint[_parts.Length];
        _encoder = new JpegEncoder(quality, subsample420: true);
        _intervalMs = (int)Math.Max(1, 1000 / Math.Max(1, maxFps));
        foreach (var part in _parts)
        {
            if (part.Source is FrameSlot slot)
            {
                slot.AddPixelUser();
                slot.Published += Wake;
            }
        }

        // Pacing at 33 ms needs millisecond sleeps; Windows' default timer ticks every 15.6 ms, which turned 30 fps into
        // 21. Asked for while a band exists (per process since Windows 10 2004; counted, so bands can overlap).
        _finerTimer = timeBeginPeriod(1) == 0;
        _thread = new Thread(Run) { IsBackground = true, Name = $"band {name}" };
        _thread.Start();
    }

    public string Name => _slot.Name;

    public Frame? Latest => _slot.Latest;

    public Frame? WaitNewer(uint afterSeq, int timeoutMs) => _slot.WaitNewer(afterSeq, timeoutMs);

    /// <summary>Milliseconds per band (drawing the changed displays and encoding), smoothed.</summary>
    public double ComposeMs => Volatile.Read(ref _composeMs);

    private void Wake() => _wake.Set();

    private void Run()
    {
        var last = 0L;
        while (!_stop)
        {
            // woken by a display that published (a source that is not a slot is looked at every 50 ms), then no sooner
            // than the interval after the last band: displays that change together go out together
            _wake.WaitOne(_parts.All(p => p.Source is FrameSlot) ? 500 : 50);
            var wait = (int)(last + _intervalMs - Environment.TickCount64);
            if (wait > 0)
            {
                Thread.Sleep(wait);
            }

            var started = Stopwatch.GetTimestamp();
            var startedMs = Environment.TickCount64;
            var changed = false;
            for (var i = 0; i < _parts.Length; i++)
            {
                if (_parts[i].Source.Latest is { } frame && frame.Seq != _drawn[i])
                {
                    try
                    {
                        Draw(_parts[i], frame);
                        changed = true;
                    }
                    catch (Exception)                                // one bad picture must not stop the band
                    {
                    }

                    _drawn[i] = frame.Seq;
                }
            }

            if (!changed)
            {
                continue;
            }

            var jpeg = _encoder.Encode(_canvas, _width, _height, _width * 4);
            _slot.Publish(jpeg, _width, _height);
            last = startedMs;                                        // the interval counts from the start: composing takes time too
            _composeMs = _composeMs * 0.9 + Stopwatch.GetElapsedTime(started).TotalMilliseconds * 0.1;
        }
    }

    /// <summary>One display into its box: centred like the DU centres a tile, cut to the box if it is larger.</summary>
    private void Draw(BandPart part, Frame frame)
    {
        if (part.Source is FrameSlot slot && slot.ReadPixels(frame.Seq, (pixels, size) => Blit(part, pixels, size.Width, size.Height, size.Width * 4)))
        {
            return;
        }

        var bgra = _decoder.Decompress(frame.Jpeg.ToArray(), TJPixelFormat.BGRA, TJFlags.None, out var w, out var h, out var stride);
        Blit(part, bgra, w, h, stride);
    }

    private void Blit(BandPart part, ReadOnlySpan<byte> pixels, int w, int h, int stride)
    {
        // the box inside the band (a layout that changed under us must not write outside the canvas)
        var bx = Math.Clamp(part.X, 0, _width);
        var by = Math.Clamp(part.Y, 0, _height);
        var bw = Math.Min(part.Width, _width - bx);
        var bh = Math.Min(part.Height, _height - by);
        if (bw <= 0 || bh <= 0)
        {
            return;
        }

        for (var y = 0; y < bh; y++)                                // black first: a smaller picture leaves no old edges
        {
            _canvas.AsSpan(((by + y) * _width + bx) * 4, bw * 4).Clear();
        }

        var cw = Math.Min(w, bw);
        var ch = Math.Min(h, bh);
        var (sx, sy) = ((w - cw) / 2, (h - ch) / 2);                 // cut a larger picture around its centre
        var (dx, dy) = (bx + (bw - cw) / 2, by + (bh - ch) / 2);     // centre a smaller one
        for (var y = 0; y < ch; y++)
        {
            pixels.Slice((sy + y) * stride + sx * 4, cw * 4).CopyTo(_canvas.AsSpan(((dy + y) * _width + dx) * 4));
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(2000);
        foreach (var part in _parts)
        {
            if (part.Source is FrameSlot slot)
            {
                slot.Published -= Wake;
                slot.RemovePixelUser();
            }
        }

        if (_finerTimer)
        {
            timeEndPeriod(1);
        }

        _wake.Dispose();

        _encoder.Dispose();
        _decoder.Dispose();
    }

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint milliseconds);
}
