namespace RegionControl;

/// <summary>Geometry for resizing a shared border: which windows sit on the dragged line, and where their edge goes.</summary>
public static class BorderMath
{
    /// <summary>
    /// Indexes of <paramref name="others"/> that have an edge on the dragged line and connect to it along the line.
    /// <paramref name="vertical"/> true: the line is x = <paramref name="line"/> (left or right edges). False: y = line.
    /// A window joins when its edge is within <paramref name="tol"/> of the line and its span overlaps the dragged
    /// window, or a window already in the group, by at least <paramref name="minOverlap"/>.
    /// </summary>
    public static List<int> Members(bool vertical, int line, RECT dragged, IReadOnlyList<RECT> others, int tol, int minOverlap)
    {
        bool OnLine(RECT r) => vertical
            ? Math.Abs(r.Left - line) <= tol || Math.Abs(r.Right - line) <= tol
            : Math.Abs(r.Top - line) <= tol || Math.Abs(r.Bottom - line) <= tol;
        (int lo, int hi) Span(RECT r) => vertical ? (r.Top, r.Bottom) : (r.Left, r.Right);
        bool Overlaps((int lo, int hi) a, (int lo, int hi) b) => Math.Min(a.hi, b.hi) - Math.Max(a.lo, b.lo) >= minOverlap;

        var spans = new List<(int lo, int hi)> { Span(dragged) };
        var members = new List<int>();
        bool grew = true;
        while (grew)
        {
            grew = false;
            for (int i = 0; i < others.Count; i++)
            {
                if (members.Contains(i) || !OnLine(others[i])) continue;
                var s = Span(others[i]);
                if (!spans.Any(x => Overlaps(x, s))) continue;
                members.Add(i); spans.Add(s); grew = true;
            }
        }
        members.Sort();
        return members;
    }

    /// <summary>
    /// Move the edge of <paramref name="r"/> that sits on the line (<paramref name="line0"/>) by <paramref name="delta"/>,
    /// keeping any small gap, and never below <paramref name="minSize"/>. Returns r unchanged when no edge is on the line.
    /// </summary>
    public static RECT Move(RECT r, bool vertical, int line0, int delta, int minSize, int tol)
    {
        if (vertical)
        {
            bool l = Math.Abs(r.Left - line0) <= tol, rt = Math.Abs(r.Right - line0) <= tol;
            if (l && !rt) r.Left = Math.Min(r.Left + delta, r.Right - minSize);
            else if (rt && !l) r.Right = Math.Max(r.Right + delta, r.Left + minSize);
        }
        else
        {
            bool t = Math.Abs(r.Top - line0) <= tol, b = Math.Abs(r.Bottom - line0) <= tol;
            if (t && !b) r.Top = Math.Min(r.Top + delta, r.Bottom - minSize);
            else if (b && !t) r.Bottom = Math.Max(r.Bottom + delta, r.Top + minSize);
        }
        return r;
    }
}

/// <summary>
/// Dragging a border between snapped windows moves it for all of them, as Windows does for snap groups.
/// Only windows that Snap Layouts snapped take part. A border drag with no such neighbour is an ordinary resize.
/// </summary>
public partial class DragWatcher
{
    sealed class Member
    {
        public IntPtr Hwnd;
        public RECT F0;                 // visible frame when the drag began
        public int L, T, R, B;          // invisible border sizes: window rect minus visible frame
        public bool InX, InY;           // on the dragged vertical line, on the dragged horizontal line
        public RECT Last;               // last frame applied
    }

    sealed class BorderDrag
    {
        public IntPtr Dragged;
        public RECT F0;
        public int Ht, LineX, LineY, Tol, MinSize;
        public bool UseX, UseY;
        public List<Member> Members = new();
    }

    BorderDrag? _rs;

