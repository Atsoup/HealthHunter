using System.Drawing;
using System.Drawing.Imaging;

namespace HealthHunter;

/// <summary>
/// Reads an on-screen health bar by color. Works with any renderer (D3D9–12, Vulkan, OpenGL)
/// because it reads the composed desktop image — run the game in windowed or borderless mode.
/// </summary>
public sealed class BarDetector
{
    public BarCalibration Cal { get; set; }

    public bool IsCalibrated => Cal != null && Cal.Width > 1 && Cal.Height > 0 && Cal.FillArgb != 0;
    public Rectangle Region => Cal == null ? Rectangle.Empty : new Rectangle(Cal.X, Cal.Y, Cal.Width, Cal.Height);

    /// <summary>v6: several finders and the assistant read the screen at once; one capture at a time is cheaper and safer.</summary>
    private static readonly object CaptureLock = new();

    public static Bitmap Capture(Rectangle r)
    {
        lock (CaptureLock)
        {
            var bmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(r.Location, Point.Empty, r.Size, CopyPixelOperation.SourceCopy);
            return bmp;
        }
    }

    public static Bitmap CaptureVirtualScreen()
    {
        var vs = SystemInformation.VirtualScreen;
        return Capture(vs);
    }

    /// <summary>Call while the bar is completely full. Learns the fill color.</summary>
    public void Calibrate(Rectangle region)
    {
        Cal ??= new BarCalibration();
        Cal.X = region.X; Cal.Y = region.Y; Cal.Width = region.Width; Cal.Height = region.Height;
        Cal.Vertical = region.Height > region.Width;
        using var bmp = Capture(region);
        var px = ReadPixels(bmp, out int w, out int h);
        var rs = new List<int>(); var gs = new List<int>(); var bs = new List<int>();
        foreach (var (x, y) in SampleLine(w, h, Cal.Vertical))
        {
            int c = px[y * w + x];
            rs.Add((c >> 16) & 255); gs.Add((c >> 8) & 255); bs.Add(c & 255);
        }
        rs.Sort(); gs.Sort(); bs.Sort();
        int m = rs.Count / 2;
        Cal.FillArgb = unchecked((int)0xFF000000) | (rs[m] << 16) | (gs[m] << 8) | bs[m];
        Cal.Anchor = 0;
        Diag.Log($"Bar calibrated: box {Cal.X},{Cal.Y} {Cal.Width}x{Cal.Height} ({(Cal.Vertical ? "vertical" : "horizontal")}), " +
                 $"fill color R{rs[m]} G{gs[m]} B{bs[m]}, tolerance {Cal.Tolerance}.");
    }

    /// <summary>Returns fill fraction 0..1, or NaN if not calibrated.</summary>
    public double Measure(out Bitmap preview)
    {
        preview = null;
        if (!IsCalibrated) return double.NaN;
        preview = Capture(Region);
        return Measure(preview);
    }

    /// <summary>Fraction of sampled pixels in the last measurement that matched the fill color (diagnostics).</summary>
    public double LastMatchRatio { get; private set; }
    /// <summary>True when the last capture was (almost) all black: exclusive fullscreen or HDR blocks screen capture.</summary>
    public bool LastCaptureBlack { get; private set; }

