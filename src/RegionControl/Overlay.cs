using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace RegionControl;

/// <summary>Draws a layout as a small rounded card of mini zones.</summary>
public static class Thumb
{
    /// <summary>Space between the card edge and its mini zones, and between mini zones, in logical px.</summary>
    public const float Pad = 4, Gap = 2.5f;

    /// <summary>Mini rectangles of each zone inside a card, for a monitor of the given work size.</summary>
    public static Rectangle[] MiniRects(ZoneLayout layout, Rectangle cell, int workW, int workH, double scale)
    {
        var work = new RECT(0, 0, Math.Max(1, workW), Math.Max(1, workH));
        int pad = Math.Max(2, (int)Math.Round(Pad * scale));
        int half = Math.Max(1, (int)Math.Round(Gap * scale / 2));
        var inner = Rectangle.Inflate(cell, -pad, -pad);
        var res = new Rectangle[layout.Zones.Count];
        for (int i = 0; i < res.Length; i++)
        {
            var r = layout.ZoneRect(i, work);
            int l = inner.Left + (int)Math.Round(r.Left * (double)inner.Width / work.Width);
            int t = inner.Top + (int)Math.Round(r.Top * (double)inner.Height / work.Height);
            int rr = inner.Left + (int)Math.Round(r.Right * (double)inner.Width / work.Width);
            int b = inner.Top + (int)Math.Round(r.Bottom * (double)inner.Height / work.Height);
            res[i] = Rectangle.FromLTRB(l + half, t + half, Math.Max(l + half + 1, rr - half), Math.Max(t + half + 1, b - half));
        }
        return res;
    }

    /// <summary>
    /// Card with mini zones. <paramref name="hot"/> (0..1) lights the card and draws the accent ring;
    /// <paramref name="hoverZone"/> fills that mini zone with the accent.
    /// <paramref name="under"/> is the opaque colour behind the card, used to keep overlapping mini zones apart.
    /// </summary>
    public static void Draw(Graphics g, ZoneLayout layout, Rectangle cell, int workW, int workH, double scale, int hoverZone, float hot, Color under)
    {
        float s = (float)scale;
        var cf = new RectangleF(cell.X, cell.Y, cell.Width, cell.Height);
        float rc = Theme.RadiusCard * s;
        var cardColor = Theme.Mix(Theme.Card, Theme.CardHot, hot);
        var cardOpaque = Theme.Over(cardColor, under);
        using (var p = Theme.Round(cf, rc))
        using (var b = new SolidBrush(cardColor))
            g.FillPath(b, p);

        var minis = MiniRects(layout, cell, workW, workH, scale);
        var order = layout.PaintOrder(new RECT(0, 0, Math.Max(1, workW), Math.Max(1, workH)));
        // Translucent glass minis; a floating mini first blanks what lies under it, so it reads as on top.
        var mini = Theme.Mix(Theme.MiniZone, Theme.A(Theme.Dark ? Color.White : Color.Black, Theme.Dark ? 0.40 : 0.24), hot * 0.6f);
        float rm = Theme.RadiusMini * s;
        using var moat = new Pen(cardOpaque, Math.Max(1f, Gap * s));
        using var underBrush = new SolidBrush(cardOpaque);
        using var zb = new SolidBrush(mini);
        using var hb = new SolidBrush(Theme.Accent);
        using var edge = new Pen(Theme.MiniZoneEdge, 1);
        var painted = new List<Rectangle>();
        foreach (int i in order)
        {
            var r = minis[i];
            using var p = Theme.Round(new RectangleF(r.X, r.Y, r.Width, r.Height), rm);
            if (painted.Any(o => o.IntersectsWith(r)))
            {
                g.DrawPath(moat, p);
                g.FillPath(underBrush, p);
            }
            g.FillPath(i == hoverZone ? hb : zb, p);
            if (i != hoverZone) g.DrawPath(edge, p);
            painted.Add(r);
        }

        if (hot > 0.01f)
        {
            using var glow = new Pen(Theme.A(Theme.Accent, 0.28 * hot), 5 * s);
            using var ring = new Pen(Theme.A(Theme.Accent, hot), 2 * s);
            using var gp = Theme.Round(RectangleF.Inflate(cf, 1.5f * s, 1.5f * s), rc + 1.5f * s);
            using var rp = Theme.Round(RectangleF.Inflate(cf, 0.5f * s, 0.5f * s), rc + 0.5f * s);
            g.DrawPath(glow, gp);
            g.DrawPath(ring, rp);
        }
    }

