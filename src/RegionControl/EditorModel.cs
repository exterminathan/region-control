using System.Globalization;
using System.Text;

namespace RegionControl;

/// <summary>A zone in the editor, as fractions (0..1) of the work area.</summary>
public class EditZone
{
    public double L, T, R, B;
    public ZoneMode Mode;
    public string Name = "";
    public double W => R - L;
    public double H => B - T;
    public double Area => W * H;
    public bool Contains(double x, double y) => x >= L && x < R && y >= T && y < B;
    public EditZone Clone() => (EditZone)MemberwiseClone();
}

/// <summary>
/// A divider: one line that zones share. Vertical: the line x = Pos; Before zones end at it (R), After zones start at it (L).
/// Horizontal: the line y = Pos; Before zones end at it (B), After zones start at it (T).
/// </summary>
public class Divider
{
    public bool Vertical;
    public double Pos, Lo, Hi;           // line position and its extent along the line
    public List<int> Before = new(), After = new();
    public double Min, Max;              // allowed range for Pos, computed at drag start
    public IEnumerable<int> All => Before.Concat(After);
}

/// <summary>
/// Editor state and every edit operation, without any UI, so the self-test can check it.
/// Coordinates are fractions of the work area; WorkW and WorkH convert to pixels.
/// </summary>
public class EditorModel
{
    public const double Eps = 1e-6;
    public List<EditZone> Zones = new();
    public int Cols = 12, Rows = 8;
    public double WorkW, WorkH;
    public string Name = "New layout";
    public string? LayoutId;
    public double MinPx = 32;        // smallest zone side in pixels
    public double MagnetPx = 10;     // edge snap distance in pixels
    public ZoneMode NewMode = ZoneMode.Percent;

    record Snapshot(List<EditZone> Zones, int Cols, int Rows, string? Id, string Name);
    readonly List<Snapshot> _undo = new(), _redo = new();
    string _savedSig = "";

    public EditorModel(double workW, double workH)
    {
        WorkW = Math.Max(1, workW); WorkH = Math.Max(1, workH);
        Zones.Add(Full(NewMode));
        _savedSig = Signature();
    }

    static EditZone Full(ZoneMode m) => new() { L = 0, T = 0, R = 1, B = 1, Mode = m };

    double MinW => MinPx / WorkW;
    double MinH => MinPx / WorkH;

    // ---------- undo ----------

    Snapshot Take() => new(Zones.Select(z => z.Clone()).ToList(), Cols, Rows, LayoutId, Name);
    void Restore(Snapshot s) { Zones = s.Zones.Select(z => z.Clone()).ToList(); Cols = s.Cols; Rows = s.Rows; LayoutId = s.Id; Name = s.Name; }

    /// <summary>Save the current state for undo. Call before a change.</summary>
    public void Push()
    {
        _undo.Add(Take());
        if (_undo.Count > 200) _undo.RemoveAt(0);
        _redo.Clear();
    }

    /// <summary>After a drag: drop the undo step if nothing changed.</summary>
    public void PopIfUnchanged()
    {
        if (_undo.Count == 0) return;
        var top = _undo[^1];
        if (Sig(top.Zones, top.Cols, top.Rows, top.Id, top.Name) == Signature()) _undo.RemoveAt(_undo.Count - 1);
    }

