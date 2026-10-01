namespace GlassLink.Sim;

/// <summary>
/// Finds the PFD's attitude sphere in a picture of the sim: the largest blob of dark saturated blue that is wider than
/// tall (the visible sky half). Used as a safety check before clicking: if the PFD is lit and is not where the
/// aircraft profile expects it, the camera is not in the calibrated view (right after loading it can still be
/// gliding), and a click would pop out the wrong instrument.
/// </summary>
public static class PfdDetector
{
    /// <summary>Centre x, horizon y (the blob's lower edge) and width of the sphere, in pixels of the picture; null if
    /// there is none (a dark cockpit). Pixels are BGRA, <paramref name="stride"/> bytes per row.</summary>
    public static (int X, int Y, int Width)? Find(ReadOnlySpan<byte> bgra, int width, int height, int stride, int step = 2)
    {
        var (w, h) = (width / step, height / step);
        if (w < 8 || h < 8)
        {
            return null;
        }

        var mask = new bool[w * h];
        for (var y = 0; y < h; y++)
        {
            var row = bgra.Slice(y * step * stride, width * 4);
            for (var x = 0; x < w; x++)
            {
                int b = row[x * step * 4], g = row[x * step * 4 + 1], r = row[x * step * 4 + 2];
                mask[y * w + x] = b > 90 && b > 1.4 * g && g > 1.6 * r && r < 70;
            }
        }

        (int X, int Y, int W, int H, int Area)? best = null;
        var stack = new Stack<int>();
        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start])
            {
                continue;
            }

            // flood fill one blob (8-connected), collecting its bounding box and area
            int minX = int.MaxValue, minY = int.MaxValue, maxX = 0, maxY = 0, area = 0;
            mask[start] = false;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                var (px, py) = (i % w, i / w);
                area++;
                (minX, maxX, minY, maxY) = (Math.Min(minX, px), Math.Max(maxX, px), Math.Min(minY, py), Math.Max(maxY, py));
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var (nx, ny) = (px + dx, py + dy);
                        if (nx >= 0 && nx < w && ny >= 0 && ny < h && mask[ny * w + nx])
                        {
                            mask[ny * w + nx] = false;
                            stack.Push(ny * w + nx);
                        }
                    }
                }
            }

            var (bw, bh) = ((maxX - minX + 1) * step, (maxY - minY + 1) * step);
            var fullArea = area * step * step;
            var aspect = bw / (double)Math.Max(bh, 1);
            if (fullArea >= 300 && bw >= 30 && bh >= 12 && aspect is >= 1.3 and <= 4.0 && (best is null || fullArea > best.Value.Area)
                && GroundBelow(bgra, width, height, stride, minX * step, maxX * step, (maxY + 1) * step))
            {
                best = (minX * step, minY * step, bw, bh, fullArea);
            }
        }

        return best is { } hit ? (hit.X + hit.W / 2, hit.Y + hit.H, hit.W) : null;
    }

    /// <summary>An attitude sphere has brown ground right under its sky. A blue screen without it (the FSLabs' EFB
    /// tablet in a cold and dark cockpit, 1 Oct 2026) is not a PFD: taking it for one, the safety check refused to
    /// click forever.</summary>
    private static bool GroundBelow(ReadOnlySpan<byte> bgra, int width, int height, int stride, int x0, int x1, int y0)
    {
        int brown = 0, seen = 0;
        for (var y = y0 + 2; y < Math.Min(height, y0 + 12); y += 2)
        {
            var row = bgra.Slice(y * stride, width * 4);
            for (var x = x0; x <= Math.Min(x1, width - 1); x += 2)
            {
                int b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                seen++;
                if (r > 50 && r > g && g > b && r > b + 25)
                {
                    brown++;
                }
            }
        }

        return seen > 0 && brown * 10 >= seen * 3;                // at least 30 % brown
    }
}