    /// <summary>Layout name under a card: optional accent star, then the name, centred and cut with an ellipsis.</summary>
    public static void Label(Graphics g, string name, bool star, RectangleF r, Font font, Color color, double scale)
    {
        float s = (float)scale;
        float starW = star ? 12 * s : 0, starGap = star ? 4 * s : 0;
        var fmt = new StringFormat(StringFormat.GenericTypographic) { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        var size = g.MeasureString(name, font, new SizeF(Math.Max(1, r.Width - starW - starGap), r.Height), fmt);
        float total = starW + starGap + size.Width;
        float x = r.X + (r.Width - total) / 2;
        float y = r.Y + (r.Height - size.Height) / 2;
        if (star) Icons.Glyph(g, "star", new RectangleF(x, r.Y + (r.Height - starW) / 2 - 0.5f * s, starW, starW), Theme.Accent, 1);
        using var tb = new SolidBrush(color);
        g.DrawString(name, font, tb, new RectangleF(x + starW + starGap, y, size.Width + 2, size.Height + 2), fmt);
    }
}

/// <summary>Full-screen view of one layout's zones on a monitor. The zone under the cursor lights up in the accent colour.</summary>
public class ZonesForm : OverlayForm
{
    public ZoneLayout? Current { get; private set; }
    public RECT Work { get; private set; }
    public int Hover = -1;
    double _scale = 1;

    Rectangle[] _rects = Array.Empty<Rectangle>();   // zone rectangles drawn, window coordinates (with gaps)
    List<int> _order = new();
    int _hotZone = -1, _coolZone = -1;               // zone lighting up, zone fading out
    float _hotT = 1, _coolT;
    double _dirtyMs;                                 // cost of the last partial render
    readonly Animator _fade = new();
    Font? _fName, _fSize;

    public void ShowLayout(ZoneLayout layout, RECT work, double scale, bool show = true)
    {
        bool wasVisible = Visible;
        Current = layout; Work = work; Hover = -1;
        _hotZone = -1; _coolZone = -1; _hotT = 1; _coolT = 0;
        if (Math.Abs(scale - _scale) > 0.001 || _fName == null)
        {
            _fName?.Dispose(); _fSize?.Dispose();
            _fName = Theme.DisplayFont(17 * scale, true);
            _fSize = Theme.UiFont(12.5 * scale);
        }
        _scale = scale;
        var local = new RECT(0, 0, work.Width, work.Height);
        _order = layout.PaintOrder(local).ToList();
        int half = (int)Math.Round(Theme.ZoneGap * scale / 2);
        _rects = new Rectangle[layout.Zones.Count];
        for (int i = 0; i < _rects.Length; i++)
        {
            var r = layout.ZoneRect(i, local);
            var rc = new Rectangle(r.Left, r.Top, r.Width, r.Height);
            _rects[i] = rc.Width > 4 * half && rc.Height > 4 * half ? Rectangle.Inflate(rc, -half, -half) : rc;
        }
        SetGeometry(new Rectangle(work.Left, work.Top, work.Width, work.Height));
        if (!show) return;
        Anim.Stop();
        if (!wasVisible)
        {
            ShowRendered(Theme.Animate ? (byte)0 : (byte)255);
            _fade.Start(Theme.FadeInMs, t => Compose((byte)Math.Round(255 * Theme.Ease(t))));
        }
        else
        {
            ShowRendered(Theme.Animate ? (byte)150 : (byte)255);
            _fade.Start(Theme.LayoutSwitchMs, t => Compose((byte)Math.Round(150 + 105 * Theme.Ease(t))));
        }
    }

    public int HitTest(POINT p) => Current?.HitTest(Work, p) ?? -1;

    /// <summary>Render check: light a zone fully, with no animation.</summary>
    internal void SetHoverForRender(int zone) { Hover = zone; _hotZone = zone; _hotT = 1; _coolZone = -1; }

    Rectangle ZoneBounds(int i) => i >= 0 && i < _rects.Length ? Rectangle.Inflate(_rects[i], (int)(Theme.ZoneGap * _scale), (int)(Theme.ZoneGap * _scale)) : Rectangle.Empty;