    /// <summary>Called at a resize start. True when the window's border is shared with snapped windows, so the group follows.</summary>
    bool TryBeginBorderResize(IntPtr hwnd, int ht, POINT cursor)
    {
        _rs = null;
        PruneSaved();
        bool left = ht is 10 or 13 or 16, right = ht is 11 or 14 or 17, top = ht is 12 or 13 or 14, bottom = ht is 15 or 16 or 17;
        if (!(left || right || top || bottom)) return false;
        var (_, _, scale) = Native.MonitorAt(cursor);
        var f0 = Native.FrameBounds(hwnd);
        var d = new BorderDrag
        {
            Dragged = hwnd, F0 = f0, Ht = ht,
            Tol = Math.Max(4, (int)Math.Round(14 * scale)),
            MinSize = Math.Max(80, (int)Math.Round(160 * scale)),
            UseX = left || right, UseY = top || bottom,
            LineX = left ? f0.Left : f0.Right, LineY = top ? f0.Top : f0.Bottom,
        };
        var cands = new List<Member>();
        foreach (var h in _saved.Keys)
        {
            if (h == hwnd || !IsWindow(h) || !Native.IsWindowVisible(h) || Native.IsIconic(h) || Native.IsZoomed(h)) continue;
            var f = Native.FrameBounds(h);
            Native.GetWindowRect(h, out var w);
            cands.Add(new Member { Hwnd = h, F0 = f, Last = f, L = f.Left - w.Left, T = f.Top - w.Top, R = w.Right - f.Right, B = w.Bottom - f.Bottom });
        }
        int overlap = Math.Max(8, (int)Math.Round(20 * scale));
        var frames = cands.Select(c => c.F0).ToList();
        if (d.UseX) foreach (int i in BorderMath.Members(true, d.LineX, f0, frames, d.Tol, overlap)) cands[i].InX = true;
        if (d.UseY) foreach (int i in BorderMath.Members(false, d.LineY, f0, frames, d.Tol, overlap)) cands[i].InY = true;
        d.Members = cands.Where(c => c.InX || c.InY).ToList();
        if (d.Members.Count == 0) return false;
        _rs = d;
        Log($"border resize start (hit {ht}): {d.Members.Count} snapped window(s) share the border");
        return true;
    }

    /// <summary>One poll step: read the dragged window's new edge and move the shared edge of every group member.</summary>
    void BorderStep()
    {
        var d = _rs;
        if (d == null) return;
        if (!IsWindow(d.Dragged)) { _rs = null; _poll.Stop(); return; }
        var f = Native.FrameBounds(d.Dragged);
        bool left = d.Ht is 10 or 13 or 16, top = d.Ht is 12 or 13 or 14;
        int dx = d.UseX ? (left ? f.Left : f.Right) - d.LineX : 0;
        int dy = d.UseY ? (top ? f.Top : f.Bottom) - d.LineY : 0;
        foreach (var m in d.Members)
        {
            var r = m.F0;
            if (m.InX) r = BorderMath.Move(r, true, d.LineX, dx, d.MinSize, d.Tol);
            if (m.InY) r = BorderMath.Move(r, false, d.LineY, dy, d.MinSize, d.Tol);
            if (r.Left == m.Last.Left && r.Top == m.Last.Top && r.Right == m.Last.Right && r.Bottom == m.Last.Bottom) continue;
            m.Last = r;
            Native.SetWindowPos(m.Hwnd, IntPtr.Zero, r.Left - m.L, r.Top - m.T, r.Width + m.L + m.R, r.Height + m.T + m.B,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_ASYNCWINDOWPOS);
        }
    }

    /// <summary>Resize ended: last step, then store each window's new frame as its zone frame so a drag-out still restores its old size.</summary>
    void EndBorderResize()
    {
        var d = _rs;
        if (d == null) return;
        BorderStep();
        _rs = null;
        _poll.Stop();
        var t = new System.Windows.Forms.Timer { Interval = 60 };
        t.Tick += (_, _) =>
        {
            t.Stop(); t.Dispose();
            foreach (var h in d.Members.Select(m => m.Hwnd).Append(d.Dragged))
                if (IsWindow(h)) RememberZoneFrame(h, Native.FrameBounds(h));
            Log("border resize end: group frames stored");
        };
        t.Start();
    }
}
