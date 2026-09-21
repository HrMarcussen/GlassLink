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

/// <summary>Finding, sizing and parking the windows that are captured. Port of glasslink/windows.py.</summary>
public static class WindowFinder
{
    private static readonly Dictionary<uint, string> ProcessNames = [];

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

    public static bool IsWindow(nint hwnd) => Native.IsWindow(hwnd);

    /// <summary>Still there and still showing. A pop-out that was closed can linger as an invisible window for a while,
    /// so existence alone does not mean the display has a window.</summary>
    public static bool IsAlive(nint hwnd) => Native.IsWindow(hwnd) && Native.IsWindowVisible(hwnd) && !Native.IsIconic(hwnd) && !IsCloaked(hwnd);

    /// <summary>Cloaked = kept by the window manager but not shown anywhere. The sim leaves a closed pop-out like this
    /// for a while (found 21 Sept 2026: such a zombie still has its title and counts as visible).</summary>
    public static bool IsCloaked(nint hwnd) => Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>Makes the client area width x height and optionally moves the window, without activating it.</summary>
    public static void SetClientSize(WindowInfo w, int width, int height, int? x, int? y)
    {
        var flags = Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | (x is null || y is null ? Native.SWP_NOMOVE : 0);
        Native.SetWindowPos(w.Handle, 0, x ?? 0, y ?? 0, width + w.Window.Width - w.Client.Width, height + w.Window.Height - w.Client.Height, flags);
    }

    public static void Move(nint hwnd, int x, int y) =>
        Native.SetWindowPos(hwnd, 0, x, y, 0, 0, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOSIZE);

    /// <summary>Names a window (GlassLink:pfd), which is how it is found again after a DMC restart.</summary>
    public static void SetTitle(nint hwnd, string title) => Native.SetWindowText(hwnd, title);

    /// <summary>Asks a window to close, as its close button would.</summary>
    public static void Close(nint hwnd) => Native.PostMessage(hwnd, 0x0010, 0, 0);

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

    /// <summary>
    /// Where the client area lies inside a captured frame. A capture delivers either the DWM frame rectangle or the
    /// GetWindowRect rectangle (which includes the invisible resize borders); take the one whose size matches.
    /// </summary>
    public static (int X, int Y, int Width, int Height) ClientCrop(WindowInfo w, int frameWidth, int frameHeight)
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
            if (!ProcessNames.TryGetValue(pid, out var name))
            {
                try
                {
                    using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                    name = p.ProcessName + ".exe";
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    name = "";
                }

                ProcessNames[pid] = name;
            }

            return name;
        }
    }

    private static string Text(nint hwnd, Func<nint, StringBuilder, int, int> read)
    {
        var sb = new StringBuilder(256);
        return read(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private static class Native
    {
        public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10;
        public const int GWL_EXSTYLE = -20, DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
        public const long WS_EX_NOACTIVATE = 0x08000000;

        public delegate bool EnumProc(nint hwnd, nint lParam);

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