    /// <summary>
    /// v5: finds the fill edge instead of counting matching columns. For every possible edge position it
    /// scores how many columns agree with "filled before the edge, empty after it" and picks the best,
    /// so a few columns of see-through or flashing bar no longer throw the reading off. Bars that drain
    /// toward the other end (right-to-left, or bottom-up for vertical bars) are detected automatically.
    /// </summary>
    public double Measure(Bitmap bmp)
    {
        var px = ReadPixels(bmp, out int w, out int h);
        int fr = (Cal.FillArgb >> 16) & 255, fg = (Cal.FillArgb >> 8) & 255, fb = Cal.FillArgb & 255;
        int tol2 = Cal.Tolerance * Cal.Tolerance;
        int along = Cal.Vertical ? h : w;
        int across = Cal.Vertical ? w : h;
        if (along == 0) return double.NaN;
        // Sample the middle 40% across the bar so borders and text outlines are ignored.
        int a0 = (int)(across * 0.3), a1 = Math.Max(a0 + 1, (int)Math.Ceiling(across * 0.7));
        var filled = new bool[along];
        long matched = 0, sampled = 0, dark = 0;
        for (int i = 0; i < along; i++)
        {
            int hits = 0, total = 0;
            for (int j = a0; j < a1 && j < across; j++)
            {
                int x = Cal.Vertical ? j : i, y = Cal.Vertical ? i : j;
                int c = px[y * w + x];
                int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
                int dr = r - fr, dg = g - fg, db = b - fb;
                if (dr * dr + dg * dg + db * db <= tol2) hits++;
                if (r + g + b < 12) dark++;
                total++;
            }
            filled[i] = total > 0 && hits * 2 >= total;
            matched += hits; sampled += total;
        }
        LastMatchRatio = sampled == 0 ? 0 : (double)matched / sampled;
        LastCaptureBlack = sampled > 0 && dark >= sampled * 0.98;

        // prefix[k] = filled columns in [0, k)
        var prefix = new int[along + 1];
        for (int i = 0; i < along; i++) prefix[i + 1] = prefix[i] + (filled[i] ? 1 : 0);
        int totalFilled = prefix[along];
        int bestStart = 0, bestStartScore = -1, bestEnd = 0, bestEndScore = -1;
        for (int k = 0; k <= along; k++)
        {
            // anchored at start: [0,k) filled, [k,along) empty
            int sStart = prefix[k] + ((along - k) - (totalFilled - prefix[k]));
            if (sStart > bestStartScore) { bestStartScore = sStart; bestStart = k; }
            // anchored at end: [along-k, along) filled, [0, along-k) empty
            int e = along - k;
            int sEnd = (totalFilled - prefix[e]) + (e - prefix[e]);
            if (sEnd > bestEndScore) { bestEndScore = sEnd; bestEnd = k; }
        }
        double fStart = (double)bestStart / along, fEnd = (double)bestEnd / along;
        if (Cal.Anchor == 0)
        {
            // Learn the direction once the bar is clearly not full and one direction fits clearly better.
            int margin = Math.Max(3, along / 20);
            if (bestStartScore >= bestEndScore + margin && fStart > 0.03 && fStart < 0.95) { Cal.Anchor = 1; Diag.Log("Bar: fill is anchored at the left/top end."); }
            else if (bestEndScore >= bestStartScore + margin && fEnd > 0.03 && fEnd < 0.95) { Cal.Anchor = 2; Diag.Log("Bar: fill is anchored at the right/bottom end."); }
        }
        // Until the direction is known, use whichever fits better (vertical bars usually fill from the bottom).
        int anchor = Cal.Anchor != 0 ? Cal.Anchor
                   : bestStartScore > bestEndScore ? 1 : bestEndScore > bestStartScore ? 2 : (Cal.Vertical ? 2 : 1);
        return anchor == 1 ? fStart : fEnd;
    }

    private static IEnumerable<(int, int)> SampleLine(int w, int h, bool vertical)
    {
        if (vertical) { int x = w / 2; for (int y = 0; y < h; y++) yield return (x, y); }
        else { int y = h / 2; for (int x = 0; x < w; x++) yield return (x, y); }
    }

    private static int[] ReadPixels(Bitmap bmp, out int w, out int h)
    {
        w = bmp.Width; h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var px = new int[w * h];
            for (int y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, px, y * w, w);
            return px;
        }
        finally { bmp.UnlockBits(data); }
    }
}

/// <summary>Full-screen overlay: drag a rectangle around the health bar.</summary>
public sealed class RegionPicker : Form
{
    private readonly Bitmap _shot;
    private Point _start;
    private Rectangle _sel;
    private bool _dragging;
    public Rectangle Selected { get; private set; }

    public RegionPicker()
    {
        _shot = BarDetector.CaptureVirtualScreen();
        var vs = SystemInformation.VirtualScreen;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = vs;
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
        MouseDown += (_, e) => { _dragging = true; _start = e.Location; _sel = Rectangle.Empty; };
        MouseMove += (_, e) =>
        {
            if (!_dragging) return;
            _sel = Rectangle.FromLTRB(Math.Min(_start.X, e.X), Math.Min(_start.Y, e.Y), Math.Max(_start.X, e.X), Math.Max(_start.Y, e.Y));
            Invalidate();
        };
        MouseUp += (_, e) =>
        {
            _dragging = false;
            if (_sel.Width >= 4 && _sel.Height >= 2)
            {
                Selected = new Rectangle(_sel.X + vs.X, _sel.Y + vs.Y, _sel.Width, _sel.Height);
                DialogResult = DialogResult.OK;
                Close();
            }
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.DrawImage(_shot, 0, 0);
        using var dim = new SolidBrush(Color.FromArgb(110, 0, 0, 0));
        e.Graphics.FillRectangle(dim, ClientRectangle);
        if (_sel.Width > 0)
        {
            e.Graphics.DrawImage(_shot, _sel, _sel, GraphicsUnit.Pixel);
            using var pen = new Pen(Color.Lime, 2);
            e.Graphics.DrawRectangle(pen, _sel);
        }
        using var f = new Font("Segoe UI", 14, FontStyle.Bold);
        e.Graphics.DrawString("Drag a box tightly around the health bar's colored fill.  Esc to cancel.",
            f, Brushes.White, 24, 24);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _shot.Dispose();
        base.Dispose(disposing);
    }
}
