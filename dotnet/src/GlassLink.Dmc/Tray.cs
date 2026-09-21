using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using GlassLink.Core.Du;
using Microsoft.Win32;

namespace GlassLink.Dmc;

public enum Health
{
    Good,
    Attention,
    Broken,
}

/// <summary>The three things the top bar of the status page shows, in one line each, and the worst of them.</summary>
public sealed record Summary(Health Level, string Sim, string Displays, string Dus, Health SimLevel, Health DisplayLevel, Health DuLevel)
{
    public static Summary Of(DmcRuntime dmc)
    {
        var simWindow = GlassLink.Sim.PopoutProcedure.SimMainWindow() is not null;
        var (simLevel, sim) = !simWindow ? (Health.Attention, "Sim not running")
            : !dmc.Sim.Connected ? (Health.Attention, "Sim starting")
            : !dmc.Camera.InCockpit ? (Health.Attention, "Sim: not in cockpit")
            : (Health.Good, $"Sim: {dmc.Camera.Title}");

        var all = dmc.Displays.All;
        var found = all.Count(e => e.Capture.HasWindow);
        var popout = dmc.Auto?.State.Status;
        var displayLevel = found == all.Count || !simWindow ? Health.Good : popout == "gave_up" ? Health.Broken : Health.Attention;
        var displays = $"Displays {found}/{all.Count}" + (found < all.Count && popout == "running" ? " · popping out" : found < all.Count && popout == "gave_up" ? " · gave up" : "");

        var dus = dmc.Dus.Status().Where(d => d.Alive || d.Display.Length > 0).ToList();
        var gone = dus.Where(d => !d.Alive).Select(d => d.Label.Length > 0 ? d.Label : d.Serial[..8]).ToList();
        var outdated = dus.Count(d => d.Alive && Firmware.IsOutdated(d.Info?.Firmware, dmc.FirmwareVersion));
        var duLevel = dus.Count == 0 || gone.Count > 0 ? Health.Attention : outdated > 0 ? Health.Attention : Health.Good;
        var duText = dus.Count == 0 ? "No DUs" : $"DUs {dus.Count - gone.Count}/{dus.Count}" + (gone.Count > 0 ? $" · {string.Join(", ", gone)} disconnected" : outdated > 0 ? " · firmware update available" : "");

        var worst = (Health)Math.Max((int)simLevel, Math.Max((int)displayLevel, (int)duLevel));
        if (!simWindow && duLevel == Health.Good)
        {
            worst = Health.Good;                             // no sim is the normal state of a PC that is not flying
        }

        return new Summary(worst, sim, displays, duText, simWindow ? simLevel : Health.Good, displayLevel, duLevel);
    }
}

