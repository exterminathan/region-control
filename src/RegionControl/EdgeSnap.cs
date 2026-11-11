namespace RegionControl;

/// <summary>Which layout and which zone a side-edge drop uses.</summary>
public static class EdgeMath
{
    /// <summary>
    /// Zone that belongs to a screen edge at height <paramref name="y"/>. <paramref name="side"/> -1 is the left edge, 1 the right.
    /// Zones that touch the edge win; among them the one at that height, else the nearest in height.
    /// A layout with no zone on the edge falls back to the zone nearest to it. Zones for which <paramref name="taken"/>
    /// is true (a window already sits there) are never picked. Returns -1 when no zone is free.
    /// </summary>
    public static int PickZone(ZoneLayout layout, RECT work, int side, int y, Func<int, bool>? taken = null, int tol = 6)
    {
        int best = -1;
        long bestScore = long.MaxValue;
        for (int i = 0; i < layout.Zones.Count; i++)
        {
            if (taken != null && taken(i)) continue;
            var r = layout.ZoneRect(i, work);
            int edgeDist = Math.Abs(side < 0 ? r.Left - work.Left : work.Right - r.Right);
            int vDist = y < r.Top ? r.Top - y : y > r.Bottom ? y - r.Bottom : 0;
            long score = (long)Math.Max(0, edgeDist - tol) * 10 + vDist;
            if (score < bestScore) { bestScore = score; best = i; }
        }
        return best;
    }

    static long Area(RECT r) => (long)Math.Max(0, r.Width) * Math.Max(0, r.Height);

    static long Overlap(RECT a, RECT b) =>
        Area(new RECT(Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top), Math.Min(a.Right, b.Right), Math.Min(a.Bottom, b.Bottom)));

    /// <summary>True when a window frame covers more than 60 percent of a zone rectangle.</summary>
    public static bool Covers(RECT frame, RECT zone) => Overlap(frame, zone) * 10 > Area(zone) * 6;

    /// <summary>
    /// The layout in use on a monitor: the one whose zones match the most open window frames (each within
    /// <paramref name="tol"/> px on every side). With no match, the last used layout, else the first.
    /// </summary>
    public static ZoneLayout? CurrentLayout(IReadOnlyList<ZoneLayout> layouts, RECT work, IReadOnlyList<RECT> frames, int tol, string? lastUsedId)
    {
        if (layouts.Count == 0) return null;
        ZoneLayout? best = null;
        int bestScore = 0;
        foreach (var l in layouts)
        {
            int score = 0;
            for (int i = 0; i < l.Zones.Count; i++)
            {
                var z = l.ZoneRect(i, work);
                if (frames.Any(f => Math.Abs(f.Left - z.Left) <= tol && Math.Abs(f.Top - z.Top) <= tol
                                 && Math.Abs(f.Right - z.Right) <= tol && Math.Abs(f.Bottom - z.Bottom) <= tol)) score++;
            }
            if (score > bestScore || (score == bestScore && score > 0 && l.Id == lastUsedId)) { best = l; bestScore = score; }
        }
        return best ?? layouts.FirstOrDefault(l => l.Id == lastUsedId) ?? layouts[0];
    }
}

/// <summary>
/// Drag a window to the left or right edge of a monitor and hold it there: the layout in use on that monitor shows,
/// the free zone on that edge lights up, and dropping snaps the window there. It never takes a zone that already has a
/// window, and an edge shared with another monitor does not count.
/// </summary>
public partial class DragWatcher
{
    const int EdgeDwellMs = 300;

    ZoneLayout? _edgeLayout;                       // layout shown for a side-edge drop, or null
    int _edgeSide;                                 // -1 left, 1 right
    DateTime _edgeSince;                           // when the cursor reached the edge band (default: not at an edge)
    List<RECT> _edgeFrames = new();                // frames of the other windows, read once at the edge
    readonly Dictionary<string, string> _lastLayout = new();   // monitor key -> layout last snapped into

    bool EdgeTaken(ZoneLayout l, RECT work, int zone)
    {
        var zr = l.ZoneRect(zone, work);
        return _edgeFrames.Any(f => EdgeMath.Covers(f, zr));
    }

