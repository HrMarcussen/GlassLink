using TurboJpegWrapper;

namespace GlassLink.Capture;

/// <summary>
/// Re-encodes a display's JPEG smaller for one lighter client (a phone on Wi-Fi: <c>/ws/pfd?max=384&amp;quality=70</c>):
/// decode, shrink by area averaging, encode again. About 3 ms for a 768x768 frame, paid per client, never on the DU
/// path. A frame that already fits and needs no other quality is passed through as it is.
/// </summary>
public sealed class Transcoder(int maxSize, int quality) : IDisposable
{
    private readonly TJDecompressor _decoder = new();
    private readonly JpegEncoder _encoder = new(quality > 0 ? quality : 85, subsample420: true);      // no quality asked: the DMC's usual
    private readonly object _gate = new();
    private uint _seq;
    private ReadOnlyMemory<byte>? _last;

    /// <summary>True when the request asks for anything at all.</summary>
    public static bool Wanted(int maxSize, int quality) => maxSize > 0 || quality > 0;

    /// <summary>The JPEG for this client; the last answer again for the same sequence number.</summary>
    public ReadOnlyMemory<byte> Convert(ReadOnlyMemory<byte> jpeg, uint seq, int width, int height)
    {
        lock (_gate)
        {
            if (_last is { } same && _seq == seq)
            {
                return same;
            }

            var fits = maxSize <= 0 || Math.Max(width, height) <= maxSize;
            if (fits && quality <= 0)
            {
                return (_last = Store(seq, jpeg)).Value;
            }

            var pixels = _decoder.Decompress(jpeg.ToArray(), TJPixelFormat.BGRA, TJFlags.None, out var w, out var h, out var stride);
            var (data, dw, dh) = Downscale.Fit(pixels, w, h, stride, maxSize) ?? (pixels, w, h);
            var tight = data == pixels && stride != w * 4 ? Tighten(pixels, w, h, stride) : data;
            return (_last = Store(seq, _encoder.Encode(tight, dw, dh, dw * 4))).Value;
        }
    }

    private ReadOnlyMemory<byte> Store(uint seq, ReadOnlyMemory<byte> jpeg)
    {
        _seq = seq;
        return jpeg;
    }

    private static byte[] Tighten(byte[] pixels, int w, int h, int stride)
    {
        var tight = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            pixels.AsSpan(y * stride, w * 4).CopyTo(tight.AsSpan(y * w * 4));
        }

        return tight;
    }

    public void Dispose()
    {
        _decoder.Dispose();
        _encoder.Dispose();
    }
}
