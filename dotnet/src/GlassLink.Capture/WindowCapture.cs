using System.Runtime.InteropServices;
using GlassLink.Capture.Windows;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace GlassLink.Capture;

/// <summary>The client area of one captured frame, in CPU memory; valid only during the callback.</summary>
public readonly unsafe ref struct CapturedPixels(byte* data, int width, int height, int stride)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public int Stride { get; } = stride;

    /// <summary>BGRA rows, <see cref="Stride"/> bytes apart.</summary>
    public ReadOnlySpan<byte> Span => new(data, (Height - 1) * Stride + Width * 4);
}

/// <summary>Looks at the pixels while they are mapped (keep it short: all captures share one GPU context) and may return
/// work to be done afterwards, outside that lock, such as encoding a copy it took.</summary>
public delegate Action? PixelsHandler(CapturedPixels pixels);

/// <summary>
/// Windows.Graphics.Capture of one window. The window's picture comes from the desktop compositor, so it works for
/// windows that are covered or parked off-screen, as long as they are not minimised. Only the client area is copied
/// from the GPU (title bar and borders never leave it), and only for frames the caller wants:
/// <see cref="WantFrame"/> is asked before anything is copied, which is what makes idle displays nearly free.
/// </summary>
public sealed class WindowCapture : IDisposable
{
    private static readonly Guid CaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly object DeviceGate = new();
    private static ID3D11Device? _device;
    private static IDirect3DDevice? _winrtDevice;

    private readonly nint _hwnd;
    private readonly PixelsHandler _onPixels;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private ID3D11Texture2D? _staging;
    private (int W, int H) _stagingSize;
    private global::Windows.Graphics.SizeInt32 _poolSize;
    private bool _disposed;

    /// <summary>Asked for every frame the compositor delivers, before any copy. Return false to skip it.</summary>
    public Func<bool> WantFrame { get; set; } = () => true;

    /// <summary>The window went away (the sim closed the pop-out).</summary>
    public event Action? Closed;

    public long FramesArrived { get; private set; }

    public WindowCapture(nint hwnd, PixelsHandler onPixels, double maxFps)
    {
        _hwnd = hwnd;
        _onPixels = onPixels;
        EnsureDevice();
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var pointer = interop.CreateForWindow(hwnd, CaptureItemIid);
        try
        {
            _item = GraphicsCaptureItem.FromAbi(pointer);
        }
        finally
        {
            Marshal.Release(pointer);
        }

        _poolSize = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        _item.Closed += (_, _) => Closed?.Invoke();
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = false;
        if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
        {
            _session.IsBorderRequired = false;              // no yellow capture border around the window (Windows 11)
        }

        SetMaxFps(maxFps);
        _session.StartCapture();
    }

    /// <summary>The compositor is asked not to deliver more often than this (Windows 11 22H2 and later). Note that an
    /// interval longer than the source's frame time lets only every second frame through, so keep it well above the
    /// rate that is wanted.</summary>
    public void SetMaxFps(double fps)
    {
        if (fps > 0 && ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "MinUpdateInterval"))
        {
            _session.MinUpdateInterval = TimeSpan.FromMilliseconds(Math.Max(1, 1000.0 / fps));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pool.FrameArrived -= OnFrameArrived;
        _session.Dispose();
        _pool.Dispose();
        lock (DeviceGate)
        {
            _staging?.Dispose();
        }
    }

    private unsafe void OnFrameArrived(Direct3D11CaptureFramePool pool, object? args)
    {
        using var frame = pool.TryGetNextFrame();
        if (frame is null || _disposed)
        {
            return;
        }

        FramesArrived++;
        var size = frame.ContentSize;
        if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
        {
            _poolSize = size;                                // the window was resized: next frames come in the new size
            pool.Recreate(_winrtDevice!, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, size);
            return;
        }

        if (!WantFrame() || WindowFinder.Describe(_hwnd) is not { } window)
        {
            return;
        }

        var (x, y, w, h) = WindowFinder.ClientCrop(window, size.Width, size.Height);
        var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
        var texturePointer = access.GetInterface(typeof(ID3D11Texture2D).GUID);
        using var texture = new ID3D11Texture2D(texturePointer);
        Action? afterwards;
        lock (DeviceGate)                                    // one immediate context for all captures
        {
            if (_disposed)
            {
                return;
            }

            if (_staging is null || _stagingSize != (w, h))
            {
                _staging?.Dispose();
                _staging = _device!.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
                    BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read,
                });
                _stagingSize = (w, h);
            }

            var context = _device!.ImmediateContext;
            context.CopySubresourceRegion(_staging, 0, 0, 0, 0, texture, 0, new Box(x, y, 0, x + w, y + h, 1));
            var mapped = context.Map(_staging, 0, MapMode.Read);
            try
            {
                afterwards = _onPixels(new CapturedPixels((byte*)mapped.DataPointer, w, h, (int)mapped.RowPitch));
            }
            finally
            {
                context.Unmap(_staging, 0);
            }
        }

        afterwards?.Invoke();
    }

    private static void EnsureDevice()
    {
        lock (DeviceGate)
        {
            if (_device is not null)
            {
                return;
            }

            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, [], out _device).CheckError();
            using var dxgi = _device!.QueryInterface<IDXGIDevice>();
            var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var inspectable);
            if (hr != 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            try
            {
                _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            }
            finally
            {
                Marshal.Release(inspectable);
            }
        }
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [ComImport, System.Runtime.InteropServices.Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow([In] nint window, [In] in Guid iid);

        nint CreateForMonitor([In] nint monitor, [In] in Guid iid);
    }

    [ComImport, System.Runtime.InteropServices.Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        nint GetInterface([In] in Guid iid);
    }
}