/// <summary>
/// The DMC's face on the desktop: an icon in the notification area whose shape and colour say whether everything is
/// working (a tick, an exclamation mark or a cross: never colour alone), with the three status lines as its tooltip
/// and a small menu. The status page opens in a window of its own.
/// </summary>
public sealed class Tray : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "GlassLink DMC";
    private readonly DmcRuntime _dmc;
    private readonly string _url;
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolStripMenuItem _sim, _displays, _dus, _autostart;
    private readonly Dictionary<Health, Icon> _icons = [];
    private Health? _shown;

    public Tray(DmcRuntime dmc, string url, Action quit)
    {
        (_dmc, _url) = (dmc, url);
        foreach (var level in Enum.GetValues<Health>())
        {
            _icons[level] = Draw(level);
        }

        _sim = new ToolStripMenuItem { Enabled = false };
        _displays = new ToolStripMenuItem { Enabled = false };
        _dus = new ToolStripMenuItem { Enabled = false };
        _autostart = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutostart()) { Checked = AutostartEnabled };
        var menu = new ContextMenuStrip();
        menu.Items.AddRange(
        [
            new ToolStripMenuItem($"GlassLink DMC {dmc.Version}") { Enabled = false }, _sim, _displays, _dus, new ToolStripSeparator(),
            new ToolStripMenuItem("Open status page", null, (_, _) => OpenStatusPage()) { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) },
            new ToolStripMenuItem("Pop out missing displays now", null, (_, _) => dmc.Auto?.Retry()),
            _autostart, new ToolStripSeparator(),
            new ToolStripMenuItem("Quit", null, (_, _) => quit()),
        ]);
        menu.Opening += (_, _) => { TrayMenuStyle.Apply(menu, Palette.Current); Refresh(); };      // follows the system theme, live
        TrayMenuStyle.Apply(menu, Palette.Current);
        _icon = new NotifyIcon { Icon = _icons[Health.Attention], Text = "GlassLink DMC", ContextMenuStrip = menu, Visible = true };
        _icon.DoubleClick += (_, _) => OpenStatusPage();
        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    public void Dispose()
    {
        _timer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        foreach (var icon in _icons.Values)
        {
            icon.Dispose();
        }
    }

    private void Refresh()
    {
        try
        {
            var s = Summary.Of(_dmc);
            var palette = Palette.Current;
            foreach (var (item, text, level) in new[] { (_sim, s.Sim, s.SimLevel), (_displays, s.Displays, s.DisplayLevel), (_dus, s.Dus, s.DuLevel) })
            {
                item.Text = $"{Symbol(level)}  {text}";          // a symbol and a colour: never colour alone
                item.Tag = palette.For(level);
            }

            var tip = $"GlassLink DMC\n{s.Sim}\n{s.Displays}\n{s.Dus}";
            _icon.Text = tip.Length > 127 ? tip[..127] : tip;          // the limit of a notification area tooltip
            if (_shown != s.Level)
            {
                _icon.Icon = _icons[s.Level];
                _shown = s.Level;
            }
        }
        catch (Exception ex)                                 // a timer tick must never take the process down
        {
            _dmc.Log($"tray: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Draws the menu into a picture, light or dark, without showing it: for checking the look (--render-menu).</summary>
    public static void RenderPreview(string file, Palette palette)
    {
        using var menu = new ContextMenuStrip();
        var lines = new[] { (Health.Good, "Sim: FenixA320 CFM SL"), (Health.Attention, "Displays 4/6 · popping out"), (Health.Broken, "DUs 1/2 · DU2 disconnected") };
        menu.Items.Add(new ToolStripMenuItem("GlassLink DMC 0.5.0") { Enabled = false });
        foreach (var (level, text) in lines)
        {
            menu.Items.Add(new ToolStripMenuItem($"{Symbol(level)}  {text}") { Enabled = false, Tag = palette.For(level) });
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open status page") { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) });
        menu.Items.Add(new ToolStripMenuItem("Pop out missing displays now"));
        menu.Items.Add(new ToolStripMenuItem("Start with Windows") { Checked = true });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit"));
        TrayMenuStyle.Apply(menu, palette);
        menu.CreateControl();
        menu.PerformLayout();
        var size = menu.GetPreferredSize(Size.Empty);
        menu.Size = size;
        using var bitmap = new Bitmap(size.Width, size.Height);
        menu.DrawToBitmap(bitmap, new Rectangle(Point.Empty, size));
        bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static string Symbol(Health level) => level switch { Health.Good => "✓", Health.Attention => "!", _ => "✕" };

    /// <summary>The status page in a window of its own (Edge's app mode: no tabs, no address bar); the default browser
    /// if Edge is not there.</summary>
    private void OpenStatusPage()
    {
        try
        {
            var edge = new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }
                .Select(f => Path.Combine(Environment.GetFolderPath(f), "Microsoft", "Edge", "Application", "msedge.exe")).FirstOrDefault(File.Exists);
            Process.Start(edge is not null
                ? new ProcessStartInfo(edge, $"--app={_url} --window-size=1300,900") { UseShellExecute = false }
                : new ProcessStartInfo(_url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _dmc.Log($"could not open the status page: {ex.Message}");
        }
    }

    // -- start with Windows: only ever changed by the user's click ---------------------------------------------
    private static bool AutostartEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunName) is string;
        }
    }

    private void ToggleAutostart()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (AutostartEnabled)
        {
            key.DeleteValue(RunName, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(RunName, $"\"{Environment.ProcessPath}\" --config \"{_dmc.ConfigPath}\"");
        }

        _autostart.Checked = AutostartEnabled;
        _dmc.Log($"start with Windows: {(_autostart.Checked ? "on" : "off")}");
    }

    /// <summary>A disc in the state's colour with the state's symbol in it, drawn at 32 px (Windows scales it down).</summary>
    private static Icon Draw(Health level)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var colour = level switch { Health.Good => Color.FromArgb(0x1f, 0x9d, 0x4a), Health.Attention => Color.FromArgb(0xe0, 0x9a, 0x00), _ => Color.FromArgb(0xd2, 0x2f, 0x2f) };
            using var fill = new SolidBrush(colour);
            g.FillEllipse(fill, 1, 1, 30, 30);
            using var pen = new Pen(Color.White, 4.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            switch (level)
            {
                case Health.Good:
                    g.DrawLines(pen, [new PointF(8.5f, 16.5f), new PointF(14, 22), new PointF(23.5f, 10.5f)]);
                    break;
                case Health.Attention:
                    g.DrawLine(pen, 16, 7.5f, 16, 18);
                    g.FillEllipse(Brushes.White, 13.4f, 21.5f, 5.2f, 5.2f);
                    break;
                default:
                    g.DrawLine(pen, 10, 10, 22, 22);
                    g.DrawLine(pen, 22, 10, 10, 22);
                    break;
            }
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
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint handle);
}
