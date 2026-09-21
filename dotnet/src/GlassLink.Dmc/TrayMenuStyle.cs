using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace GlassLink.Dmc;

/// <summary>The colours of the status page (glasslink/static/admin.html), light and dark, for everything the tray draws.</summary>
public sealed record Palette(Color Background, Color Card, Color Line, Color Text, Color Dim, Color Accent, Color Hover, Color Ok, Color Warn, Color Bad)
{
    public static readonly Palette Dark = new(
        ColorTranslator.FromHtml("#181b22"), ColorTranslator.FromHtml("#181b22"), ColorTranslator.FromHtml("#3a4150"), ColorTranslator.FromHtml("#eceef2"),
        ColorTranslator.FromHtml("#a3acbd"), ColorTranslator.FromHtml("#6cb2ff"), ColorTranslator.FromHtml("#2a3040"),
        ColorTranslator.FromHtml("#4ade6a"), ColorTranslator.FromHtml("#f5c04a"), ColorTranslator.FromHtml("#ff7b7b"));

    public static readonly Palette Light = new(
        ColorTranslator.FromHtml("#ffffff"), ColorTranslator.FromHtml("#ffffff"), ColorTranslator.FromHtml("#c9ced8"), ColorTranslator.FromHtml("#15181e"),
        ColorTranslator.FromHtml("#4f586a"), ColorTranslator.FromHtml("#0b5cad"), ColorTranslator.FromHtml("#e8eef7"),
        ColorTranslator.FromHtml("#116329"), ColorTranslator.FromHtml("#7a4d00"), ColorTranslator.FromHtml("#b3261e"));

    /// <summary>Follows Windows' "Choose your mode" for apps, read each time the menu opens (so it changes live).</summary>
    public static Palette Current
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("AppsUseLightTheme") is int light && light == 0 ? Dark : Light;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                return Light;
            }
        }
    }

    public Color For(Health level) => level switch { Health.Good => Ok, Health.Attention => Warn, _ => Bad };
}

/// <summary>A flat, roomy menu in the palette of the status page: no gradients, no image margin, rounded hover, the
/// system's text size (the font is Windows' own menu font, which follows the accessibility text size).</summary>
public sealed class TrayMenuRenderer(Palette palette) : ToolStripProfessionalRenderer(new Colors(palette))
{
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(palette.Background);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(palette.Line);
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled)
        {
            return;
        }

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
        using var path = Rounded(bounds, 6);
        using var brush = new SolidBrush(palette.Hover);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        // Status lines are disabled items (nothing to click) but must stay fully readable, in the colour of their state.
        e.TextColor = e.Item.Tag is Color tagged ? tagged : e.Item.Enabled ? palette.Text : palette.Dim;
        TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont, e.TextRectangle, e.TextColor, e.TextFormat | TextFormatFlags.VerticalCenter);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(palette.Line);
        e.Graphics.DrawLine(pen, 12, e.Item.Height / 2, e.Item.Width - 12, e.Item.Height / 2);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = e.ImageRectangle;
        using var pen = new Pen(palette.Accent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        e.Graphics.DrawLines(pen, [new PointF(r.Left + 3, r.Top + r.Height / 2f), new PointF(r.Left + r.Width / 2f - 1, r.Bottom - 4), new PointF(r.Right - 3, r.Top + 4)]);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class Colors(Palette p) : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => p.Background;

        public override Color MenuBorder => p.Line;

        public override Color MenuItemBorder => Color.Transparent;

        public override Color MenuItemSelected => p.Hover;

        public override Color ImageMarginGradientBegin => p.Background;

        public override Color ImageMarginGradientMiddle => p.Background;

        public override Color ImageMarginGradientEnd => p.Background;

        public override Color SeparatorDark => p.Line;

        public override Color SeparatorLight => p.Line;
    }
}

public static class TrayMenuStyle
{
    /// <summary>Applies palette, spacing and Windows 11 rounded corners to a menu; call again whenever it opens.</summary>
    public static void Apply(ContextMenuStrip menu, Palette palette)
    {
        menu.Renderer = new TrayMenuRenderer(palette);
        menu.BackColor = palette.Background;
        menu.ForeColor = palette.Text;
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = true;
        menu.DropShadowEnabled = true;
        menu.Padding = new Padding(2, 6, 2, 6);
        menu.Font = SystemFonts.MenuFont ?? menu.Font;
        foreach (ToolStripItem item in menu.Items)
        {
            if (item is ToolStripMenuItem entry)
            {
                entry.Padding = new Padding(6, 5, 14, 5);
                entry.ForeColor = palette.Text;
            }
        }

        var round = 2;                                       // DWMWCP_ROUND
        DwmSetWindowAttribute(menu.Handle, 33, ref round, sizeof(int));       // DWMWA_WINDOW_CORNER_PREFERENCE (Windows 11)
        var dark = ReferenceEquals(palette, Palette.Dark) ? 1 : 0;
        DwmSetWindowAttribute(menu.Handle, 20, ref dark, sizeof(int));        // DWMWA_USE_IMMERSIVE_DARK_MODE: shadow and border follow
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
