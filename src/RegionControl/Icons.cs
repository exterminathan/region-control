using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RegionControl;

/// <summary>
/// App icon, tray icon, cursors and toolbar glyphs, all drawn in code (no emoji, no icon packs).
/// The app icon: a blue-to-cyan rounded square holding three glass zones, one tall and two stacked.
/// </summary>
public static class Icons
{
    public static readonly int[] IcoSizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    // Brand colours: fixed, not the user's accent, so the icon is the same on every PC.
    static readonly Color BrandA = Color.FromArgb(37, 99, 235);   // top left
    static readonly Color BrandB = Color.FromArgb(34, 200, 238);  // bottom right

    /// <summary>The app icon at one size, straight alpha.</summary>
    public static Bitmap RenderApp(int s)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.Clear(Color.Transparent);
        bool tiny = s <= 24;

        float m = tiny ? 0 : s * 0.06f;
        var bg = new RectangleF(m, m, s - 2 * m, s - 2 * m);
        float radius = bg.Width * (tiny ? 0.2f : 0.24f);
        using (var path = Theme.Round(bg, radius))
        {
            if (!tiny)
            {
                // Soft shadow under the tile.
                for (int i = 3; i >= 1; i--)
                {
                    using var sp = Theme.Round(new RectangleF(bg.X - i * s / 200f, bg.Y + i * s / 90f, bg.Width + i * s / 100f, bg.Height), radius);
                    using var sb = new SolidBrush(Color.FromArgb(18, 0, 20, 60));
                    g.FillPath(sb, sp);
                }
            }
            using (var lg = new LinearGradientBrush(new PointF(bg.Left, bg.Top), new PointF(bg.Right, bg.Bottom), BrandA, BrandB))
                g.FillPath(lg, path);
            if (!tiny)
            {
                // Glass sheen on the top half.
                var clip = g.Clip;
                g.SetClip(path);
                using var sheen = new LinearGradientBrush(new RectangleF(bg.X, bg.Y, bg.Width, bg.Height * 0.55f + 1),
                    Color.FromArgb(44, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical);
                g.FillEllipse(sheen, bg.X - bg.Width * 0.3f, bg.Y - bg.Height * 0.62f, bg.Width * 1.6f, bg.Height * 1.1f);
                g.Clip = clip;
                using var rim = new Pen(Color.FromArgb(60, 255, 255, 255), Math.Max(1f, s / 64f));
                using var rp = Theme.Round(RectangleF.Inflate(bg, -s / 128f, -s / 128f), radius);
                g.DrawPath(rim, rp);
            }
        }

        // Three zones.
        float pad = tiny ? Math.Max(3, (float)Math.Round(s * 0.19)) : bg.Width * 0.2f;
        float gap = tiny ? 1 : Math.Max(1.5f, s * 0.05f);
        var inner = new RectangleF(bg.X + pad, bg.Y + pad, bg.Width - 2 * pad, bg.Height - 2 * pad);
        if (tiny) inner = new RectangleF((float)Math.Round(inner.X), (float)Math.Round(inner.Y), (float)Math.Round(inner.Width), (float)Math.Round(inner.Height));
        float leftW = tiny ? (float)Math.Round(inner.Width * 0.55) : inner.Width * 0.56f;
        float rx = inner.X + leftW + gap, rw = inner.Right - rx;
        float topH = tiny ? (float)Math.Floor((inner.Height - gap) / 2) : (inner.Height - gap) / 2;
        var zl = new RectangleF(inner.X, inner.Y, leftW, inner.Height);
        var zt = new RectangleF(rx, inner.Y, rw, topH);
        var zb = new RectangleF(rx, inner.Y + topH + gap, rw, inner.Bottom - (inner.Y + topH + gap));
        float zr = tiny ? Math.Max(0.8f, s * 0.05f) : s * 0.055f;
        if (tiny) g.SmoothingMode = SmoothingMode.None;
        Zone(g, zl, zr, 250, tiny);
        Zone(g, zt, zr, 190, tiny);
        Zone(g, zb, zr, 135, tiny);
        return bmp;
    }

