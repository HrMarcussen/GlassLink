using TurboJpegWrapper;

namespace GlassLink.Capture;

/// <summary>
/// BGRA pixels to a baseline JPEG the DU's hardware decoder takes (4:2:0 by default), with libjpeg-turbo, the same
/// library the Python DMC uses. Measured on a 768x768 PFD picture (i7-8700K): 1.3 ms per frame; SkiaSharp's encoder,
/// which is libjpeg-turbo without its assembler routines, took 6.9 ms. One encoder per display: an instance is not
/// thread-safe.
/// </summary>
public sealed class JpegEncoder(int quality = 85, bool subsample420 = true) : IDisposable
{
    private readonly TJCompressor _compressor = new();
    private readonly int _quality = Math.Clamp(quality, 30, 100);
    private readonly TJSubsamplingOption _subsampling = subsample420 ? TJSubsamplingOption.Chrominance420 : TJSubsamplingOption.Chrominance444;

    public unsafe byte[] Encode(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        if (bgra.Length < (long)stride * (height - 1) + width * 4)
        {
            throw new ArgumentException("pixel buffer smaller than width x height", nameof(bgra));
        }

        fixed (byte* pixels = bgra)
        {
            return _compressor.Compress((nint)pixels, stride, width, height, TJPixelFormat.BGRA, _subsampling, _quality, TJFlags.None);
        }
    }

    public void Dispose() => _compressor.Dispose();
}
