using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RegionControl;

/// <summary>
/// A 32-bit premultiplied DIB section. GDI+ draws into it through <see cref="Bitmap"/>,
/// and UpdateLayeredWindow reads it through <see cref="Dc"/> with no copy.
/// </summary>
public sealed class Surface : IDisposable
{
    public readonly int Width, Height;
    public readonly IntPtr Bits, HBitmap, Dc;
    public readonly Bitmap Bitmap;
    readonly IntPtr _old;
    int[]? _row;

    public Surface(int w, int h)
    {
        Width = Math.Max(1, w); Height = Math.Max(1, h);
        var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = Width, biHeight = -Height, biPlanes = 1, biBitCount = 32 };
        HBitmap = Gdi.CreateDIBSection(IntPtr.Zero, ref bi, 0, out Bits, IntPtr.Zero, 0);
        if (HBitmap == IntPtr.Zero) throw new OutOfMemoryException($"CreateDIBSection {Width}x{Height} failed");
        Dc = Gdi.CreateCompatibleDC(IntPtr.Zero);
        _old = Gdi.SelectObject(Dc, HBitmap);
        Bitmap = new Bitmap(Width, Height, Width * 4, PixelFormat.Format32bppPArgb, Bits);
    }

    public Graphics CreateGraphics()
    {
        var g = Graphics.FromImage(Bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.CompositingQuality = CompositingQuality.HighSpeed;
        return g;
    }

    /// <summary>Fill a rectangle with one colour, replacing what is there (fast, no blending).</summary>
    public void Fill(Rectangle r, Color c)
    {
        r.Intersect(new Rectangle(0, 0, Width, Height));
        if (r.Width <= 0 || r.Height <= 0) return;
        int v = Premul(c);
        if (_row == null || _row.Length < Width) _row = new int[Width];
        Array.Fill(_row, v, 0, r.Width);
        for (int y = r.Top; y < r.Bottom; y++)
            Marshal.Copy(_row, 0, Bits + (y * Width + r.Left) * 4, r.Width);
    }

    public static int Premul(Color c)
    {
        int a = c.A;
        int r = c.R * a / 255, g = c.G * a / 255, b = c.B * a / 255;
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    public void Dispose()
    {
        Bitmap.Dispose();
        Gdi.SelectObject(Dc, _old);
        Gdi.DeleteDC(Dc);
        Gdi.DeleteObject(HBitmap);
    }
}

/// <summary>Runs a short animation on the UI thread. Progress goes from 0 to 1 over the duration.</summary>
public sealed class Animator : IDisposable
{
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 8 };
    readonly Stopwatch _clock = new();
    Action<float>? _step;
    Action? _done;
    double _ms;

    public Animator() => _timer.Tick += (_, _) => Tick();

    public bool Running => _timer.Enabled;

    /// <summary>Start, replacing any running animation. With animations off, jumps to the end at once.</summary>
    public void Start(double ms, Action<float> step, Action? done = null)
    {
        Stop();
        if (!Theme.Animate || ms <= 0) { step(1); done?.Invoke(); return; }
        _ms = ms; _step = step; _done = done;
        _clock.Restart();
        step(0);
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _step = null; _done = null;
    }

    /// <summary>Jump to the end now and run the done action.</summary>
    public void Finish()
    {
        if (!Running) return;
        var s = _step; var d = _done;
        Stop();
        s?.Invoke(1); d?.Invoke();
    }

    void Tick()
    {
        double t = _clock.Elapsed.TotalMilliseconds / _ms;
        var s = _step;
        if (t >= 1) { var d = _done; Stop(); s?.Invoke(1); d?.Invoke(); }
        else s?.Invoke((float)t);
    }

    public void Dispose() { _timer.Stop(); _timer.Dispose(); }
}

/// <summary>Blur, screen capture, noise, shadow and the acrylic panel.</summary>
public static class Glass
{
    /// <summary>
    /// Where panel backdrops come from: a screen rectangle (physical pixels) in, a small copy out (1/<see cref="Theme.BlurDownscale"/> size).
    /// The app reads the screen; the render check swaps in a sample wallpaper.
    /// </summary>
    public static Func<Rectangle, int, Bitmap?> Source = CaptureSmall;

