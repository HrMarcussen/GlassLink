using GlassLink.Core.Config;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GlassLink.Capture.Windows;

public readonly record struct Rect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

/// <summary>A top-level window as the DMC sees it. All rectangles are in physical pixels.</summary>
/// <summary>Window rectangle, client area and DWM frame, in screen pixels.</summary>
public readonly record struct WindowGeometry(Rect Window, Rect Client, Rect Frame);

public sealed record WindowInfo(nint Handle, string Title, string ClassName, string Process, bool Minimized, Rect Window, Rect Client, Rect Frame);

/// <summary>Which window a display is: the "match" rule of a display in config.json. Every key that is given must hold.</summary>
public sealed record WindowMatch(string? Process, string? ClassName, string? Title, string? TitleExact, string? TitleRegex, int[]? ClientSize)
{
    public static WindowMatch From(JsonObject? o)
    {
        string? Str(string key) => o?[key] is { } n && n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : null;
        var size = o?["client_size"] is JsonArray { Count: 2 } a ? new[] { (int)a[0]!.AsDouble(), (int)a[1]!.AsDouble() } : null;
        return new WindowMatch(Str("process"), Str("class"), Str("title"), Str("title_exact"), Str("title_regex"), size);
    }

    public bool Matches(WindowInfo w) =>
        (Process is null || string.Equals(w.Process, Process, StringComparison.OrdinalIgnoreCase))
        && (ClassName is null || w.ClassName == ClassName)
        && (TitleExact is null || w.Title == TitleExact)
        && (Title is null || w.Title.Contains(Title, StringComparison.OrdinalIgnoreCase))
        && (TitleRegex is null || Regex.IsMatch(w.Title, TitleRegex))
        && (ClientSize is null || (w.Client.Width == ClientSize[0] && w.Client.Height == ClientSize[1]));
}

/// <summary>Finding, sizing and parking the windows that are captured.</summary>
public static class WindowFinder
{
    private static readonly Dictionary<uint, (string Name, long At)> ProcessNames = [];   // looked up again after 30 s: process ids are reused (#34)

    /// <summary>Physical pixels everywhere: call once at start, before any window function.</summary>
    public static void SetDpiAware() => Native.SetProcessDpiAwarenessContext(-4);      // per monitor v2

