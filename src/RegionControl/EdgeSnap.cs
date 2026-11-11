namespace RegionControl;

/// <summary>Which zone a side-edge drop takes.</summary>
public static class EdgeMath
{
    /// <summary>
    /// Zone that belongs to a screen edge at height <paramref name="y"/>. <paramref name="side"/> -1 is the left edge, 1 the right.
    /// Zones that touch the edge win; among them the one at that height, else the nearest in height.
    /// A layout with no zone on the edge falls back to the zone nearest to it. Returns -1 for a layout with no zones.
    /// </summary>
    public static int PickZone(ZoneLayout layout, RECT work, int side, int y, int tol = 6)
    {
        int best = -1;
        long bestScore = long.MaxValue;
        for (int i = 0; i < layout.Zones.Count; i++)
        {
            var r = layout.ZoneRect(i, work);
            int edgeDist = Math.Abs(side < 0 ? r.Left - work.Left : work.Right - r.Right);
            int vDist = y < r.Top ? r.Top - y : y > r.Bottom ? y - r.Bottom : 0;
            long score = (long)Math.Max(0, edgeDist - tol) * 10 + vDist;
            if (score < bestScore) { bestScore = score; best = i; }
        }
        return best;
    }
}

/// <summary>
/// Drag a window to the left or right edge of a monitor: the monitor's first layout shows, the zone on that edge lights up,
/// and dropping snaps the window there. An edge shared with another monitor does not count.
/// </summary>
public partial class DragWatcher
{
    ZoneLayout? _edgeLayout;   // layout shown for a side-edge drop, or null
    int _edgeSide;             // -1 left, 1 right

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
            if (side == 0) return false;
            var layouts = Lib.StripFor(mon.Key, SetupKey);
            if (layouts.Count == 0 || layouts[0].Zones.Count == 0) return false;
            _edgeLayout = layouts[0];
            _edgeSide = side;
            _mon = mon;
            _zones.ShowLayout(_edgeLayout, mon.Work, mon.Scale);
            _esc.Install();
            Log($"edge drop shown on {mon}, {(side < 0 ? "left" : "right")} edge, layout \"{_edgeLayout.Name}\"");
        }
        else
        {
            if (atTop) { HideOverlay(); return false; }          // the top picker takes over
            if (mon.Handle != _mon!.Handle) { HideOverlay(); return true; }
        }

        int hover;
        if (side != 0)
        {
            _edgeSide = side;
            hover = EdgeMath.PickZone(_edgeLayout!, _mon!.Work, side, p.Y);
        }
        else
        {
            hover = _zones.HitTest(p);
            // Far from the edge and on no zone: the overlay goes away.
            int far = Math.Max(1, (int)Math.Round(160 * mon.Scale));
            int dist = _edgeSide < 0 ? p.X - mon.Bounds.Left : mon.Bounds.Right - 1 - p.X;
            if (hover < 0 && dist > far) { HideOverlay(); return true; }
        }
        if (hover != _zones.Hover) { _zones.Hover = hover; _zones.Invalidate(); }
        return true;
    }
}
