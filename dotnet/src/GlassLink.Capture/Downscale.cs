namespace GlassLink.Capture;

/// <summary>
/// Shrinks a BGRA picture so that its longer side is at most <c>maxSize</c> pixels (a display's <c>max_size</c>).
/// Area averaging, as cv2.INTER_AREA in the Python DMC: every source pixel contributes to the output in proportion
/// to the area it covers, so thin lines and small digits fade evenly instead of dropping out. Separable: rows first
/// into a row buffer of sums, then columns. About 2 ms for 1024x1024 -> 768x768.
/// </summary>
public static class Downscale
{
    /// <summary>The scaled picture (tight stride), or null if the picture already fits.</summary>
    public static (byte[] Pixels, int Width, int Height)? Fit(ReadOnlySpan<byte> bgra, int width, int height, int stride, int maxSize)
    {
        var longest = Math.Max(width, height);
        if (maxSize <= 0 || longest <= maxSize)
        {
            return null;
        }

        var scale = maxSize / (double)longest;
        // whole 16-pixel blocks: the DU's JPEG decoder refuses 4:2:0 pictures of other sizes (#27)
        var (w, h) = (Math.Max(16, (int)Math.Round(width * scale / 16) * 16), Math.Max(16, (int)Math.Round(height * scale / 16) * 16));
        var xs = Weights(width, w);
        var ys = Weights(height, h);

        // pass 1: each source row shrunk to w pixels, as floats (pooled: this runs per frame)
        var rows = System.Buffers.ArrayPool<float>.Shared.Rent(height * w * 4);
        rows.AsSpan(0, height * w * 4).Clear();
        var acc = System.Buffers.ArrayPool<float>.Shared.Rent(h * w * 4);
        acc.AsSpan(0, h * w * 4).Clear();
        try
        {
        for (var y = 0; y < height; y++)
        {
            var src = bgra.Slice(y * stride, width * 4);
            var dst = rows.AsSpan(y * w * 4, w * 4);
            foreach (var (from, to, weight) in xs)
            {
                var o = to * 4;
                var i = from * 4;
                dst[o] += src[i] * weight;
                dst[o + 1] += src[i + 1] * weight;
                dst[o + 2] += src[i + 2] * weight;
                dst[o + 3] += src[i + 3] * weight;
            }
        }

        // pass 2: the shrunk rows combined into h rows
        foreach (var (from, to, weight) in ys)
        {
            var src = rows.AsSpan(from * w * 4, w * 4);
            var dst = acc.AsSpan(to * w * 4, w * 4);
            for (var i = 0; i < dst.Length; i++)
            {
                dst[i] += src[i] * weight;
            }
        }

        var pixels = new byte[h * w * 4];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)Math.Clamp((int)(acc[i] + 0.5f), 0, 255);
        }

        return (pixels, w, h);
        }
        finally
        {
            System.Buffers.ArrayPool<float>.Shared.Return(rows);
            System.Buffers.ArrayPool<float>.Shared.Return(acc);
        }
    }

    /// <summary>For every source index along one axis: the output index it lands in and the share of that output
    /// pixel it makes up (a source pixel straddling two output pixels appears twice). Shares of one output sum to 1.</summary>
    private static List<(int From, int To, float Weight)> Weights(int source, int target)
    {
        var list = new List<(int, int, float)>(source + target);
        var ratio = source / (double)target;                   // source pixels per output pixel
        for (var to = 0; to < target; to++)
        {
            var (start, end) = (to * ratio, (to + 1) * ratio);
            for (var from = (int)start; from < Math.Min(source, Math.Ceiling(end)); from++)
            {
                var overlap = Math.Min(end, from + 1) - Math.Max(start, from);
                if (overlap > 1e-9)
                {
                    list.Add((from, to, (float)(overlap / ratio)));
                }
            }
        }

        return list;
    }
}