    static Rectangle Union(params Rectangle[] rs)
    {
        Rectangle u = Rectangle.Empty;
        foreach (var r in rs) if (!r.IsEmpty) u = u.IsEmpty ? r : Rectangle.Union(u, r);
        return u;
    }

    protected override void OnRenderRequest()
    {
        if (Hover == _hotZone) { if (!Anim.Running) Render(); return; }
        var dirty = Union(ZoneBounds(_hotZone), ZoneBounds(_coolZone), ZoneBounds(Hover));
        _coolZone = _hotZone; float cool0 = _hotZone >= 0 ? _hotT : 0;
        _hotZone = Hover; _hotT = 0; _coolT = cool0;
        if (!Theme.Animate || _dirtyMs > 7)
        {
            _hotT = 1; _coolT = 0;
            PartialRender(dirty);
            return;
        }
        Anim.Start(Theme.HighlightMs, t =>
        {
            float e = Theme.Ease(t);
            _hotT = e; _coolT = cool0 * (1 - e);
            PartialRender(dirty);
        }, () => _coolZone = -1);
    }

    void PartialRender(Rectangle dirty)
    {
        if (dirty.IsEmpty) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Render(dirty);
        _dirtyMs = sw.Elapsed.TotalMilliseconds;
    }

    protected override void PaintLayer(Surface s, Graphics g, Rectangle dirty)
    {
        s.Fill(dirty, Theme.Scrim);
        if (Current == null) return;
        float sc = (float)_scale;
        float radius = Theme.RadiusZone * sc, half = Theme.ZoneGap * sc / 2;
        using var scrim = new SolidBrush(Theme.Scrim);
        bool first = true;
        foreach (int i in _order)
        {
            var r = _rects[i];
            if (!Rectangle.Inflate(r, (int)half + 2, (int)half + 2).IntersectsWith(dirty)) { first = false; continue; }
            var rf = new RectangleF(r.X, r.Y, r.Width, r.Height);
            if (!first)
            {
                // Cut a gap around a zone that lies over a bigger one.
                g.CompositingMode = CompositingMode.SourceCopy;
                g.SmoothingMode = SmoothingMode.None;
                using (var moat = Theme.Round(RectangleF.Inflate(rf, half, half), radius + half)) g.FillPath(scrim, moat);
                g.CompositingMode = CompositingMode.SourceOver;
                g.SmoothingMode = SmoothingMode.AntiAlias;
            }
            first = false;
            float t = i == _hotZone ? _hotT : i == _coolZone ? _coolT : 0;
            PaintZone(s, g, dirty, i, rf, radius, sc, t);
        }
    }

