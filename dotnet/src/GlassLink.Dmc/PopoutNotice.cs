using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using GlassLink.Sim;

namespace GlassLink.Dmc;

/// <summary>
/// A notice across the top of the sim's window while the automatic pop-out is about to move the camera and while it
/// does ("pops out the PFD in 3 s: hands off mouse and keyboard"). It never takes the focus and lets every click
/// through: the pop-out clicks into the sim and checks that the sim is in front, and the user's own clicks must reach
/// the sim too. Shown by the tray; a DMC without one says it on the status page only.
/// </summary>
public sealed class PopoutNotice : Form
{
    private const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    private static readonly nint HwndTopmost = -1;
    private readonly SynchronizationContext _ui;
    private readonly Label _label;

    /// <summary>Made on the UI thread (the tray's).</summary>
    public PopoutNotice()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(0x1b, 0x20, 0x27);
        Opacity = 0.92;
        _label = new Label
        {
            Dock = DockStyle.Fill, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 13f, FontStyle.Bold), Padding = new Padding(12, 0, 12, 0),
        };
        Controls.Add(_label);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    /// <summary>Shows the text over the sim (from any thread), or hides the notice for null.</summary>
    public void Say(string? text) => _ui.Post(_ => Apply(text), null);

    private void Apply(string? text)
    {
        if (IsDisposed)
        {
            return;
        }

        if (text is null || PopoutProcedure.SimMainWindow() is not { } sim)
        {
            Hide();
            return;
        }

        _label.Text = text;
        var (w, h) = (Math.Min(sim.Client.Width - 40, LogicalToDeviceUnits(820)), LogicalToDeviceUnits(60));
        // physical pixels, as the sim's window is measured (the DMC is per-monitor DPI aware)
        SetWindowPos(Handle, HwndTopmost, sim.Client.Left + (sim.Client.Width - w) / 2, sim.Client.Top + LogicalToDeviceUnits(36), w, h,
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
        Visible = true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
}