    /// <summary>Cancel a drag: go back to the state saved at its start.</summary>
    public void CancelDrag()
    {
        if (_undo.Count == 0) return;
        Restore(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Add(Take());
        Restore(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Add(Take());
        Restore(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        return true;
    }

    static string Sig(List<EditZone> zones, int cols, int rows, string? id, string name)
    {
        var sb = new StringBuilder();
        sb.Append(cols).Append('x').Append(rows).Append('|').Append(id).Append('|').Append(name);
        foreach (var z in zones)
            sb.Append('|').Append(z.Mode).Append(':').Append(string.Join(",", new[] { z.L, z.T, z.R, z.B }.Select(v => Math.Round(v, 6).ToString(CultureInfo.InvariantCulture))));
        return sb.ToString();
    }

    public string Signature() => Sig(Zones, Cols, Rows, LayoutId, Name);
    public bool Dirty => Signature() != _savedSig;
    public void MarkSaved() => _savedSig = Signature();

    // ---------- load and save ----------

    public void NewBlank()
    {
        Push();
        Zones = new();
        LayoutId = null; Name = "New layout";
    }

    public void Load(ZoneLayout layout, bool undoable = true)
    {
        if (undoable) Push();
        Cols = layout.Grid.Cols; Rows = layout.Grid.Rows;
        LayoutId = layout.Id; Name = layout.Name;
        Zones = layout.Zones.Select(z =>
        {
            var (l, t, r, b) = z.ToFraction(layout.Grid, WorkW, WorkH);
            return new EditZone { L = l, T = t, R = r, B = b, Mode = z.Mode, Name = z.Name };
        }).ToList();
        if (!undoable) MarkSaved();
    }

    /// <summary>Convert to a saved layout. Each zone is stored in its own mode. Zones are named by position.</summary>
    public ZoneLayout ToLayout()
    {
        var layout = new ZoneLayout { Id = LayoutId ?? "", Name = Name.Trim().Length > 0 ? Name.Trim() : "New layout", Grid = new GridSize { Cols = Cols, Rows = Rows } };
        int n = 0;
        foreach (var z in Zones.OrderBy(z => Math.Round(z.T, 4)).ThenBy(z => z.L))
        {
            var d = new Zone { Mode = z.Mode, Name = $"Zone {++n}" };
            switch (z.Mode)
            {
                case ZoneMode.Pixels:
                    d.X = Math.Round(z.L * WorkW); d.W = Math.Max(1, Math.Round(z.R * WorkW) - d.X);
                    d.Y = Math.Round(z.T * WorkH); d.H = Math.Max(1, Math.Round(z.B * WorkH) - d.Y);
                    break;
                case ZoneMode.Grid:
                    d.X = Math.Round(z.L * Cols); d.W = Math.Max(1, Math.Round(z.R * Cols) - d.X);
                    d.Y = Math.Round(z.T * Rows); d.H = Math.Max(1, Math.Round(z.B * Rows) - d.Y);
                    break;
                default:
                    d.X = Math.Round(z.L * 100, 3); d.W = Math.Round(z.R * 100, 3) - d.X;
                    d.Y = Math.Round(z.T * 100, 3); d.H = Math.Round(z.B * 100, 3) - d.Y;
                    d.W = Math.Round(d.W, 3); d.H = Math.Round(d.H, 3);
                    break;
            }
            layout.Zones.Add(d);
        }
        return layout;
    }

    // ---------- queries ----------

    /// <summary>Zone under a point, or -1. With overlap, the smallest zone wins.</summary>
    public int HitZone(double x, double y)
    {
        int best = -1; double bestArea = double.MaxValue;
        for (int i = 0; i < Zones.Count; i++)
            if (Zones[i].Contains(x, y) && Zones[i].Area < bestArea) { best = i; bestArea = Zones[i].Area; }
        return best;
    }

    /// <summary>Default split direction: a vertical line for a wide zone, a horizontal line for a tall zone.</summary>
    public bool DefaultVertical(int i) => Zones[i].W * WorkW >= Zones[i].H * WorkH;

    double SnapGrid(double v, bool vertical) { int n = vertical ? Cols : Rows; return Math.Round(v * n) / n; }
    double SnapPixel(double v, bool vertical) { double n = vertical ? WorkW : WorkH; return Math.Round(v * n) / n; }

    /// <summary>Edge positions other zones offer for magnetic snapping, plus the borders and the centre.</summary>
    List<double> Candidates(bool vertical, IEnumerable<int> exclude)
    {
        var ex = exclude.ToHashSet();
        var list = new List<double> { 0, 0.5, 1 };
        for (int i = 0; i < Zones.Count; i++)
        {
            if (ex.Contains(i)) continue;
            var z = Zones[i];
            if (vertical) { list.Add(z.L); list.Add(z.R); } else { list.Add(z.T); list.Add(z.B); }
        }
        return list;
    }

    double Magnet(double v, bool vertical, IEnumerable<int> exclude, out bool snapped)
    {
        double tol = MagnetPx / (vertical ? WorkW : WorkH), best = v, bestD = tol;
        snapped = false;
        foreach (var c in Candidates(vertical, exclude))
        {
            double d = Math.Abs(c - v);
            if (d <= bestD) { best = c; bestD = d; snapped = true; }
        }
        return best;
    }

    /// <summary>Snap a position: to the grid when grid is true, else to other edges (when magnet) and whole pixels.</summary>
    public double Snap(double v, bool vertical, bool grid, bool magnet, IEnumerable<int> exclude)
    {
        if (grid) return SnapGrid(v, vertical);
        if (magnet)
        {
            double m = Magnet(v, vertical, exclude, out bool snapped);
            if (snapped) return m;
        }
        return SnapPixel(v, vertical);
    }

    // ---------- split ----------

    public double SnapSplit(int i, bool vertical, double pos, bool magnet) =>
        Snap(pos, vertical, Zones[i].Mode == ZoneMode.Grid, magnet, new[] { i });

    public bool CanSplit(int i, bool vertical, double pos)
    {
        var z = Zones[i];
        return vertical ? pos - z.L >= MinW - Eps && z.R - pos >= MinW - Eps
                        : pos - z.T >= MinH - Eps && z.B - pos >= MinH - Eps;
    }

    /// <summary>Split zone i with a vertical line at x = pos, or a horizontal line at y = pos. Both parts keep the mode.</summary>
    public bool Split(int i, bool vertical, double pos)
    {
        if (i < 0 || i >= Zones.Count || !CanSplit(i, vertical, pos)) return false;
        Push();
        var a = Zones[i];
        var b = a.Clone();
        b.Name = "";
        if (vertical) { a.R = pos; b.L = pos; } else { a.B = pos; b.T = pos; }
        Zones.Insert(i + 1, b);
        return true;
    }

    // ---------- dividers ----------

    /// <summary>The nearest zone edge within tolPx of the point, with every zone that shares that line.</summary>
    public Divider? FindDivider(double x, double y, bool vertical, double tolPx, bool joinTouching = false)
    {
        double tol = tolPx / (vertical ? WorkW : WorkH), tolAlong = tolPx / (vertical ? WorkH : WorkW);
        double best = double.NaN, bestD = tol;
        EditZone? seed = null;
        foreach (var z in Zones)
        {
            double along = vertical ? y : x, a = vertical ? z.T : z.L, b = vertical ? z.B : z.R;
            if (along < a - tolAlong || along > b + tolAlong) continue;
            double across = vertical ? x : y;
            foreach (var e in vertical ? new[] { z.L, z.R } : new[] { z.T, z.B })
            {
                double d = Math.Abs(across - e);
                if (d <= bestD) { bestD = d; best = e; seed = z; }
            }
        }
        if (seed == null) return null;
        return Group(vertical, best, vertical ? seed.T : seed.L, vertical ? seed.B : seed.R, joinTouching);
    }

    /// <summary>
    /// Collect the zones on the line at pos whose extent overlaps the seed extent, growing it as zones join.
    /// By default only real overlap joins, so two dividers that just meet end to end stay separate.
    /// With joinTouching (Ctrl), dividers that meet end to end move together.
    /// </summary>
    public Divider Group(bool vertical, double pos, double lo, double hi, bool joinTouching = false)
    {
        var d = new Divider { Vertical = vertical, Pos = pos, Lo = lo, Hi = hi };
        var done = new HashSet<int>();
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < Zones.Count; i++)
            {
                if (done.Contains(i)) continue;
                var z = Zones[i];
                double a = vertical ? z.T : z.L, b = vertical ? z.B : z.R;
                double end = vertical ? z.R : z.B, start = vertical ? z.L : z.T;
                bool before = Math.Abs(end - pos) < Eps, after = Math.Abs(start - pos) < Eps;
                if (!before && !after) continue;
                bool overlaps = joinTouching ? b >= d.Lo - Eps && a <= d.Hi + Eps : b > d.Lo + Eps && a < d.Hi - Eps;
                if (!overlaps) continue;
                done.Add(i);
                if (before) d.Before.Add(i); else d.After.Add(i);
                d.Lo = Math.Min(d.Lo, a); d.Hi = Math.Max(d.Hi, b);
                changed = true;
            }
        }
        double min = 0, max = 1;
        foreach (int i in d.Before) min = Math.Max(min, (vertical ? Zones[i].L : Zones[i].T) + (vertical ? MinW : MinH));
        foreach (int i in d.After) max = Math.Min(max, (vertical ? Zones[i].R : Zones[i].B) - (vertical ? MinW : MinH));
        d.Min = min; d.Max = Math.Max(min, max);
        return d;
    }

    public bool UsesGrid(Divider d) => d.All.Any(i => Zones[i].Mode == ZoneMode.Grid);

    /// <summary>Snap and clamp a new divider position.</summary>
    public double SnapDivider(Divider d, double pos, bool magnet)
    {
        bool grid = UsesGrid(d);
        double v = Snap(pos, d.Vertical, grid, magnet, d.All);
        v = Math.Clamp(v, d.Min, d.Max);
        if (grid && Math.Abs(SnapGrid(v, d.Vertical) - v) > Eps)
        {
            // The clamp moved it off the grid: take the nearest grid line inside the range, if there is one.
            int n = d.Vertical ? Cols : Rows;
            double lo = Math.Ceiling(d.Min * n - Eps) / n, hi = Math.Floor(d.Max * n + Eps) / n;
            if (lo <= hi + Eps) v = Math.Clamp(SnapGrid(v, d.Vertical), lo, hi);
        }
        return v;
    }

    /// <summary>Move every zone edge on the divider. The caller pushes undo at drag start.</summary>
    public void MoveDivider(Divider d, double pos)
    {
        pos = Math.Clamp(pos, d.Min, d.Max);
        foreach (int i in d.Before) { if (d.Vertical) Zones[i].R = pos; else Zones[i].B = pos; }
        foreach (int i in d.After) { if (d.Vertical) Zones[i].L = pos; else Zones[i].T = pos; }
        d.Pos = pos;
    }

    /// <summary>Merge the two zones on either side of the divider at a point along it. They must line up.</summary>
    public bool Merge(Divider d, double along, out string why)
    {
        why = "";
        int a = d.Before.FirstOrDefault(i => Span(i, d.Vertical).lo - Eps <= along && along <= Span(i, d.Vertical).hi + Eps, -1);
        int b = d.After.FirstOrDefault(i => Span(i, d.Vertical).lo - Eps <= along && along <= Span(i, d.Vertical).hi + Eps, -1);
        if (a < 0 || b < 0) { why = "This edge has a zone on one side only."; return false; }
        var sa = Span(a, d.Vertical); var sb = Span(b, d.Vertical);
        if (Math.Abs(sa.lo - sb.lo) > Eps || Math.Abs(sa.hi - sb.hi) > Eps)
        { why = "The two zones do not line up. Drag their other edges to match first."; return false; }
        Push();
        var za = Zones[a]; var zb = Zones[b];
        za.L = Math.Min(za.L, zb.L); za.T = Math.Min(za.T, zb.T); za.R = Math.Max(za.R, zb.R); za.B = Math.Max(za.B, zb.B);
        Zones.RemoveAt(b);
        return true;
    }

    (double lo, double hi) Span(int i, bool vertical) => vertical ? (Zones[i].T, Zones[i].B) : (Zones[i].L, Zones[i].R);

    // ---------- draw, move, delete, modes ----------

    /// <summary>Snap a drawn rectangle (two corners) and normalise it.</summary>
    public (double l, double t, double r, double b) SnapRect(double x0, double y0, double x1, double y1, bool magnet)
    {
        bool grid = NewMode == ZoneMode.Grid;
        double l = Math.Clamp(Math.Min(x0, x1), 0, 1), r = Math.Clamp(Math.Max(x0, x1), 0, 1);
        double t = Math.Clamp(Math.Min(y0, y1), 0, 1), b = Math.Clamp(Math.Max(y0, y1), 0, 1);
        var none = Array.Empty<int>();
        return (Snap(l, true, grid, magnet, none), Snap(t, false, grid, magnet, none), Snap(r, true, grid, magnet, none), Snap(b, false, grid, magnet, none));
    }

    public bool BigEnough(double l, double t, double r, double b) => r - l >= MinW - Eps && b - t >= MinH - Eps;

    /// <summary>Add a drawn zone on top. Returns its index, or -1 when too small.</summary>
    public int AddZone(double l, double t, double r, double b)
    {
        if (!BigEnough(l, t, r, b)) return -1;
        Push();
        Zones.Add(new EditZone { L = l, T = t, R = r, B = b, Mode = NewMode });
        return Zones.Count - 1;
    }

    /// <summary>Move zone i to its start rectangle plus an offset, snapped and kept on screen. Caller pushes undo.</summary>
    public void MoveZone(int i, EditZone start, double dx, double dy, bool magnet)
    {
        var z = Zones[i];
        double w = start.W, h = start.H;
        double l = Math.Clamp(start.L + dx, 0, 1 - w), t = Math.Clamp(start.T + dy, 0, 1 - h);
        if (z.Mode == ZoneMode.Grid) { l = SnapGrid(l, true); t = SnapGrid(t, false); }
        else
        {
            l = SnapEdgePair(l, w, true, i, magnet);
            t = SnapEdgePair(t, h, false, i, magnet);
        }
        z.L = Math.Clamp(l, 0, 1 - w); z.R = z.L + w;
        z.T = Math.Clamp(t, 0, 1 - h); z.B = z.T + h;
    }

    double SnapEdgePair(double start, double size, bool vertical, int self, bool magnet)
    {
        if (magnet)
        {
            double a = Magnet(start, vertical, new[] { self }, out bool sa);
            double b = Magnet(start + size, vertical, new[] { self }, out bool sb);
            if (sa && (!sb || Math.Abs(a - start) <= Math.Abs(b - start - size))) return a;
            if (sb) return b - size;
        }
        return SnapPixel(start, vertical);
    }

    public void Delete(int i)
    {
        if (i < 0 || i >= Zones.Count) return;
        Push();
        Zones.RemoveAt(i);
    }

    public void Duplicate(int i)
    {
        if (i < 0 || i >= Zones.Count) return;
        Push();
        var c = Zones[i].Clone();
        double dx = Math.Min(40 / WorkW, 1 - c.R), dy = Math.Min(40 / WorkH, 1 - c.B);
        c.L += dx; c.R += dx; c.T += dy; c.B += dy;
        Zones.Add(c);
    }

    /// <summary>Change a zone's mode. Grid mode snaps its edges to the grid.</summary>
    public void SetMode(int i, ZoneMode m)
    {
        if (i < 0 || i >= Zones.Count || Zones[i].Mode == m) return;
        Push();
        Zones[i].Mode = m;
        if (m == ZoneMode.Grid) SnapZoneToGrid(Zones[i]);
    }

    public static ZoneMode NextMode(ZoneMode m) => m switch { ZoneMode.Percent => ZoneMode.Pixels, ZoneMode.Pixels => ZoneMode.Grid, _ => ZoneMode.Percent };

    void SnapZoneToGrid(EditZone z)
    {
        z.L = SnapGrid(z.L, true); z.R = SnapGrid(z.R, true);
        z.T = SnapGrid(z.T, false); z.B = SnapGrid(z.B, false);
        if (z.R - z.L < 1.0 / Cols - Eps) { if (z.L + 1.0 / Cols <= 1 + Eps) z.R = z.L + 1.0 / Cols; else z.L = z.R - 1.0 / Cols; }
        if (z.B - z.T < 1.0 / Rows - Eps) { if (z.T + 1.0 / Rows <= 1 + Eps) z.B = z.T + 1.0 / Rows; else z.T = z.B - 1.0 / Rows; }
    }

    /// <summary>Change the grid. Grid-mode zones snap to the new lines.</summary>
    public void SetGrid(int cols, int rows)
    {
        cols = Math.Clamp(cols, 1, 96); rows = Math.Clamp(rows, 1, 96);
        if (cols == Cols && rows == Rows) return;
        Push();
        Cols = cols; Rows = rows;
        foreach (var z in Zones.Where(z => z.Mode == ZoneMode.Grid)) SnapZoneToGrid(z);
    }
}