    /// <summary>Blurred, slightly saturated small copy of a screen rectangle, or null when glass is off.</summary>
    public static Bitmap? Backdrop(Rectangle screen, int radius)
    {
        if (!Theme.Glass || screen.Width < 2 || screen.Height < 2) return null;
        try
        {
            var small = Source(screen, Theme.BlurDownscale);
            if (small == null) return null;
            BlurInPlace(small, radius, Theme.BlurPasses, 1.25);
            return small;
        }
        catch (Exception ex) { DragWatcher.Log("glass: backdrop failed: " + ex.Message); return null; }
    }

    /// <summary>Copies a screen rectangle at 1/factor size with averaging. Reads the screen only.</summary>
    public static Bitmap? CaptureSmall(Rectangle r, int factor)
    {
        int w = Math.Max(1, r.Width / factor), h = Math.Max(1, r.Height / factor);
        IntPtr screen = Gdi.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        try
        {
            using var s = new Surface(w, h);
            Gdi.SetStretchBltMode(s.Dc, 4 /* HALFTONE */);
            Gdi.SetBrushOrgEx(s.Dc, 0, 0, IntPtr.Zero);
            if (!Gdi.StretchBlt(s.Dc, 0, 0, w, h, screen, r.Left, r.Top, r.Width, r.Height, 0x00CC0020 /* SRCCOPY */)) return null;
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            var px = new int[w * h];
            Marshal.Copy(s.Bits, px, 0, px.Length);
            for (int i = 0; i < px.Length; i++) px[i] |= unchecked((int)0xFF000000); // GDI leaves alpha at 0
            Marshal.Copy(px, 0, data.Scan0, px.Length);
            bmp.UnlockBits(data);
            return bmp;
        }
        finally { Gdi.ReleaseDC(IntPtr.Zero, screen); }
    }

    /// <summary>Small copy of part of an image, for the render check.</summary>
    public static Bitmap Downscale(Image src, Rectangle area, int factor)
    {
        int w = Math.Max(1, area.Width / factor), h = Math.Max(1, area.Height / factor);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(src, new Rectangle(0, 0, w, h), area, GraphicsUnit.Pixel);
        return bmp;
    }

    /// <summary>Box blur, run <paramref name="passes"/> times, on an opaque 32-bit image. Optional saturation boost.</summary>
    public static void BlurInPlace(Bitmap bmp, int radius, int passes, double saturation = 1)
    {
        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        var px = new int[w * h];
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        var tmp = new int[px.Length];
        for (int p = 0; p < passes && radius > 0; p++)
        {
            BoxH(px, tmp, w, h, radius);
            BoxV(tmp, px, w, h, radius);
        }
        if (Math.Abs(saturation - 1) > 0.01)
        {
            for (int i = 0; i < px.Length; i++)
            {
                int c = px[i];
                int a = (c >> 24) & 255, r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
                double l = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                r = Clamp(l + (r - l) * saturation, a); g = Clamp(l + (g - l) * saturation, a); b = Clamp(l + (b - l) * saturation, a);
                px[i] = (a << 24) | (r << 16) | (g << 8) | b;
            }
        }
        Marshal.Copy(px, 0, data.Scan0, px.Length);
        bmp.UnlockBits(data);
    }

    static int Clamp(double v, int max) => (int)Math.Clamp(Math.Round(v), 0, max);