    static void Zone(Graphics g, RectangleF r, float radius, int alpha, bool tiny)
    {
        using var b = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255));
        if (tiny) { g.FillRectangle(b, r); return; }
        using var p = Theme.Round(r, radius);
        g.FillPath(b, p);
    }

    /// <summary>Multi-size .ico bytes: BMP frames up to 64 px, PNG frames for 128 and 256.</summary>
    public static byte[] IcoBytes(int[]? sizes = null)
    {
        sizes ??= IcoSizes;
        var frames = new List<byte[]>();
        foreach (int s in sizes)
        {
            using var bmp = RenderApp(s);
            frames.Add(s >= 128 ? Png(bmp) : Dib(bmp));
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            int s = sizes[i];
            w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(frames[i].Length); w.Write(offset);
            offset += frames[i].Length;
        }
        foreach (var f in frames) w.Write(f);
        w.Flush();
        return ms.ToArray();
    }

    public static void WriteIco(string path) => File.WriteAllBytes(path, IcoBytes());

    static byte[] Png(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    static byte[] Dib(Bitmap bmp)
    {
        int s = bmp.Width;
        var data = bmp.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new byte[s * s * 4];
        for (int y = 0; y < s; y++) Marshal.Copy(data.Scan0 + y * data.Stride, px, y * s * 4, s * 4);
        bmp.UnlockBits(data);
        int maskStride = (s + 31) / 32 * 4;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32);
        w.Write(0); w.Write(s * s * 4 + maskStride * s); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        for (int y = s - 1; y >= 0; y--) w.Write(px, y * s * 4, s * 4); // bottom-up BGRA
        for (int y = s - 1; y >= 0; y--)
        {
            var row = new byte[maskStride];
            for (int x = 0; x < s; x++) if (px[(y * s + x) * 4 + 3] == 0) row[x / 8] |= (byte)(0x80 >> (x % 8));
            w.Write(row);
        }
        w.Flush();
        return ms.ToArray();
    }

    static byte[]? _ico;

    /// <summary>The app icon as an Icon at the given size (the best frame from the in-memory .ico).</summary>
    public static Icon AppIcon(int size)
    {
        _ico ??= IcoBytes();
        using var ms = new MemoryStream(_ico);
        return new Icon(ms, new Size(size, size));
    }

    /// <summary>Tray icon at the system small-icon size for this DPI.</summary>
    public static Icon TrayIcon() => AppIcon(SystemInformation.SmallIconSize.Width);

    // ---------- cursors ----------

    public enum CursorKind { SplitVertical, SplitHorizontal, Draw }

    static readonly Dictionary<(CursorKind, int), Cursor> _cursors = new();

    /// <summary>Custom cursor drawn for the editor. Hotspot in the centre. Falls back to a system cursor.</summary>
    public static Cursor Cursor(CursorKind kind, double scale)
    {
        int s = Math.Max(24, (int)Math.Round(32 * scale));
        if (_cursors.TryGetValue((kind, s), out var c)) return c;
        try
        {
            using var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float mid = s / 2f, w = Math.Max(1.5f, s / 16f);
                using var outline = new Pen(Color.FromArgb(220, 0, 0, 0), w + 2) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                using var white = new Pen(Color.White, w) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                void Line(float x1, float y1, float x2, float y2) { g.DrawLine(outline, x1, y1, x2, y2); g.DrawLine(white, x1, y1, x2, y2); }
                switch (kind)
                {
                    case CursorKind.SplitVertical:
                    case CursorKind.SplitHorizontal:
                        {
                            bool v = kind == CursorKind.SplitVertical;
                            // Dashed cut line with a diamond at the hotspot.
                            float a = s * 0.1f, b = s * 0.9f, seg = s / 9f;
                            for (float t = a; t < b; t += seg * 1.6f)
                            {
                                float e = Math.Min(b, t + seg);
                                if (v) Line(mid, t, mid, e); else Line(t, mid, e, mid);
                            }
                            float d = s * 0.14f;
                            var dia = new[] { new PointF(mid, mid - d), new PointF(mid + d, mid), new PointF(mid, mid + d), new PointF(mid - d, mid) };
                            using (var op = new Pen(Color.FromArgb(220, 0, 0, 0), 2)) g.DrawPolygon(op, dia);
                            g.FillPolygon(Brushes.White, dia);
                            break;
                        }
                    case CursorKind.Draw:
                        {
                            float r = s * 0.36f;
                            Line(mid, mid - r, mid, mid - s * 0.08f); Line(mid, mid + s * 0.08f, mid, mid + r);
                            Line(mid - r, mid, mid - s * 0.08f, mid); Line(mid + s * 0.08f, mid, mid + r, mid);
                            // Small dashed box: "draw a zone".
                            var box = new RectangleF(mid + s * 0.14f, mid + s * 0.14f, s * 0.28f, s * 0.22f);
                            using var bo = new Pen(Color.FromArgb(220, 0, 0, 0), w + 2);
                            using var bw = new Pen(Color.White, w) { DashStyle = DashStyle.Dash };
                            g.DrawRectangle(bo, box.X, box.Y, box.Width, box.Height);
                            g.DrawRectangle(bw, box.X, box.Y, box.Width, box.Height);
                            break;
                        }
                }
            }
            IntPtr hicon = bmp.GetHicon();
            if (!GetIconInfo(hicon, out var info)) { Gdi.DestroyIcon(hicon); throw new InvalidOperationException("GetIconInfo"); }
            info.fIcon = false; info.xHotspot = s / 2; info.yHotspot = s / 2;
            IntPtr hcur = Gdi.CreateIconIndirect(ref info);
            Gdi.DeleteObject(info.hbmColor); Gdi.DeleteObject(info.hbmMask); Gdi.DestroyIcon(hicon);
            if (hcur == IntPtr.Zero) throw new InvalidOperationException("CreateIconIndirect");
            c = new Cursor(hcur);
        }
        catch
        {
            c = kind switch { CursorKind.SplitVertical => Cursors.VSplit, CursorKind.SplitHorizontal => Cursors.HSplit, _ => Cursors.Cross };
        }
        _cursors[(kind, s)] = c;
        return c;
    }

    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr hicon, out ICONINFO info);

    // ---------- glyphs (16-unit design box) ----------

    /// <summary>Draw a toolbar glyph into a square.</summary>
    public static void Glyph(Graphics g, string name, RectangleF r, Color color, float strokePx)
    {
        float k = Math.Min(r.Width, r.Height) / 16f;
        float ox = r.X + (r.Width - 16 * k) / 2, oy = r.Y + (r.Height - 16 * k) / 2;
        PointF P(float x, float y) => new(ox + x * k, oy + y * k);
        RectangleF R(float x, float y, float w, float h) => new(ox + x * k, oy + y * k, w * k, h * k);
        using var pen = new Pen(color, strokePx) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(color);
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        switch (name)
        {
            case "split":
                using (var p = Theme.Round(R(2, 3, 12, 10), 2 * k)) g.DrawPath(pen, p);
                g.DrawLine(pen, P(8, 3), P(8, 13));
                break;
            case "draw":
                using (var dp = new Pen(color, strokePx) { DashStyle = DashStyle.Dash, DashCap = DashCap.Round })
                using (var p = Theme.Round(R(2, 2, 9, 8), 1.5f * k)) g.DrawPath(dp, p);
                g.DrawLine(pen, P(12.5f, 9.5f), P(12.5f, 14.5f));
                g.DrawLine(pen, P(10, 12), P(15, 12));
                break;
            case "move":
                g.DrawLine(pen, P(8, 2), P(8, 14)); g.DrawLine(pen, P(2, 8), P(14, 8));
                g.DrawLines(pen, new[] { P(6, 4), P(8, 2), P(10, 4) });
                g.DrawLines(pen, new[] { P(6, 12), P(8, 14), P(10, 12) });
                g.DrawLines(pen, new[] { P(4, 6), P(2, 8), P(4, 10) });
                g.DrawLines(pen, new[] { P(12, 6), P(14, 8), P(12, 10) });
                break;
            case "undo":
            case "redo":
                {
                    using var p = new GraphicsPath();
                    p.AddLine(P(3.5f, 6.5f), P(10, 6.5f));
                    p.AddArc(R(7, 6.5f, 6, 6), 270, 180);
                    p.AddLine(P(10, 12.5f), P(6, 12.5f));
                    var arrow = new[] { P(6, 4), P(3.5f, 6.5f), P(6, 9) };
                    if (name == "redo")
                    {
                        using var m = new Matrix(-1, 0, 0, 1, 2 * (ox + 8 * k), 0);
                        p.Transform(m);
                        m.TransformPoints(arrow);
                    }
                    g.DrawPath(pen, p);
                    g.DrawLines(pen, arrow);
                    break;
                }
            case "open":
                using (var a = Theme.Round(R(2, 2, 5.5f, 5.5f), 1.2f * k)) g.DrawPath(pen, a);
                using (var b = Theme.Round(R(8.5f, 2, 5.5f, 5.5f), 1.2f * k)) g.DrawPath(pen, b);
                using (var c = Theme.Round(R(2, 8.5f, 5.5f, 5.5f), 1.2f * k)) g.DrawPath(pen, c);
                using (var d = Theme.Round(R(8.5f, 8.5f, 5.5f, 5.5f), 1.2f * k)) g.DrawPath(pen, d);
                break;
            case "save":
                g.DrawLine(pen, P(8, 2), P(8, 10));
                g.DrawLines(pen, new[] { P(4.8f, 7), P(8, 10.2f), P(11.2f, 7) });
                g.DrawLines(pen, new[] { P(2.5f, 10.5f), P(2.5f, 13.5f), P(13.5f, 13.5f), P(13.5f, 10.5f) });
                break;
            case "copy":
                using (var a = Theme.Round(R(5, 5, 9, 9), 1.5f * k)) g.DrawPath(pen, a);
                g.DrawLines(pen, new[] { P(11, 3.2f), P(11, 2.5f), P(2.5f, 2.5f), P(2.5f, 11), P(3.2f, 11) });
                break;
            case "delete":
                g.DrawLine(pen, P(2.5f, 4), P(13.5f, 4));
                g.DrawLines(pen, new[] { P(6, 4), P(6, 2.5f), P(10, 2.5f), P(10, 4) });
                g.DrawLines(pen, new[] { P(4, 4), P(4.8f, 13.5f), P(11.2f, 13.5f), P(12, 4) });
                g.DrawLine(pen, P(6.8f, 6.8f), P(6.8f, 11)); g.DrawLine(pen, P(9.2f, 6.8f), P(9.2f, 11));
                break;
            case "star":
            case "star-outline":
                {
                    var pts = new PointF[10];
                    for (int i = 0; i < 10; i++)
                    {
                        double ang = -Math.PI / 2 + i * Math.PI / 5;
                        float rad = i % 2 == 0 ? 6.8f : 2.9f;
                        pts[i] = P(8 + (float)(Math.Cos(ang) * rad), 8.6f + (float)(Math.Sin(ang) * rad));
                    }
                    if (name == "star") g.FillPolygon(brush, pts); else g.DrawPolygon(pen, pts);
                    break;
                }
            case "close":
                g.DrawLine(pen, P(4, 4), P(12, 12)); g.DrawLine(pen, P(12, 4), P(4, 12));
                break;
            case "monitor":
                using (var p = Theme.Round(R(1.5f, 2.5f, 13, 9), 1.5f * k)) g.DrawPath(pen, p);
                g.DrawLine(pen, P(8, 11.5f), P(8, 13.5f)); g.DrawLine(pen, P(5, 13.8f), P(11, 13.8f));
                break;
            case "grip":
                for (int i = 0; i < 3; i++) for (int j = 0; j < 2; j++)
                    g.FillEllipse(brush, R(5.2f + j * 4 - 1.1f, 3.5f + i * 4.5f - 1.1f, 2.2f, 2.2f));
                break;
            case "chevron":
                g.DrawLines(pen, new[] { P(5, 6.5f), P(8, 9.5f), P(11, 6.5f) });
                break;
            case "plus":
                g.DrawLine(pen, P(8, 3.5f), P(8, 12.5f)); g.DrawLine(pen, P(3.5f, 8), P(12.5f, 8));
                break;
            case "minus":
                g.DrawLine(pen, P(3.5f, 8), P(12.5f, 8));
                break;
            case "grid":
                using (var p = Theme.Round(R(2, 2, 12, 12), 1.5f * k)) g.DrawPath(pen, p);
                g.DrawLine(pen, P(6, 2), P(6, 14)); g.DrawLine(pen, P(10, 2), P(10, 14));
                g.DrawLine(pen, P(2, 6), P(14, 6)); g.DrawLine(pen, P(2, 10), P(14, 10));
                break;
            case "check":
                g.DrawLines(pen, new[] { P(3, 8.5f), P(6.5f, 12), P(13, 4.5f) });
                break;
            case "info":
                g.DrawEllipse(pen, R(2, 2, 12, 12));
                g.DrawLine(pen, P(8, 7.5f), P(8, 11.2f));
                g.FillEllipse(brush, R(7.1f, 4.2f, 1.8f, 1.8f));
                break;
            case "pencil":
                // Diagonal pencil: body from the lower-left tip to the upper-right cap, with a cap line.
                g.DrawPolygon(pen, new[] { P(2.5f, 13.5f), P(3.2f, 10.4f), P(11, 2.6f), P(13.4f, 5), P(5.6f, 12.8f) });
                g.DrawLine(pen, P(9.4f, 4.2f), P(11.8f, 6.6f));
                break;
            case "warn":
                g.DrawPolygon(pen, new[] { P(8, 2.5f), P(14, 13), P(2, 13) });
                g.DrawLine(pen, P(8, 6.5f), P(8, 9.5f));
                g.FillEllipse(brush, R(7.15f, 10.6f, 1.7f, 1.7f));
                break;
        }
        g.SmoothingMode = old;
    }
}
