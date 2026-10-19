namespace HealthHunter;

/// <summary>
/// Small always-on-top message in the screen corner ("Infinite health: ON"), shown when a hotkey fires.
/// Click-through and never takes focus, so the game keeps keyboard and mouse input.
/// Visible over windowed / borderless games; exclusive fullscreen hides it (the beep still plays).
/// </summary>
public sealed class Overlay : Form
{
    private const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
                      WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;

    private readonly Label _text = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
        Font = new Font("Segoe UI", 14f, FontStyle.Bold), ForeColor = Color.White
    };
    private readonly System.Windows.Forms.Timer _hide = new() { Interval = 1800 };

    public Overlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(32, 32, 32);
        Opacity = 0.88;
        Size = new Size(380, 54);
        Controls.Add(_text);
        _hide.Tick += (_, _) => { _hide.Stop(); Hide(); };
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

    public void ShowMessage(string message, bool on)
    {
        _text.Text = message;
        BackColor = on ? Color.FromArgb(30, 120, 50) : Color.FromArgb(48, 48, 48);
        Fit();
        if (!Visible)
        {
            Show();
            Fit(); // the first Show creates the window, which can rescale the font for the monitor's DPI
        }
        _hide.Stop();
        _hide.Start();
    }

    /// <summary>380 × 54 at 100 %, scaled with the message font, and wide enough for the message.</summary>
    private void Fit()
    {
        float k = Theme.ScaleOf(_text.Font, 14f);
        int Px(float v) => Theme.Px(k, v);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        int maxW = Math.Max(Px(120), area.Width - 2 * Px(24));
        var s = TextRenderer.MeasureText(_text.Text, _text.Font, new Size(maxW - Px(32), int.MaxValue), TextFormatFlags.WordBreak);
        Size = new Size(Math.Min(maxW, Math.Max(Px(380), s.Width + Px(32))), Math.Max(Px(54), s.Height + Px(20)));
        Location = new Point(area.Left + Px(24), area.Top + Px(24));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hide.Dispose();
        base.Dispose(disposing);
    }
}