    static void BoxH(int[] src, int[] dst, int w, int h, int r)
    {
        int div = 2 * r + 1;
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            int sa = 0, sr = 0, sg = 0, sb = 0;
            for (int i = -r; i <= r; i++) Add(src[row + Math.Clamp(i, 0, w - 1)], ref sa, ref sr, ref sg, ref sb, 1);
            for (int x = 0; x < w; x++)
            {
                dst[row + x] = ((sa / div) << 24) | ((sr / div) << 16) | ((sg / div) << 8) | (sb / div);
                Add(src[row + Math.Min(x + r + 1, w - 1)], ref sa, ref sr, ref sg, ref sb, 1);
                Add(src[row + Math.Max(x - r, 0)], ref sa, ref sr, ref sg, ref sb, -1);
            }
        }
    }

    static void BoxV(int[] src, int[] dst, int w, int h, int r)
    {
        int div = 2 * r + 1;
        for (int x = 0; x < w; x++)
        {
            int sa = 0, sr = 0, sg = 0, sb = 0;
            for (int i = -r; i <= r; i++) Add(src[Math.Clamp(i, 0, h - 1) * w + x], ref sa, ref sr, ref sg, ref sb, 1);
            for (int y = 0; y < h; y++)
            {
                dst[y * w + x] = ((sa / div) << 24) | ((sr / div) << 16) | ((sg / div) << 8) | (sb / div);
                Add(src[Math.Min(y + r + 1, h - 1) * w + x], ref sa, ref sr, ref sg, ref sb, 1);
                Add(src[Math.Max(y - r, 0) * w + x], ref sa, ref sr, ref sg, ref sb, -1);
            }
        }
    }

    static void Add(int c, ref int a, ref int r, ref int g, ref int b, int sign)
    {
        a += sign * ((c >> 24) & 255); r += sign * ((c >> 16) & 255); g += sign * ((c >> 8) & 255); b += sign * (c & 255);
    }

    // ---------- drawing ----------

    static readonly ImageAttributes Clamped = MakeClamped();
    static ImageAttributes MakeClamped() { var a = new ImageAttributes(); a.SetWrapMode(WrapMode.TileFlipXY); return a; }

    /// <summary>Draw a small blurred copy stretched over <paramref name="dest"/>.</summary>
    public static void DrawStretched(Graphics g, Bitmap small, RectangleF dest)
    {
        var old = g.InterpolationMode;
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.DrawImage(small, new[] { dest.Location, new PointF(dest.Right, dest.Top), new PointF(dest.Left, dest.Bottom) },
            new RectangleF(0, 0, small.Width, small.Height), GraphicsUnit.Pixel, Clamped);
        g.InterpolationMode = old;
    }

    static TextureBrush? _noise;
    static int _noiseVersion = -1;

    /// <summary>Fine grain that makes a flat tint read as frosted glass.</summary>
    public static TextureBrush Noise
    {
        get
        {
            if (_noise != null && _noiseVersion == Theme.Version) return _noise;
            _noise?.Dispose();
            var bmp = new Bitmap(128, 128, PixelFormat.Format32bppPArgb);
            var rnd = new Random(7);
            for (int y = 0; y < 128; y++)
                for (int x = 0; x < 128; x++)
                {
                    int a = rnd.Next(Theme.Grain + 1);
                    int v = rnd.Next(2) == 0 ? 0 : 255;
                    bmp.SetPixel(x, y, Color.FromArgb(a, v, v, v));
                }
            _noise = new TextureBrush(bmp, WrapMode.Tile);
            _noiseVersion = Theme.Version;
            return _noise;
        }
    }

    /// <summary>
    /// Acrylic panel: blurred backdrop (or a solid colour when glass is off), tint, grain, top light and a hairline border.
    /// <paramref name="backdropArea"/> is where the small blurred copy maps to, in the same coordinates as <paramref name="r"/>.
    /// </summary>
    public static void Panel(Graphics g, RectangleF r, float radius, Bitmap? blurred, RectangleF backdropArea, float scale, Color? tint = null)
    {
        using var path = Theme.Round(r, radius);
        if (blurred != null)
        {
            using (var tb = new TextureBrush(blurred, WrapMode.TileFlipXY))
            {
                tb.TranslateTransform(backdropArea.X, backdropArea.Y);
                tb.ScaleTransform(backdropArea.Width / blurred.Width, backdropArea.Height / blurred.Height);
                var old = g.InterpolationMode;
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.FillPath(tb, path);
                g.InterpolationMode = old;
            }
            using (var t = new SolidBrush(tint ?? Theme.GlassTint)) g.FillPath(t, path);
            if (Theme.Grain > 0) g.FillPath(Noise, path);
        }
        else
        {
            using var b = new SolidBrush(tint.HasValue ? Theme.Over(tint.Value, Theme.Panel) : Theme.Panel);
            g.FillPath(b, path);
        }
        // Top light: a soft gradient line inside the top edge.
        var hl = new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, Math.Min(r.Height - 1, radius * 2.5f));
        if (hl.Height > 2)
        {
            using var hp = Theme.Round(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), radius - 0.5f);
            using var lb = new LinearGradientBrush(new RectangleF(hl.X, hl.Y - 1, hl.Width, hl.Height + 2), Theme.PanelHighlight, Color.Transparent, LinearGradientMode.Vertical);
            using var lp = new Pen(lb, Math.Max(1f, scale));
            var clip = g.Clip;
            g.SetClip(new RectangleF(hl.X - 1, hl.Y - 1, hl.Width + 2, hl.Height), CombineMode.Intersect);
            g.DrawPath(lp, hp);
            g.Clip = clip;
        }
        using var border = new Pen(Theme.PanelBorder, Math.Max(1f, Theme.Border * scale));
        g.DrawPath(border, path);
    }

    /// <summary>
    /// Fill a rounded rectangle fast: the inside with plain rectangles, only the four corners antialiased.
    /// Looks the same as FillPath with antialiasing, at a fraction of the cost on big areas.
    /// </summary>
    public static void FillRound(Graphics g, Brush b, RectangleF r, float radius)
    {
        int rad = (int)Math.Ceiling(radius);
        if (rad < 2 || r.Width < 4 * rad || r.Height < 4 * rad)
        {
            using var p0 = Theme.Round(r, radius);
            g.FillPath(b, p0);
            return;
        }
        int x0 = (int)Math.Round(r.Left), y0 = (int)Math.Round(r.Top), x1 = (int)Math.Round(r.Right), y1 = (int)Math.Round(r.Bottom);
        var oldSmooth = g.SmoothingMode; var oldOffset = g.PixelOffsetMode;
        g.SmoothingMode = SmoothingMode.None;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.FillRectangle(b, x0, y0 + rad, x1 - x0, y1 - y0 - 2 * rad);
        g.FillRectangle(b, x0 + rad, y0, x1 - x0 - 2 * rad, rad);
        g.FillRectangle(b, x0 + rad, y1 - rad, x1 - x0 - 2 * rad, rad);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.Round(new RectangleF(x0, y0, x1 - x0, y1 - y0), radius);
        var clip = g.Clip;
        foreach (var c in new[] { new Rectangle(x0, y0, rad, rad), new Rectangle(x1 - rad, y0, rad, rad), new Rectangle(x0, y1 - rad, rad, rad), new Rectangle(x1 - rad, y1 - rad, rad, rad) })
        {
            g.SetClip(c, CombineMode.Intersect);
            g.FillPath(b, path);
            g.Clip = clip;
        }
        clip.Dispose();
        g.SmoothingMode = oldSmooth; g.PixelOffsetMode = oldOffset;
    }

    /// <summary>
    /// Fill a rounded rectangle whose whole area is plain <paramref name="under"/> now (for example the zones scrim).
    /// The inside is written straight to memory with the blended colour; only the corners go through GDI+.
    /// </summary>
    public static void FillRoundOver(Surface s, Graphics g, Rectangle clip, Color fill, Color under, RectangleF r, float radius)
    {
        int rad = (int)Math.Ceiling(radius);
        if (rad < 2 || r.Width < 4 * rad || r.Height < 4 * rad)
        {
            using var b0 = new SolidBrush(fill);
            FillRound(g, b0, r, radius);
            return;
        }
        double fa = fill.A / 255.0, ua = under.A / 255.0;
        double oa = fa + ua * (1 - fa);
        Color blended = Color.Transparent;
        if (oa > 0)
        {
            int C(int f, int u) => (int)Math.Clamp(Math.Round((f * fa + u * ua * (1 - fa)) / oa), 0, 255);
            blended = Color.FromArgb((int)Math.Round(oa * 255), C(fill.R, under.R), C(fill.G, under.G), C(fill.B, under.B));
        }
        int x0 = (int)Math.Round(r.Left), y0 = (int)Math.Round(r.Top), x1 = (int)Math.Round(r.Right), y1 = (int)Math.Round(r.Bottom);
        g.Flush(FlushIntention.Sync);
        s.Fill(Rectangle.Intersect(clip, new Rectangle(x0, y0 + rad, x1 - x0, y1 - y0 - 2 * rad)), blended);
        s.Fill(Rectangle.Intersect(clip, new Rectangle(x0 + rad, y0, x1 - x0 - 2 * rad, rad)), blended);
        s.Fill(Rectangle.Intersect(clip, new Rectangle(x0 + rad, y1 - rad, x1 - x0 - 2 * rad, rad)), blended);
        using var path = Theme.Round(new RectangleF(x0, y0, x1 - x0, y1 - y0), radius);
        using var b = new SolidBrush(fill);
        var old = g.Clip;
        foreach (var c in new[] { new Rectangle(x0, y0, rad, rad), new Rectangle(x1 - rad, y0, rad, rad), new Rectangle(x0, y1 - rad, rad, rad), new Rectangle(x1 - rad, y1 - rad, rad, rad) })
        {
            if (!c.IntersectsWith(clip)) continue;
            g.SetClip(c, CombineMode.Intersect);
            g.FillPath(b, path);
            g.Clip = old;
        }
        old.Dispose();
    }

    /// <summary>Frosted panel over content that is already blurred (the editor): tint, top light, hairline border. Cheap.</summary>
    public static void Frosted(Graphics g, RectangleF r, float radius, Color tint, float scale)
    {
        using (var b = new SolidBrush(tint)) FillRound(g, b, r, radius);
        using var path = Theme.Round(r, radius);
        using (var hp = new Pen(Theme.PanelHighlight, Math.Max(1f, scale)))
        {
            var clip = g.Clip;
            g.SetClip(new RectangleF(r.X, r.Y, r.Width, radius), CombineMode.Intersect);
            using var inner = Theme.Round(RectangleF.Inflate(r, -0.5f * scale, -0.5f * scale), radius);
            g.DrawPath(hp, inner);
            g.Clip = clip;
        }
        using var border = new Pen(Theme.PanelBorder, Math.Max(1f, Theme.Border * scale));
        g.DrawPath(border, path);
    }

    static readonly Dictionary<(int, int, int, int, int), Bitmap> _shadows = new();

    /// <summary>Soft drop shadow under a rounded rectangle. Cached per size.</summary>
    public static void Shadow(Graphics g, RectangleF r, float radius, float size, float offsetY)
    {
        if (size < 1) return;
        int w = (int)Math.Ceiling(r.Width), h = (int)Math.Ceiling(r.Height), s = (int)Math.Ceiling(size);
        int f = (long)(w + 2 * s) * (h + 2 * s) > 1_500_000 ? 2 : 1; // big shadows are kept at half size, drawn stretched
        var key = (w, h, (int)radius, s, Theme.Version * 2 + f);
        if (!_shadows.TryGetValue(key, out var bmp))
        {
            if (_shadows.Count > 24) { foreach (var b in _shadows.Values) b.Dispose(); _shadows.Clear(); }
            int mw = (w + 2 * s) / f + 2, mh = (h + 2 * s) / f + 2;
            bmp = new Bitmap(mw, mh, PixelFormat.Format32bppPArgb);
            using (var mg = Graphics.FromImage(bmp))
            {
                mg.SmoothingMode = SmoothingMode.AntiAlias;
                using var p = Theme.Round(new RectangleF((float)s / f, (float)s / f, (float)w / f, (float)h / f), radius / f);
                using var br = new SolidBrush(Theme.Shadow);
                mg.FillPath(br, p);
            }
            BlurInPlace(bmp, Math.Max(1, s / f / 3), 3);
            _shadows[key] = bmp;
        }
        var dest = new RectangleF(r.X - s, r.Y - s + offsetY, bmp.Width * f, bmp.Height * f);
        if (f == 1) { g.DrawImageUnscaled(bmp, (int)Math.Round(dest.X), (int)Math.Round(dest.Y)); return; }
        var old = g.InterpolationMode;
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.DrawImage(bmp, dest, new RectangleF(0, 0, bmp.Width, bmp.Height), GraphicsUnit.Pixel);
        g.InterpolationMode = old;
    }

    /// <summary>Pill-shaped label: tinted rounded box and centred text.</summary>
    public static SizeF Pill(Graphics g, string text, Font font, PointF centre, Color back, Color fore, float padX, float padY, Color? border = null)
    {
        var sz = g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
        var r = new RectangleF(centre.X - sz.Width / 2 - padX, centre.Y - sz.Height / 2 - padY, sz.Width + 2 * padX, sz.Height + 2 * padY);
        using (var p = Theme.Round(r, r.Height / 2))
        {
            using (var b = new SolidBrush(back)) g.FillPath(b, p);
            if (border.HasValue) using (var pen = new Pen(border.Value, 1)) g.DrawPath(pen, p);
        }
        using var tb = new SolidBrush(fore);
        g.DrawString(text, font, tb, new PointF(r.X + padX, r.Y + padY), StringFormat.GenericTypographic);
        return r.Size;
    }
}

