using GlassLink.Capture;
using GlassLink.Core.Du;
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

    [Fact]
    public void A_band_puts_each_display_centred_in_its_box_from_raw_pixels_or_its_jpeg()
    {
        static byte[] Solid(int w, int h, byte b, byte g, byte r)
        {
            var px = new byte[w * h * 4];
            for (var i = 0; i < px.Length; i += 4)
            {
                (px[i], px[i + 1], px[i + 2], px[i + 3]) = (b, g, r, 255);
            }

            return px;
        }

        var raw = new FrameSlot("pfd");
        var jpegOnly = new FrameSlot("nd");
        using var encoder = new JpegEncoder(95);
        var band = new BandComposer("band", 128, 48, [new BandPart(raw, 0, 0, 48, 48), new BandPart(jpegOnly, 64, 0, 64, 48)]);
        Assert.True(raw.WantsPixels);
        raw.Publish(new byte[] { 1 }, 48, 48, Solid(48, 48, 0, 0, 255));      // the JPEG is junk: the pixels must be what is used
        jpegOnly.Publish(encoder.Encode(Solid(32, 32, 0, 255, 0), 32, 32, 32 * 4), 32, 32);
        byte[] pixels = [];
        var seq = 0u;
        var deadline = Environment.TickCount64 + 3000;
        using var decoder = new TurboJpegWrapper.TJDecompressor();
        while (Environment.TickCount64 < deadline)
        {
            if (band.WaitNewer(seq, 200) is not { } frame)
            {
                continue;
            }

            seq = frame.Seq;
            pixels = decoder.Decompress(frame.Jpeg.ToArray(), TurboJpegWrapper.TJPixelFormat.BGRA, TurboJpegWrapper.TJFlags.None, out var w, out var h, out _);
            Assert.Equal((128, 48), (w, h));
            if (pixels[(24 * 128 + 24) * 4 + 2] > 200 && pixels[(24 * 128 + 96) * 4 + 1] > 200)
            {
                break;                                               // both displays are in
            }
        }

        (int B, int G, int R) At(int x, int y) => (pixels[(y * 128 + x) * 4], pixels[(y * 128 + x) * 4 + 1], pixels[(y * 128 + x) * 4 + 2]);
        Assert.True(At(24, 24).R > 200 && At(24, 24).G < 60, $"red at 24,24: {At(24, 24)}");      // raw red in the first box
        Assert.True(At(96, 24).G > 200 && At(96, 24).R < 60, $"green at 96,24: {At(96, 24)}");    // green from the JPEG, centred in the second box (x 80..111)
        Assert.True(At(70, 24).G < 40 && At(120, 24).G < 40, "black around it");
        band.Dispose();
        Assert.False(raw.WantsPixels);
    }
}