    public static List<WindowInfo> Enumerate()
    {
        var found = new List<WindowInfo>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (Native.IsWindowVisible(hwnd) && !IsCloaked(hwnd) && Describe(hwnd) is { } info && info.Client.Width > 0 && info.Client.Height > 0)
            {
                found.Add(info);
            }

            return true;
        }, 0);
        return found;
    }

    /// <summary>The window for a rule; with several hits the one that is not minimised and smallest (a pop-out is
    /// smaller than the sim's main window).</summary>
    public static WindowInfo? Find(WindowMatch match) =>
        Enumerate().Where(match.Matches).OrderBy(w => w.Minimized).ThenBy(w => (long)w.Client.Width * w.Client.Height).FirstOrDefault();

    public static WindowInfo? Describe(nint hwnd)
    {
        if (!Native.IsWindow(hwnd) || !Native.GetWindowRect(hwnd, out var wr) || !Native.GetClientRect(hwnd, out var cr))
        {
            return null;
        }

        var origin = default(Native.Point);
        Native.ClientToScreen(hwnd, ref origin);
        var client = new Rect(origin.X, origin.Y, origin.X + cr.Right, origin.Y + cr.Bottom);
        var frame = Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Rect fr, Marshal.SizeOf<Rect>()) == 0 ? fr : wr;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        return new WindowInfo(hwnd, Text(hwnd, Native.GetWindowText), Text(hwnd, Native.GetClassName), ProcessName(pid), Native.IsIconic(hwnd), wr, client, frame);
    }

    /// <summary>Only the three rectangles of a window, for every captured frame: no title, class or process name, so
    /// nothing is allocated per frame (#34). Null when the window is gone.</summary>
    public static WindowGeometry? Geometry(nint hwnd)
    {
        if (!Native.GetWindowRect(hwnd, out var wr) || !Native.GetClientRect(hwnd, out var cr))
        {
            return null;
        }

        var origin = default(Native.Point);
        Native.ClientToScreen(hwnd, ref origin);
        var frame = Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Rect fr, Marshal.SizeOf<Rect>()) == 0 ? fr : wr;
        return new WindowGeometry(wr, new Rect(origin.X, origin.Y, origin.X + cr.Right, origin.Y + cr.Bottom), frame);
    }

    public static bool IsWindow(nint hwnd) => Native.IsWindow(hwnd);

    /// <summary>Still there and still showing. A pop-out that was closed can linger as an invisible window for a while,
    /// so existence alone does not mean the display has a window.</summary>
    public static bool IsAlive(nint hwnd) => Native.IsWindow(hwnd) && Native.IsWindowVisible(hwnd) && !Native.IsIconic(hwnd) && !IsCloaked(hwnd);

    /// <summary>Cloaked = kept by the window manager but not shown anywhere. The sim leaves a closed pop-out like this
    /// for a while (found 21 Sept 2026: such a zombie still has its title and counts as visible).</summary>
    public static bool IsCloaked(nint hwnd) => Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>Makes the client area width x height and optionally parks the window (see <see cref="OffScreen"/>),
    /// without activating it.</summary>
    public static void SetClientSize(WindowInfo w, int width, int height, int? x, int? y)
    {
        var (cx, cy) = (width + w.Window.Width - w.Client.Width, height + w.Window.Height - w.Client.Height);
        var flags = Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | (x is null || y is null ? Native.SWP_NOMOVE : 0);
        var (px, py) = x is { } ax && y is { } ay ? OffScreen(ax, ay, cx, cy, Monitors()) : (0, 0);
        Native.SetWindowPos(w.Handle, 0, px, py, cx, cy, flags);
    }

    /// <summary>Parks a window at x, y (see <see cref="OffScreen"/>), without activating it.</summary>
    public static void Move(nint hwnd, int x, int y)
    {
        var size = Native.GetWindowRect(hwnd, out var r) ? (r.Width, r.Height) : (0, 0);
        var (px, py) = OffScreen(x, y, size.Item1, size.Item2, Monitors());
        Native.SetWindowPos(hwnd, 0, px, py, 0, 0, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOSIZE);
    }

    /// <summary>
    /// Where a parked window really goes: its configured place, unless that lies on one of the screens. Then it moves
    /// right of all of them, keeping its distance to the default parking area (x 2600, made for a 2560-wide screen), so
    /// a wider or added monitor never shows a pop-out (found 7 Oct 2026 with a 3840-wide screen). Parked windows may
    /// overlap each other: the capture reads each window on its own.
    /// </summary>
    public static (int X, int Y) OffScreen(int x, int y, int width, int height, IReadOnlyList<Rect> monitors)
    {
        var box = new Rect(x, y, x + Math.Max(1, width), y + Math.Max(1, height));
        if (monitors.Count == 0 || !monitors.Any(m => m.Left < box.Right && box.Left < m.Right && m.Top < box.Bottom && box.Top < m.Bottom))
        {
            return (x, y);
        }

        var right = monitors.Max(m => m.Right) + 40;
        return (Math.Max(x + right - 2600, right), y);
    }

    /// <summary>The monitors, in physical pixels.</summary>
    public static List<Rect> Monitors()
    {
        var found = new List<Rect>();
        Native.EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref Rect r, nint data) => { found.Add(r); return true; }, 0);
        return found;
    }

    /// <summary>Names a window (GlassLink:pfd), which is how it is found again after a DMC restart.</summary>
    public static void SetTitle(nint hwnd, string title) => Native.SetWindowText(hwnd, title);

    /// <summary>Asks a window to close, as its close button would. Never an X-Plane window: X-Plane takes a close message
    /// to any of its windows, a pop-out too, as "quit X-Plane" and exits at once (2 Oct 2026). False if refused.</summary>
    public static bool Close(nint hwnd)
    {
        if (Describe(hwnd) is { } w && string.Equals(w.Process, "X-Plane.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Native.PostMessage(hwnd, 0x0010, 0, 0);
        return true;
    }

    /// <summary>The window's picture as BGRA pixels (PrintWindow, works for DirectX windows); null if it cannot be read.</summary>
    public static unsafe (byte[] Pixels, int Width, int Height)? Grab(nint hwnd)
    {
        if (Describe(hwnd) is not { } w || w.Window.Width <= 0 || w.Window.Height <= 0)
        {
            return null;
        }

        var (width, height) = (w.Window.Width, w.Window.Height);
        var screen = Native.GetDC(0);
        var memory = Native.CreateCompatibleDC(screen);
        var header = new Native.BITMAPINFOHEADER { biSize = 40, biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        var bitmap = Native.CreateDIBSection(memory, ref header, 0, out var bits, 0, 0);
        try
        {
            if (bitmap == 0 || bits == 0)
            {
                return null;
            }

            var old = Native.SelectObject(memory, bitmap);
            var ok = Native.PrintWindow(hwnd, memory, 2);
            Native.SelectObject(memory, old);
            if (!ok)
            {
                return null;
            }

            var pixels = new byte[width * height * 4];
            new ReadOnlySpan<byte>((void*)bits, pixels.Length).CopyTo(pixels);
            return (pixels, width, height);
        }
        finally
        {
            if (bitmap != 0)
            {
                Native.DeleteObject(bitmap);
            }

            Native.DeleteDC(memory);
            Native.ReleaseDC(0, screen);
        }
    }

    /// <summary>
    /// The window's picture reduced to a coarse grid of grey values (rows x columns, 0..255), taken with PrintWindow.
    /// Two of these a moment apart tell whether the sim's camera is still moving. Null if the window cannot be read.
    /// </summary>
    public static unsafe float[]? CoarseGrey(nint hwnd, int rows = 36, int columns = 64)
    {
        if (Describe(hwnd) is not { } w || w.Window.Width <= 0 || w.Window.Height <= 0)
        {
            return null;
        }

        var (width, height) = (w.Window.Width, w.Window.Height);
        var screen = Native.GetDC(0);
        var memory = Native.CreateCompatibleDC(screen);
        var header = new Native.BITMAPINFOHEADER { biSize = 40, biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        var bitmap = Native.CreateDIBSection(memory, ref header, 0, out var bits, 0, 0);
        try
        {
            if (bitmap == 0 || bits == 0)
            {
                return null;
            }

            var old = Native.SelectObject(memory, bitmap);
            var ok = Native.PrintWindow(hwnd, memory, 2);       // PW_RENDERFULLCONTENT: works for DirectX windows
            Native.SelectObject(memory, old);
            if (!ok)
            {
                return null;
            }

            var grid = new float[rows * columns];
            var pixels = (byte*)bits;
            for (var r = 0; r < rows; r++)
            {
                var y = (int)((long)r * (height - 1) / Math.Max(1, rows - 1));
                for (var c = 0; c < columns; c++)
                {
                    var x = (int)((long)c * (width - 1) / Math.Max(1, columns - 1));
                    var p = pixels + ((long)y * width + x) * 4;
                    grid[r * columns + c] = (p[0] + p[1] + p[2]) / 3f;
                }
            }

            return grid;
        }
        finally
        {
            if (bitmap != 0)
            {
                Native.DeleteObject(bitmap);
            }

            Native.DeleteDC(memory);
            Native.ReleaseDC(0, screen);
        }
    }

    /// <summary>A parked pop-out must never take the keyboard focus (WS_EX_NOACTIVATE). True if this changed it.</summary>
    public static bool SetNoActivate(nint hwnd)
    {
        var style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
        if ((style & Native.WS_EX_NOACTIVATE) != 0)
        {
            return false;
        }

        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, style | Native.WS_EX_NOACTIVATE);
        return true;
    }

    /// <summary>A tool window that never takes the focus: left out of Alt+Tab and the taskbar, for the pop-outs of a sim
    /// that shows them as ordinary windows (X-Plane). True if this changed it.</summary>
    public static bool SetToolWindow(nint hwnd)
    {
        var style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
        var wanted = (style | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE) & ~Native.WS_EX_APPWINDOW;
        if (wanted == style)
        {
            return false;
        }

        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, wanted);
        Native.SetWindowPos(hwnd, 0, 0, 0, 0, 0, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOSIZE | Native.SWP_NOMOVE | Native.SWP_FRAMECHANGED);
        return true;
    }

    /// <summary>
    /// Where the client area lies inside a captured frame. A capture delivers either the DWM frame rectangle or the
    /// GetWindowRect rectangle (which includes the invisible resize borders); take the one whose size matches.
    /// </summary>
    public static (int X, int Y, int Width, int Height) ClientCrop(WindowInfo w, int frameWidth, int frameHeight) =>
        ClientCrop(new WindowGeometry(w.Window, w.Client, w.Frame), frameWidth, frameHeight);

    public static (int X, int Y, int Width, int Height) ClientCrop(WindowGeometry w, int frameWidth, int frameHeight)
    {
        var origin = (frameWidth, frameHeight) == (w.Frame.Width, w.Frame.Height) ? w.Frame : w.Window;
        var x = Math.Max(0, w.Client.Left - origin.Left);
        var y = Math.Max(0, w.Client.Top - origin.Top);
        var width = Math.Min(w.Client.Width, frameWidth - x);
        var height = Math.Min(w.Client.Height, frameHeight - y);
        return width <= 0 || height <= 0 ? (0, 0, frameWidth, frameHeight) : (x, y, width, height);
    }

    private static string ProcessName(uint pid)
    {
        lock (ProcessNames)
        {
            if (!ProcessNames.TryGetValue(pid, out var cached) || Environment.TickCount64 - cached.At > 30_000)
            {
                string name;
                try
                {
                    using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                    name = p.ProcessName + ".exe";
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    name = "";
                }

                ProcessNames[pid] = cached = (name, Environment.TickCount64);
            }

            return cached.Name;
        }
    }

    private static string Text(nint hwnd, Func<nint, StringBuilder, int, int> read)
    {
        var sb = new StringBuilder(256);
        return read(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private static class Native
    {
        public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;
        public const int GWL_EXSTYLE = -20, DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
        public const long WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;

        public delegate bool EnumProc(nint hwnd, nint lParam);

        public delegate bool MonitorEnumProc(nint monitor, nint dc, ref Rect rect, nint data);

        [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnumProc callback, nint data);

        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public int X, Y;
        }

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, nint lParam);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(nint hwnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hwnd, out Rect rect);
        [DllImport("user32.dll")] public static extern bool GetClientRect(nint hwnd, out Rect rect);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(nint hwnd, ref Point point);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(nint hwnd, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(nint hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern long GetWindowLongPtr(nint hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern long SetWindowLongPtr(nint hwnd, int index, long value);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(nint context);
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SetWindowText(nint hwnd, string text);
        [DllImport("user32.dll")] public static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
        [DllImport("user32.dll")] public static extern bool PrintWindow(nint hwnd, nint dc, uint flags);
        [DllImport("user32.dll")] public static extern nint GetDC(nint hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(nint hwnd, nint dc);
        [DllImport("gdi32.dll")] public static extern nint CreateCompatibleDC(nint dc);
        [DllImport("gdi32.dll")] public static extern nint CreateDIBSection(nint dc, ref BITMAPINFOHEADER info, uint usage, out nint bits, nint section, uint offset);
        [DllImport("gdi32.dll")] public static extern nint SelectObject(nint dc, nint obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(nint obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(nint dc);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    }
}