    void PaintZone(Surface s, Graphics g, Rectangle dirty, int i, RectangleF r, float radius, float sc, float t)
    {
        using var path = Theme.Round(r, radius);
        Glass.FillRoundOver(s, g, dirty, Theme.Mix(Theme.ZoneFill, Theme.ZoneHotFill, t), Theme.Scrim, r, radius);
        // Light from the top: a soft inner line along the upper edge.
        using (var hl = new Pen(Color.FromArgb((int)(40 + 50 * t), 255, 255, 255), Math.Max(1, 2 * sc)))
        {
            var clip = g.Clip;
            g.SetClip(new RectangleF(r.X, r.Y, r.Width, radius * 1.5f), CombineMode.Intersect);
            using var inner = Theme.Round(RectangleF.Inflate(r, -2.5f * sc, -2.5f * sc), radius - 2 * sc);
            g.DrawPath(hl, inner);
            g.Clip = clip;
        }
        float bw = (1.5f + 1.2f * t) * sc;
        using (var pen = new Pen(Theme.Mix(Theme.ZoneEdge, Theme.ZoneHotEdge, t), bw) { Alignment = PenAlignment.Inset })
            g.DrawPath(pen, path);

        // Label: zone name and size in a pill at the centre.
        if (_fName == null || _fSize == null || r.Width < 60 * sc || r.Height < 40 * sc) return;
        var zone = Current!.Zones[i];
        string name = zone.Name.Length > 0 ? zone.Name : $"Zone {i + 1}";
        var full = Current.ZoneRect(i, new RECT(0, 0, Work.Width, Work.Height));
        string size = $"{full.Width} × {full.Height}";
        var fmt = StringFormat.GenericTypographic;
        var ns = g.MeasureString(name, _fName, PointF.Empty, fmt);
        var ss = g.MeasureString(size, _fSize, PointF.Empty, fmt);
        bool two = r.Height >= 90 * sc;
        float padX = 14 * sc, padY = 8 * sc, lineGap = 2 * sc;
        float w = Math.Min(r.Width - 12 * sc, Math.Max(ns.Width, two ? ss.Width : 0) + 2 * padX);
        float h = ns.Height + (two ? ss.Height + lineGap : 0) + 2 * padY;
        var pill = new RectangleF(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
        using (var pp = Theme.Round(pill, Math.Min(h / 2, 14 * sc)))
        {
            using var pb = new SolidBrush(Theme.Mix(Color.FromArgb(200, 22, 23, 28), Theme.Accent, t));
            g.FillPath(pb, pp);
            using var pe = new Pen(Color.FromArgb((int)(40 * (1 - t)), 255, 255, 255), 1);
            g.DrawPath(pe, pp);
        }
        var fore = Theme.Mix(Color.White, Theme.OnAccent, t);
        var trim = new StringFormat(fmt) { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap, Alignment = StringAlignment.Center };
        using (var tb = new SolidBrush(fore))
            g.DrawString(name, _fName, tb, new RectangleF(pill.X + padX / 2, pill.Y + padY, pill.Width - padX, ns.Height + 2), trim);
        if (two)
            using (var sb = new SolidBrush(Theme.A(fore, 0.72)))
                g.DrawString(size, _fSize, sb, new RectangleF(pill.X + padX / 2, pill.Y + padY + ns.Height + lineGap, pill.Width - padX, ss.Height + 2), trim);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) _fade.Stop();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _fade.Dispose(); _fName?.Dispose(); _fSize?.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>The picker strip at the top of a monitor: a glass panel with one card per layout, main layout first.</summary>
public class StripForm : OverlayForm
{
    public List<ZoneLayout> Layouts = new();
    public string? MainId;
    public RECT Work;                           // work area of the monitor the strip is on
    public RECT PanelRect { get; private set; } // panel bounds on screen (the window adds a shadow margin)
    public double DpiScale = 1;
    public int Active = -1;                     // layout shown full screen
    public int HoverZone = -1;                  // zone under the cursor, in the active layout

    readonly List<Rectangle> _cells = new();    // panel coordinates
    readonly List<Rectangle[]> _minis = new();
    Rectangle _pill, _pillHit;                  // "Layout" edit pill, drawn and hit areas, panel coordinates
    float _pillT;                               // pill highlight, 0..1
    readonly Animator _pillAnim = new();
    float[] _hot = Array.Empty<float>();
    int _shownActive = -1;
    int _labelH, _margin;
    Bitmap? _blur;
    Font? _font;
    readonly Animator _slide = new();

    int S(double v) => (int)Math.Round(v * DpiScale);

    /// <summary>Place the strip centred at the top of the monitor and slide it in.</summary>
    public void Place(MonitorInfo mon, List<ZoneLayout> layouts, string? mainId, bool show = true)
    {
        Layouts = layouts; MainId = mainId; Work = mon.Work; Active = -1; HoverZone = -1; _shownActive = -1;
        if (Math.Abs(mon.Scale - DpiScale) > 0.001 || _font == null)
        {
            _font?.Dispose();
            _font = Theme.UiFont(12.5 * mon.Scale);
        }
        DpiScale = mon.Scale;
        int n = Math.Max(1, layouts.Count);
        int pad = S(12), gap = S(10);
        _labelH = S(24);
        // The "Layout" pill sits at the far right, after a divider.
        int pillW = S(104), pillGap = S(18);
        int cellW = Math.Min(S(150), (mon.Bounds.Width - S(40) - 2 * pad - (n - 1) * gap - pillGap - pillW) / n);
        cellW = Math.Max(S(40), cellW);
        int cellH = Math.Max(S(16), (int)Math.Round(cellW * (double)Work.Height / Math.Max(1, Work.Width)));
        int w = pad * 2 + n * cellW + (n - 1) * gap + pillGap + pillW;
        int pillH = Math.Min(S(36), cellH + _labelH - S(4));
        int pillX = w - pad - pillW;
        _pill = new Rectangle(pillX, pad + Math.Max(0, (cellH - pillH) / 2), pillW, pillH);
        _pillHit = new Rectangle(pillX - pillGap / 2, pad, pillW + pillGap / 2 + pad / 2, cellH + _labelH);
        PillHover = false; _pillT = 0; _pillAnim.Stop();
        int h = pad + cellH + _labelH + S(4);
        int left = mon.Bounds.Left + (mon.Bounds.Width - w) / 2;
        int top = mon.Bounds.Top + S(10);
        PanelRect = new RECT(left, top, left + w, top + h);

        _cells.Clear(); _minis.Clear();
        for (int i = 0; i < layouts.Count; i++)
        {
            var cell = new Rectangle(pad + i * (cellW + gap), pad, cellW, cellH);
            _cells.Add(cell);
            _minis.Add(Thumb.MiniRects(layouts[i], cell, Work.Width, Work.Height, DpiScale));
        }
        _hot = new float[layouts.Count];

        _blur?.Dispose();
        _blur = Glass.Backdrop(new Rectangle(left, top, w, h), Theme.BlurRadiusSmall);

        _margin = S(Theme.ShadowSize + 6);
        var win = new Rectangle(left - _margin, top - _margin, w + 2 * _margin, h + 2 * _margin);
        Anim.Stop(); _slide.Stop();
        if (!show) { SetGeometry(win); return; }
        int slide = S(Theme.SlidePx);
        SetGeometry(Theme.Animate ? new Rectangle(win.X, win.Y - slide, win.Width, win.Height) : win);
        ShowRendered(Theme.Animate ? (byte)0 : (byte)255);
        _slide.Start(Theme.SlideInMs, t =>
        {
            float e = Theme.Ease(t);
            Compose((byte)Math.Round(255 * Math.Min(1, t * 1.6)), new Point(win.X, win.Y - (int)Math.Round(slide * (1 - e))));
        });
    }

    /// <summary>Render check: set the active card and mini zone, fully lit, with no animation.</summary>
    internal void SetActiveForRender(int layout, int zone)
    {
        Active = layout; HoverZone = zone; _shownActive = layout;
        for (int i = 0; i < _hot.Length; i++) _hot[i] = i == layout ? 1 : 0;
    }

    /// <summary>Render check: light the "Layout" pill fully, with no animation.</summary>
    internal void SetPillForRender(bool on) { PillHover = on; _pillT = on ? 1 : 0; }

    /// <summary>Margin around the panel for the shadow, in pixels.</summary>
    public int ShadowMargin => _margin;

    /// <summary>True while the cursor is over the "Layout" pill. Set it with <see cref="SetPillHover"/>.</summary>
    public bool PillHover { get; private set; }

    /// <summary>True when a screen point is over the "Layout" pill (its hit area is a little bigger than the drawn pill).</summary>
    public bool HitPill(POINT p) => _pillHit.Contains(new Point(p.X - PanelRect.Left, p.Y - PanelRect.Top));

    /// <summary>Light or dim the "Layout" pill, eased like a zone highlight. Does nothing when unchanged.</summary>
    public void SetPillHover(bool on)
    {
        if (on == PillHover) return;
        PillHover = on;
        if (!Visible) { _pillT = on ? 1 : 0; return; }
        float from = _pillT, to = on ? 1 : 0;
        if (!Theme.Animate) { _pillT = to; Render(); return; }
        _pillAnim.Start(Theme.HighlightMs, t => { _pillT = from + (to - from) * Theme.Ease(t); Render(); });
    }

    /// <summary>
    /// Layout the editor starts from when the window drops on the "Layout" pill:
    /// the card last hovered, else the main layout, else the first card. Null when the strip is empty.
    /// </summary>
    public ZoneLayout? EditSource =>
        Active >= 0 && Active < Layouts.Count ? Layouts[Active]
        : Layouts.FirstOrDefault(l => l.Id == MainId) ?? Layouts.FirstOrDefault();

    /// <summary>Keep the strip above the zones overlay. Shows it if hidden.</summary>
    public override void ShowAt(RECT r)
    {
        if (Visible) BringToTop();
        else ShowRendered(255);
    }

    /// <summary>Layout and zone under a screen point. Layout -1: not over a card. Zone -1: no mini zone.</summary>
    public (int layout, int zone) HitTest(POINT p)
    {
        var local = new Point(p.X - PanelRect.Left, p.Y - PanelRect.Top);
        for (int i = 0; i < _cells.Count; i++)
        {
            var cell = _cells[i];
            var withLabel = new Rectangle(cell.X, cell.Y, cell.Width, cell.Height + _labelH);
            if (!withLabel.Contains(local)) continue;
            int best = -1; long bestArea = long.MaxValue;
            for (int z = 0; z < _minis[i].Length; z++)
            {
                var m = _minis[i][z];
                long a = (long)m.Width * m.Height;
                if (m.Contains(local) && a < bestArea) { best = z; bestArea = a; }
            }
            return (i, best);
        }
        return (-1, -1);
    }

    protected override void OnRenderRequest()
    {
        if (Active != _shownActive && _hot.Length == _cells.Count)
        {
            _shownActive = Active;
            var from = (float[])_hot.Clone();
            Anim.Start(Theme.HighlightMs, t =>
            {
                float e = Theme.Ease(t);
                for (int i = 0; i < _hot.Length; i++) _hot[i] = from[i] + ((i == Active ? 1 : 0) - from[i]) * e;
                Render();
            });
        }
        else if (!Anim.Running) Render();
    }

    protected override void PaintLayer(Surface s, Graphics g, Rectangle dirty)
    {
        s.Fill(dirty, Color.Transparent);
        float sc = (float)DpiScale;
        g.TranslateTransform(_margin, _margin);
        var panel = new RectangleF(0, 0, PanelRect.Width, PanelRect.Height);
        float radius = Theme.RadiusPanel * sc;
        Glass.Shadow(g, panel, radius, Theme.ShadowSize * sc, Theme.ShadowOffsetY * sc);
        Glass.Panel(g, panel, radius, _blur, panel, sc);
        var under = Theme.Over(Theme.GlassTint, Theme.Dark ? Color.FromArgb(40, 40, 46) : Color.FromArgb(225, 228, 234));
        if (_blur == null) under = Theme.Panel;
        for (int i = 0; i < _cells.Count && i < Layouts.Count; i++)
        {
            float hot = i < _hot.Length ? _hot[i] : 0;
            Thumb.Draw(g, Layouts[i], _cells[i], Work.Width, Work.Height, DpiScale, i == Active ? HoverZone : -1, hot, under);
            var lr = new RectangleF(_cells[i].X - S(4), _cells[i].Bottom + S(3), _cells[i].Width + S(8), _labelH - S(4));
            Thumb.Label(g, Layouts[i].Name, Layouts[i].Id == MainId, lr, _font!, Theme.Mix(Theme.Dim, Theme.Text, hot), DpiScale);
        }
        PaintPill(g, sc);
        g.ResetTransform();
    }

    /// <summary>The "Layout" pill: a glass capsule with a pencil and the word. Lit with the accent, like a zone, on hover.</summary>
    void PaintPill(Graphics g, float sc)
    {
        if (_pill.IsEmpty || _font == null) return;
        float t = _pillT;
        // Hairline divider between the cards and the pill.
        int dx = _pill.Left - (int)Math.Round(9 * sc);
        using (var div = new Pen(Theme.A(Theme.Dark ? Color.White : Color.Black, Theme.Dark ? 0.14 : 0.12), Math.Max(1, sc)))
            g.DrawLine(div, dx, _pillHit.Top + 4 * sc, dx, _pillHit.Bottom - 6 * sc);

        var r = new RectangleF(_pill.X, _pill.Y, _pill.Width, _pill.Height);
        float rad = r.Height / 2;
        using var path = Theme.Round(r, rad);
        using (var fill = new SolidBrush(Theme.Mix(Theme.Card, Theme.ZoneHotFill, t))) g.FillPath(fill, path);
        if (t > 0.01f)
            using (var solid = new SolidBrush(Theme.A(Theme.Accent, 0.55 * t))) g.FillPath(solid, path);
        // Light from the top, as on the zone tiles.
        using (var hl = new Pen(Color.FromArgb((int)(Theme.Dark ? 40 + 40 * t : 110 + 60 * t), 255, 255, 255), Math.Max(1, 1.2f * sc)))
        {
            var clip = g.Clip;
            g.SetClip(new RectangleF(r.X, r.Y, r.Width, r.Height * 0.45f), CombineMode.Intersect);
            using var inner = Theme.Round(RectangleF.Inflate(r, -1.5f * sc, -1.5f * sc), rad - 1.5f * sc);
            g.DrawPath(hl, inner);
            g.Clip = clip;
        }
        using (var edge = new Pen(Theme.Mix(Theme.MiniZoneEdge, Theme.ZoneHotEdge, t), (1 + 1.2f * t) * sc) { Alignment = PenAlignment.Inset })
            g.DrawPath(edge, path);
        if (t > 0.01f)
        {
            using var glow = new Pen(Theme.A(Theme.Accent, 0.28 * t), 5 * sc);
            using var gp = Theme.Round(RectangleF.Inflate(r, 1.5f * sc, 1.5f * sc), rad + 1.5f * sc);
            g.DrawPath(glow, gp);
        }

        // Pencil and label, centred together.
        var fore = Theme.Mix(Theme.Text, Theme.OnAccent, t);
        const string text = "Layout";
        var fmt = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap };
        var ts = g.MeasureString(text, _font, PointF.Empty, fmt);
        float icon = Math.Min(15 * sc, r.Height - 8 * sc), igap = 6 * sc;
        float total = icon + igap + ts.Width;
        float x = r.X + (r.Width - total) / 2;
        Icons.Glyph(g, "pencil", new RectangleF(x, r.Y + (r.Height - icon) / 2, icon, icon), fore, Math.Max(1.2f, 1.5f * sc));
        using var tb = new SolidBrush(fore);
        g.DrawString(text, _font, tb, x + icon + igap, r.Y + (r.Height - ts.Height) / 2, fmt);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) { _slide.Stop(); _pillAnim.Stop(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _slide.Dispose(); _pillAnim.Dispose(); _blur?.Dispose(); _font?.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>Short confirmation after a snap: the zone outline glows in the accent colour and fades out.</summary>
public static class SnapFlash
{
    static FlashForm? _form;

    /// <summary>Flash a zone (screen pixels). Does nothing when Windows animations are off.</summary>
    public static void Show(RECT zone, double scale)
    {
        if (!Theme.Animate || zone.Width < 8 || zone.Height < 8) return;
        try
        {
            _form ??= new FlashForm();
            _form.Flash(zone, scale);
        }
        catch (Exception ex) { DragWatcher.Log("snap flash failed: " + ex.Message); }
    }

    public static void Dispose() { _form?.Dispose(); _form = null; }

    internal static Bitmap Render(RECT zone, double scale)
    {
        using var f = new FlashForm();
        f.Prepare(zone, scale);
        return f.Snapshot();
    }

    sealed class FlashForm : OverlayForm
    {
        double _scale = 1;
        int _pad;

        public void Prepare(RECT z, double scale)
        {
            _scale = scale;
            _pad = (int)Math.Round(8 * scale);
            SetGeometry(new Rectangle(z.Left - _pad, z.Top - _pad, z.Width + 2 * _pad, z.Height + 2 * _pad));
        }

        public void Flash(RECT z, double scale)
        {
            Prepare(z, scale);
            ShowRendered(255);
            Anim.Start(Theme.SnapFlashMs, t => Compose((byte)Math.Round(255 * (1 - t) * (1 - t))), Hide);
        }

        protected override void PaintLayer(Surface s, Graphics g, Rectangle dirty)
        {
            s.Fill(dirty, Color.Transparent);
            float sc = (float)_scale;
            var r = new RectangleF(_pad, _pad, WindowRect.Width - 2 * _pad, WindowRect.Height - 2 * _pad);
            float radius = 8 * sc;
            using var path = Theme.Round(r, radius);
            using (var fill = new SolidBrush(Theme.A(Theme.Accent, 0.16))) g.FillPath(fill, path);
            using (var glow = new Pen(Theme.A(Theme.Accent, 0.35), 9 * sc)) g.DrawPath(glow, path);
            using (var edge = new Pen(Theme.AccentSoft, 3 * sc)) g.DrawPath(edge, path);
        }
    }
}
