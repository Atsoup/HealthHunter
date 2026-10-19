using System.Drawing;
using System.Drawing.Imaging;

namespace HealthHunter;

/// <summary>A word Windows OCR read, in the pixels of the image it was given.</summary>
public sealed class OcrWord
{
    public string Text;
    public Rectangle Rect;
    public int Line;
}

/// <summary>
/// v6: reads text off a screenshot with the OCR built into Windows 10/11 (Windows.Media.Ocr; no download, no
/// cloud). Used to spot numbers on the HUD (ammo, gold, XP, stat screens) so they can be found in memory. The whole
/// WinRT side lives in <see cref="Impl"/> so that on a system without it (older Windows, Wine) the first call fails
/// once and the assistant carries on without numbers.
/// </summary>
public static class Ocr
{
    private static bool? _ok;
    private static readonly object _lock = new();
    /// <summary>Why OCR is off, when it is.</summary>
    public static string Unavailable { get; private set; } = "";

    public static bool Available
    {
        get
        {
            lock (_lock)
            {
                if (_ok == null)
                {
                    try { _ok = Impl.Init(); if (_ok == false) Unavailable = "no OCR language is installed in Windows"; }
                    catch (Exception ex) { _ok = false; Unavailable = ex.GetType().Name + ": " + ex.Message; }
                    Diag.Log(_ok == true ? $"OCR: Windows OCR ready ({Impl.LanguageTag})." : "OCR: not available: " + Unavailable);
                }
                return _ok.Value;
            }
        }
    }

    /// <summary>
    /// Reads all words in the bitmap. <paramref name="scale"/> enlarges small HUD text before reading (2 = double);
    /// the rectangles returned are in the bitmap's own pixels.
    /// </summary>
    public static List<OcrWord> Read(Bitmap bmp, int scale = 1)
    {
        if (!Available) return new();
        Bitmap src = bmp;
        try
        {
            if (scale > 1)
            {
                src = new Bitmap(bmp.Width * scale, bmp.Height * scale, PixelFormat.Format32bppArgb);
                using var g = Graphics.FromImage(src);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(bmp, 0, 0, src.Width, src.Height);
            }
            var words = Impl.Read(src);
            if (scale > 1)
                foreach (var w in words)
                    w.Rect = new Rectangle(w.Rect.X / scale, w.Rect.Y / scale, Math.Max(1, w.Rect.Width / scale), Math.Max(1, w.Rect.Height / scale));
            return words;
        }
        catch (Exception ex)
        {
            Diag.Log("OCR: read failed: " + ex.Message);
            return new();
        }
        finally { if (!ReferenceEquals(src, bmp)) src.Dispose(); }
    }

    /// <summary>The WinRT calls, kept apart so the rest of the app never touches them when OCR is missing.</summary>
    private static class Impl
    {
        private static Windows.Media.Ocr.OcrEngine _engine;
        public static string LanguageTag => _engine?.RecognizerLanguage?.LanguageTag ?? "";

        public static bool Init()
        {
            _engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
                      ?? Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
            return _engine != null;
        }

        public static List<OcrWord> Read(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            int max = (int)Windows.Media.Ocr.OcrEngine.MaxImageDimension;
            if (w > max || h > max) return new();
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            byte[] bytes;
            try
            {
                bytes = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * w * 4, w * 4);
            }
            finally { bmp.UnlockBits(data); }
            var writer = new Windows.Storage.Streams.DataWriter();
            writer.WriteBytes(bytes);
            var buffer = writer.DetachBuffer();
            using var sb = Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer(buffer, Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, w, h,
                                                                                         Windows.Graphics.Imaging.BitmapAlphaMode.Ignore);
            var result = _engine.RecognizeAsync(sb).AsTask().GetAwaiter().GetResult();
            var words = new List<OcrWord>();
            int line = 0;
            foreach (var l in result.Lines)
            {
                foreach (var wd in l.Words)
                {
                    var r = wd.BoundingRect;
                    words.Add(new OcrWord { Text = wd.Text, Line = line, Rect = new Rectangle((int)r.X, (int)r.Y, (int)Math.Ceiling(r.Width), (int)Math.Ceiling(r.Height)) });
                }
                line++;
            }
            return words;
        }
    }
}