/// <summary>
/// Base for our click-through, topmost, never-activated overlay windows.
/// Per-pixel alpha through UpdateLayeredWindow: antialiased corners, soft shadows, cheap fades.
/// All coordinates are physical pixels (the process is per-monitor DPI aware v2).
/// Calling Invalidate() re-renders on the next message loop pass.
/// </summary>
public abstract class OverlayForm : Form
{
    Surface? _surf;
    Point _pos;
    Size _size;
    byte _alpha = 255;
    bool _queued;
    readonly IntPtr _ulw = Marshal.AllocHGlobal(256);

    /// <summary>Time of the last render and push, in ms.</summary>
    public double LastRenderMs { get; private set; }
    protected readonly Animator Anim = new();

    protected OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // LAYERED | TRANSPARENT (click-through) | TOOLWINDOW | NOACTIVATE | TOPMOST
            cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8000000 | 0x8;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_DPICHANGED = 0x02E0;
        const int WM_MOUSEACTIVATE = 0x0021;
        const int WM_NCHITTEST = 0x0084;
        if (m.Msg == WM_DPICHANGED) { m.Result = IntPtr.Zero; return; } // we place ourselves in physical pixels
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)3; return; } // MA_NOACTIVATE
        if (m.Msg == WM_NCHITTEST) { m.Result = (IntPtr)(-1); return; }  // HTTRANSPARENT
        base.WndProc(ref m);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    protected override void OnInvalidated(InvalidateEventArgs e)
    {
        base.OnInvalidated(e);
        if (_queued || !IsHandleCreated || !Visible) return;
        _queued = true;
        BeginInvoke(() => { _queued = false; if (Visible) OnRenderRequest(); });
    }

    /// <summary>Called after Invalidate(). Default: full render.</summary>
    protected virtual void OnRenderRequest() => Render();

    /// <summary>Paint the window content. <paramref name="dirty"/> is the area to repaint, in window coordinates.</summary>
    protected abstract void PaintLayer(Surface s, Graphics g, Rectangle dirty);

    /// <summary>Window size and position in screen pixels.</summary>
    public Rectangle WindowRect => new(_pos, _size);

    protected void SetGeometry(Rectangle r) { _pos = r.Location; _size = r.Size; }

    /// <summary>Render into the surface without pushing to the window. For render checks.</summary>
    public Bitmap Snapshot()
    {
        EnsureSurface();
        using (var g = _surf!.CreateGraphics()) PaintLayer(_surf, g, new Rectangle(Point.Empty, _size));
        // Deep copy: Clone() would share the DIB memory, which dies with the form.
        var copy = new Bitmap(_surf.Width, _surf.Height, PixelFormat.Format32bppPArgb);
        var data = copy.LockBits(new Rectangle(0, 0, copy.Width, copy.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        var row = new byte[_surf.Width * 4];
        for (int y = 0; y < _surf.Height; y++)
        {
            Marshal.Copy(_surf.Bits + y * _surf.Width * 4, row, 0, row.Length);
            Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
        }
        copy.UnlockBits(data);
        return copy;
    }

    /// <summary>Render check: paint (all, or only <paramref name="dirty"/>) without pushing, and return the time in ms.</summary>
    internal double TimePaint(Rectangle? dirty = null)
    {
        EnsureSurface();
        var d = dirty ?? new Rectangle(Point.Empty, _size);
        var sw = Stopwatch.StartNew();
        using (var g = _surf!.CreateGraphics()) { g.SetClip(d); PaintLayer(_surf, g, d); }
        return sw.Elapsed.TotalMilliseconds;
    }

    void EnsureSurface()
    {
        if (_surf != null && _surf.Width == _size.Width && _surf.Height == _size.Height) return;
        _surf?.Dispose();
        _surf = new Surface(_size.Width, _size.Height);
    }

    /// <summary>Repaint (all, or only <paramref name="dirty"/>) and push to the window.</summary>
    public void Render(Rectangle? dirty = null)
    {
        if (_size.Width < 1 || _size.Height < 1) return;
        var sw = Stopwatch.StartNew();
        bool resized = _surf == null || _surf.Width != _size.Width || _surf.Height != _size.Height;
        EnsureSurface();
        var full = new Rectangle(Point.Empty, _size);
        var d = resized || dirty == null ? full : Rectangle.Intersect(dirty.Value, full);
        if (d.Width <= 0 || d.Height <= 0) return;
        using (var g = _surf!.CreateGraphics())
        {
            g.SetClip(d);
            PaintLayer(_surf, g, d);
        }
        Push(d == full ? null : d);
        LastRenderMs = sw.Elapsed.TotalMilliseconds;
    }

    void Push(Rectangle? dirty)
    {
        if (!IsHandleCreated || _surf == null) return;
        // ULW_INFO and the structs it points to live in one unmanaged block.
        IntPtr p = _ulw;
        IntPtr ptDst = p + 64, size = p + 72, ptSrc = p + 80, blend = p + 88, rc = p + 96;
        Marshal.WriteInt32(ptDst, _pos.X); Marshal.WriteInt32(ptDst + 4, _pos.Y);
        Marshal.WriteInt32(size, _size.Width); Marshal.WriteInt32(size + 4, _size.Height);
        Marshal.WriteInt64(ptSrc, 0);
        Marshal.WriteInt32(blend, (0) | (0 << 8) | (_alpha << 16) | (1 << 24)); // AC_SRC_OVER, 0, alpha, AC_SRC_ALPHA
        if (dirty.HasValue)
        {
            var d = dirty.Value;
            Marshal.WriteInt32(rc, d.Left); Marshal.WriteInt32(rc + 4, d.Top); Marshal.WriteInt32(rc + 8, d.Right); Marshal.WriteInt32(rc + 12, d.Bottom);
        }
        var info = new UPDATELAYEREDWINDOWINFO
        {
            cbSize = Marshal.SizeOf<UPDATELAYEREDWINDOWINFO>(),
            pptDst = ptDst, psize = size, hdcSrc = _surf.Dc, pptSrc = ptSrc, pblend = blend,
            dwFlags = 2 /* ULW_ALPHA */, prcDirty = dirty.HasValue ? rc : IntPtr.Zero,
        };
        if (!Gdi.UpdateLayeredWindowIndirect(Handle, ref info) && dirty.HasValue)
        {
            info.prcDirty = IntPtr.Zero; // some drivers refuse a dirty rect: push everything
            Gdi.UpdateLayeredWindowIndirect(Handle, ref info);
        }
    }

    /// <summary>Change only the window alpha and position: no repaint, very cheap.</summary>
    protected void Compose(byte alpha, Point? pos = null)
    {
        _alpha = alpha;
        if (pos.HasValue) _pos = pos.Value;
        if (!IsHandleCreated || _surf == null) return;
        var pt = new POINT { X = _pos.X, Y = _pos.Y };
        int blend = (_alpha << 16) | (1 << 24);
        Gdi.UpdateLayeredWindow(Handle, IntPtr.Zero, ref pt, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref blend, 2);
    }

    protected byte Alpha => _alpha;

    /// <summary>Render at full alpha or <paramref name="alpha"/>, show without activation, and put on top.</summary>
    protected void ShowRendered(byte alpha)
    {
        _alpha = alpha;
        Render();
        if (!Visible) Show();
        BringToTop();
    }

    public void BringToTop() =>
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

    /// <summary>Place and show at a screen rectangle. Kept for existing callers.</summary>
    public virtual void ShowAt(RECT r)
    {
        SetGeometry(new Rectangle(r.Left, r.Top, r.Width, r.Height));
        ShowRendered(255);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) Anim.Stop();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Anim.Dispose(); _surf?.Dispose(); _surf = null; }
        Marshal.FreeHGlobal(_ulw);
        base.Dispose(disposing);
    }
}

[StructLayout(LayoutKind.Sequential)]
struct BITMAPINFOHEADER
{
    public int biSize, biWidth, biHeight;
    public short biPlanes, biBitCount;
    public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
}

[StructLayout(LayoutKind.Sequential)]
struct UPDATELAYEREDWINDOWINFO
{
    public int cbSize;
    public IntPtr hdcDst, pptDst, psize, hdcSrc, pptSrc;
    public int crKey;
    public IntPtr pblend;
    public int dwFlags;
    public IntPtr prcDirty;
}

static class Gdi
{
    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] public static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, int rop);
    [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] public static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr old);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool UpdateLayeredWindowIndirect(IntPtr hwnd, ref UPDATELAYEREDWINDOWINFO info);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, IntPtr psize, IntPtr hdcSrc, IntPtr pptSrc, int crKey, ref int blend, int flags);
    [DllImport("user32.dll")] public static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
}

[StructLayout(LayoutKind.Sequential)]
struct ICONINFO
{
    public bool fIcon;
    public int xHotspot, yHotspot;
    public IntPtr hbmMask, hbmColor;
}