    /// <summary>Frames of the visible windows on a monitor, except the dragged one.</summary>
    List<RECT> OtherFrames(MonitorInfo mon)
    {
        var list = new List<RECT>();
        var skip = new HashSet<IntPtr> { _drag };
        foreach (var w in AssistWindows.Enumerate(skip))
        {
            try
            {
                if (!w.Minimized && !Native.IsZoomed(w.Hwnd))
                {
                    var f = Native.FrameBounds(w.Hwnd);
                    var c = new POINT { X = (f.Left + f.Right) / 2, Y = (f.Top + f.Bottom) / 2 };
                    if (mon.Bounds.Contains(c)) list.Add(f);
                }
            }
            finally { w.Dispose(); }
        }
        return list;
    }

    /// <summary>One poll step of side-edge mode. True when it handled this step (the caller stops).</summary>
    bool EdgeTrack(POINT p, MonitorInfo mon, bool atTop)
    {
        if (_shown) return false;
        int band = Math.Max(2, (int)Math.Round(6 * mon.Scale));
        int side = 0;
        if (!atTop)
        {
            if (p.X <= mon.Bounds.Left + band && !_monitors.Any(m => m.Handle != mon.Handle && m.Bounds.Contains(new POINT { X = p.X - 12, Y = p.Y }))) side = -1;
            else if (p.X >= mon.Bounds.Right - 1 - band && !_monitors.Any(m => m.Handle != mon.Handle && m.Bounds.Contains(new POINT { X = p.X + 12, Y = p.Y }))) side = 1;
        }

        if (_edgeLayout == null)
        {
            if (side == 0) { _edgeSince = default; return false; }
            if (_edgeSince == default)
            {
                // Just reached an edge: read the other windows once, then wait before showing anything.
                _edgeSince = DateTime.UtcNow;
                _edgeFrames = OtherFrames(mon);
                return false;
            }
            if (_edgeSince == DateTime.MaxValue) return false;                       // this touch found nothing to offer
            if ((DateTime.UtcNow - _edgeSince).TotalMilliseconds < EdgeDwellMs) return false;

            var layouts = Lib.StripFor(mon.Key, SetupKey);
            _lastLayout.TryGetValue(mon.Key, out var lastId);
            int tol = Math.Max(8, (int)Math.Round(16 * mon.Scale));
            var layout = EdgeMath.CurrentLayout(layouts, mon.Work, _edgeFrames, tol, lastId);
            if (layout == null || layout.Zones.Count == 0) { _edgeSince = DateTime.MaxValue; return false; }
            int pick = EdgeMath.PickZone(layout, mon.Work, side, p.Y, i => EdgeTaken(layout, mon.Work, i));
            Log($"edge check: {_edgeFrames.Count} other window(s) at {string.Join(" ", _edgeFrames)}; layout in use \"{layout.Name}\"; free zone {(pick < 0 ? "none" : (pick + 1).ToString())}");
            if (pick < 0) { Log($"edge drop skipped: every zone of \"{layout.Name}\" already has a window"); _edgeSince = DateTime.MaxValue; return false; }

            _edgeLayout = layout;
            _edgeSide = side;
            _mon = mon;
            _zones.ShowLayout(layout, mon.Work, mon.Scale);
            _esc.Install();
            Log($"edge drop shown on {mon}, {(side < 0 ? "left" : "right")} edge, layout \"{layout.Name}\"");
        }
        else
        {
            if (atTop) { HideOverlay(); return false; }          // the top picker takes over
            if (mon.Handle != _mon!.Handle) { HideOverlay(); return true; }
        }

        var l = _edgeLayout!;
        var work = _mon!.Work;
        int hover;
        if (side != 0)
        {
            _edgeSide = side;
            hover = EdgeMath.PickZone(l, work, side, p.Y, i => EdgeTaken(l, work, i));
        }
        else
        {
            hover = _zones.HitTest(p);
            if (hover >= 0 && EdgeTaken(l, work, hover)) hover = -1;
            // Far from the edge and on no free zone: the overlay goes away.
            int far = Math.Max(1, (int)Math.Round(160 * mon.Scale));
            int dist = _edgeSide < 0 ? p.X - mon.Bounds.Left : mon.Bounds.Right - 1 - p.X;
            if (hover < 0 && dist > far) { HideOverlay(); return true; }
        }
        if (hover != _zones.Hover) { _zones.Hover = hover; _zones.Invalidate(); }
        return true;
    }
}
