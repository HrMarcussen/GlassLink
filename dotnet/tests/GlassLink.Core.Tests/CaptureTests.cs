using GlassLink.Capture;
using TurboJpegWrapper;

namespace GlassLink.Core.Tests;

public class CaptureTests
{
    private static byte[] Picture(int w, int h)
    {
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)(x * 255 / w), (byte)(y * 255 / h), (byte)((x + y) % 256), 255);
            }
        }

        return pixels;
    }

    [Fact]
    public void A_lighter_client_gets_a_smaller_picture_and_the_same_one_again_for_the_same_frame()
    {
        using var encoder = new JpegEncoder(85, true);
        var full = encoder.Encode(Picture(768, 768), 768, 768, 768 * 4);
        using var lighter = new Transcoder(384, 70);
        var small = lighter.Convert(full, 7, 768, 768);
        Assert.True(small.Length < full.Length / 2, $"{small.Length} of {full.Length} bytes");
        using var decoder = new TJDecompressor();
        decoder.Decompress(small.ToArray(), TJPixelFormat.BGRA, TJFlags.None, out var w, out var h, out _);
        Assert.Equal((384, 384), (w, h));
        Assert.True(lighter.Convert(full, 7, 768, 768).Span.SequenceEqual(small.Span));       // cached for the same sequence number

        using var passthrough = new Transcoder(1024, 0);                                        // fits already, no quality asked: as it is
        Assert.True(passthrough.Convert(full, 8, 768, 768).Span.SequenceEqual(full));
        Assert.False(Transcoder.Wanted(0, 0));
    }
}
