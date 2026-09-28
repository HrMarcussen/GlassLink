using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace GlassLink.Dmc;

/// <summary>
/// The status page in a window of the DMC's own (WebView2), without the Windows title bar: the page's top bar is the
/// title bar. It drags the window (CSS app-region) and carries minimise / maximise / close, which arrive here as web
/// messages. The window keeps its resize borders, shadow, rounded corners and snap, because only the caption is taken
/// away (WM_NCCALCSIZE), not the frame. Closing it frees the browser; the DMC runs on in the notification area.
/// The same page stays reachable from any browser, a phone included: this window is just one more client.
/// </summary>
public sealed class StatusWindow : Form
{
    private const int WmNcCalcSize = 0x0083;
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassLink");
    private static readonly string StateFile = Path.Combine(Folder, "window.json");
    private static StatusWindow? _open;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly Action<string> _log;
    private readonly string _url;
    private Rectangle _normal;

    private StatusWindow(string url, Action<string> log)
    {
        (_url, _log) = (url, log);
        var palette = Palette.Current;
        Text = "GlassLink DMC";
        Icon = ExeIcon.Value;                               // the icon of GlassLink.exe (glasslink.ico), made once (#47)
        BackColor = palette.Background;                      // what shows until the page has loaded: no white flash
        MinimumSize = new Size(480, 360);
        StartPosition = FormStartPosition.Manual;
        RestoreBounds_();
        _web.DefaultBackgroundColor = palette.Background;
        Controls.Add(_web);
        Shown += async (_, _) => await Start();
        ResizeEnd += (_, _) => Remember();
        Move += (_, _) => Remember();
        FormClosing += (_, _) => Save();
        FormClosed += (_, _) => { _open = null; _web.Dispose(); };
    }

    /// <summary>False if there is no WebView2 runtime on this PC (it is part of Windows 11; on Windows 10 it comes with Edge).</summary>
    public static bool Available
    {
        get
        {
            try
            {
                return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
            }
            catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or DllNotFoundException or COMException)
            {
                return false;
            }
        }
    }

    /// <summary>Opens the window, or brings the open one to the front. Call on the UI thread.</summary>
    public static void Open(string url, Action<string> log)
    {
        if (_open is null)
        {
            _open = new StatusWindow(url, log);
            _open.Show();
        }

        if (_open.WindowState == FormWindowState.Minimized)
        {
            _open.WindowState = FormWindowState.Normal;
        }

        _open.Activate();
    }

    /// <summary>The window as a program of its own, until it is closed: a second start of GlassLink.exe while the DMC runs.</summary>
    public static void RunAlone(string url)
    {
        using var window = new StatusWindow(url, _ => { });
        Application.Run(window);
    }

    private async Task Start()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Folder, "WebView2"));
            await _web.EnsureCoreWebView2Async(environment);
            var core = _web.CoreWebView2;
            core.Settings.IsNonClientRegionSupportEnabled = true;      // app-region: drag in the page moves the window
            core.Settings.IsStatusBarEnabled = false;
#if DEBUG
            core.Settings.AreDevToolsEnabled = true;
#else
            core.Settings.AreDevToolsEnabled = false;
#endif
            core.WebMessageReceived += (_, e) => Command(e.TryGetWebMessageAsString());
            // The page stays on the DMC; the viewer links open in the user's browser. Nothing else is ever opened: not
            // file://, not another site, not a protocol handler (#3).
            core.NavigationStarting += (_, e) =>
            {
                if (!IsOwn(e.Uri))
                {
                    e.Cancel = true;
                    _log($"status window: navigation to {e.Uri} refused");
                }
            };
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (!IsOwn(e.Uri) || !new Uri(e.Uri).AbsolutePath.StartsWith("/view/", StringComparison.Ordinal))
                {
                    _log($"status window: new window for {e.Uri} refused");
                    return;
                }

                try
                {
                    Process.Start(new ProcessStartInfo(new Uri(e.Uri).AbsoluteUri) { UseShellExecute = true });
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    _log($"status window: could not open the viewer: {ex.Message}");
                }
            };
            _web.ZoomFactor = ReadState()?.Zoom is > 0.2 and < 5 ? ReadState()!.Zoom : 1.0;
            _web.ZoomFactorChanged += (_, _) => Save();                // Ctrl + wheel: text size is remembered
            core.Navigate(_url);
        }
        catch (Exception ex)
        {
            _log($"status window: {ex.GetType().Name}: {ex.Message}");
            Close();
        }
    }

    /// <summary>True for an http address of this DMC (same host name and port as the page it opened).</summary>
    private bool IsOwn(string? uri)
    {
        var own = new Uri(_url);
        return Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttp && u.Port == own.Port
               && (u.IsLoopback || string.Equals(u.Host, own.Host, StringComparison.OrdinalIgnoreCase));
    }

    private void Command(string? command)
    {
        switch (command)
        {
            case "min":
                WindowState = FormWindowState.Minimized;
                break;
            case "max":
                WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                break;
            case "close":
                Close();
                break;
        }
    }

    /// <summary>No caption: the client area starts at the top of the window. The side and bottom borders stay, and with
    /// them resizing, the shadow and snap. A maximised window reaches past the screen by the frame's thickness, so
    /// there the top is pulled in by that much.</summary>
    protected override void WndProc(ref System.Windows.Forms.Message m)
    {
        if (m.Msg == WmNcCalcSize && m.WParam != 0)
        {
            var before = Marshal.ReadInt32(m.LParam, 4);               // rgrc[0].top
            base.WndProc(ref m);
            var top = before;
            if (WindowState == FormWindowState.Maximized)
            {
                var dpi = GetDpiForWindow(Handle);
                top += GetSystemMetricsForDpi(33, dpi) + GetSystemMetricsForDpi(92, dpi);      // SM_CYSIZEFRAME + SM_CXPADDEDBORDER
                Marshal.WriteInt32(m.LParam, 4, top);
                LeaveRoomForAutoHideTaskbar(m.LParam);
                m.Result = 0;
                return;
            }

            Marshal.WriteInt32(m.LParam, 4, top);
            m.Result = 0;
            return;
        }

        base.WndProc(ref m);
    }

    /// <summary>A maximised window that covers the whole monitor looks full-screen to the shell, and an auto-hide
    /// taskbar then cannot be brought up with the mouse. Leave 2 px free on the edge where it hides (#45).</summary>
    private void LeaveRoomForAutoHideTaskbar(nint rects)
    {
        var monitor = Screen.FromHandle(Handle).Bounds;
        foreach (var (edge, index) in new[] { (0u, 0), (1u, 4), (2u, 8), (3u, 12) })      // ABE_LEFT, TOP, RIGHT, BOTTOM -> left, top, right, bottom
        {
            var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), uEdge = edge, rc = new RECT { Left = monitor.Left, Top = monitor.Top, Right = monitor.Right, Bottom = monitor.Bottom } };
            if (SHAppBarMessage(0x0000000b, ref data) != 0)                                   // ABM_GETAUTOHIDEBAREX: a bar hides on this edge
            {
                var v = Marshal.ReadInt32(rects, index);
                Marshal.WriteInt32(rects, index, edge is 0 or 1 ? v + 2 : v - 2);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public nint hWnd;
        public uint uCallbackMessage, uEdge;
        public RECT rc;
        public nint lParam;
    }

    [DllImport("shell32.dll")]
    private static extern nuint SHAppBarMessage(uint message, ref APPBARDATA data);

    // -- where the window was, and how large its text -----------------------------------------------------------
    private sealed record State(int X, int Y, int Width, int Height, bool Maximized, double Zoom);

    private static State? ReadState()
    {
        try
        {
            return File.Exists(StateFile) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void RestoreBounds_()
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        var scale = DeviceDpi / 96.0;                        // 1300 x 900 as the eye sees it, whatever the text scaling
        var size = new Size(Math.Min((int)(1300 * scale), area.Width - 80), Math.Min((int)(900 * scale), area.Height - 60));
        _normal = new Rectangle(area.Left + (area.Width - size.Width) / 2, area.Top + (area.Height - size.Height) / 2, size.Width, size.Height);
        if (ReadState() is { Width: >= 480, Height: >= 360 } s && Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(new Rectangle(s.X, s.Y, s.Width, 40))))
        {
            _normal = new Rectangle(s.X, s.Y, s.Width, s.Height);
            Bounds = _normal;
            if (s.Maximized)
            {
                WindowState = FormWindowState.Maximized;
            }

            return;
        }

        Bounds = _normal;
    }

    private void Remember()
    {
        if (WindowState == FormWindowState.Normal)
        {
            _normal = Bounds;
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var zoom = _web.CoreWebView2 is not null ? _web.ZoomFactor : ReadState()?.Zoom ?? 1.0;
            File.WriteAllText(StateFile, JsonSerializer.Serialize(new State(_normal.X, _normal.Y, _normal.Width, _normal.Height, WindowState == FormWindowState.Maximized, zoom)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"status window: could not remember its place: {ex.Message}");
        }
    }

    private static readonly Lazy<Icon> ExeIcon = new(() => (Environment.ProcessPath is { } exe ? Icon.ExtractAssociatedIcon(exe) : null) ?? AppIcon.Value);

    /// <summary>The icon in the taskbar: an attitude indicator in a rounded square (sky over earth, a white horizon).</summary>
    private static readonly Lazy<Icon> AppIcon = new(() =>
    {
        using var bitmap = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = new GraphicsPath();
            const int r = 14;
            shape.AddArc(2, 2, r * 2, r * 2, 180, 90);
            shape.AddArc(62 - r * 2, 2, r * 2, r * 2, 270, 90);
            shape.AddArc(62 - r * 2, 62 - r * 2, r * 2, r * 2, 0, 90);
            shape.AddArc(2, 62 - r * 2, r * 2, r * 2, 90, 90);
            shape.CloseFigure();
            g.SetClip(shape);
            using var sky = new SolidBrush(Color.FromArgb(0x2f, 0x8f, 0xe0));
            using var earth = new SolidBrush(Color.FromArgb(0x8a, 0x5a, 0x2b));
            g.FillRectangle(sky, 0, 0, 64, 32);
            g.FillRectangle(earth, 0, 32, 64, 32);
            using var horizon = new Pen(Color.White, 3);
            g.DrawLine(horizon, 0, 32, 64, 32);
            using var wings = new Pen(Color.FromArgb(0xff, 0xd0, 0x30), 5) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(wings, 12, 32, 24, 32);
            g.DrawLine(wings, 40, 32, 52, 32);
            g.FillEllipse(Brushes.White, 29, 29, 6, 6);
        }

        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    });

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint handle);
}
