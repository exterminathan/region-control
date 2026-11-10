using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;

namespace RegionControl;

/// <summary>
/// Snap assist, like native Windows: after a window snaps into a zone, each empty zone of the layout in turn
/// (left to right, top to bottom) shows a picker with the other open windows. Click or arrow keys plus Enter
/// place the chosen window; "Skip" or a click outside skips the zone; Esc ends the whole sequence.
/// </summary>
public partial class DragWatcher
{
    sealed class AssistSession
    {
        public ZoneLayout Layout = null!;
        public RECT Work;
        public double Scale;
        public List<int> Zones = new();
        public int Index;
        public readonly HashSet<IntPtr> Placed = new();
        public readonly List<RECT> Filled = new();
        public readonly Dictionary<IntPtr, Bitmap> Thumbs = new();
        public List<AssistWindow> Entries = new();
        public bool Dead;
    }

    AssistSession? _as;
    AssistForm? _assistForm;
    AssistKeys? _assistKeys;

    Bitmap? AssistThumb(IntPtr h)
    {
        var s = _as;
        if (s == null) return null;
        lock (s) return s.Thumbs.TryGetValue(h, out var b) ? b : null;
    }

    void BeginAssist(IntPtr placed, ZoneLayout layout, int zone, MonitorInfo mon)
    {
        EndAssist("new sequence");
        try
        {
            if (layout.Zones.Count < 2) return;
            var s = new AssistSession { Layout = layout, Work = mon.Work, Scale = mon.Scale };
            s.Placed.Add(placed);
            s.Filled.Add(layout.ZoneRect(zone, mon.Work));
            s.Zones = Enumerable.Range(0, layout.Zones.Count).Where(i => i != zone)
                .Select(i => (i, r: layout.ZoneRect(i, mon.Work))).OrderBy(x => x.r.Top).ThenBy(x => x.r.Left)
                .Select(x => x.i).ToList();
            var first = AssistWindows.Enumerate(s.Placed);
            if (first.Count == 0) { first.ForEach(w => w.Dispose()); Log("snap assist: no other windows to offer"); return; }

            _assistForm ??= CreateAssistForm();
            _assistKeys ??= new AssistKeys(OnAssistKey);
            _as = s;
            Log($"snap assist: started, {s.Zones.Count} empty zone(s), {first.Count} window(s) on offer");
            StartThumbs(s, first.Select(w => w.Hwnd).ToArray());
            first.ForEach(w => w.Dispose());
            NextAssistStep();
        }
        catch (Exception ex) { Log("snap assist failed to start: " + ex.Message); EndAssist("error"); }
    }

    AssistForm CreateAssistForm()
    {
        var f = new AssistForm { Thumb = AssistThumb };
        f.Picked = OnAssistPicked;
        f.Skipped = () => OnAssistSkipped("clicked outside or chose Skip");
        _ = f.Handle;
        return f;
    }

    /// <summary>Takes the window pictures on a worker thread, so the picker opens at once and fills in.</summary>
    void StartThumbs(AssistSession s, IntPtr[] hwnds)
    {
        var form = _assistForm!;
        double scale = s.Scale;
        Task.Run(() =>
        {
            foreach (var h in hwnds)
            {
                if (s.Dead) return;
                var bmp = AssistWindows.Capture(h, (int)Math.Round(360 * scale));
                if (bmp == null) continue;
                lock (s)
                {
                    if (s.Dead) { bmp.Dispose(); return; }
                    s.Thumbs[h] = bmp;
                }
                try { form.BeginInvoke(() => { if (!s.Dead && form.Visible) form.Invalidate(); }); } catch { }
            }
        });
    }

    static long Area(RECT r) => (long)Math.Max(0, r.Width) * Math.Max(0, r.Height);

    static long Overlap(RECT a, RECT b) =>
        Area(new RECT(Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top), Math.Min(a.Right, b.Right), Math.Min(a.Bottom, b.Bottom)));

    /// <summary>Shows the picker for the next zone that is still empty, or ends the sequence.</summary>
    void NextAssistStep()
    {
        var s = _as;
        if (s == null || s.Dead) return;
        while (s.Index < s.Zones.Count)
        {
            int zi = s.Zones[s.Index];
            var zr = s.Layout.ZoneRect(zi, s.Work);
            // A zone that an earlier window already covers is not empty.
            if (s.Filled.Any(f => Overlap(f, zr) * 10 > Area(zr) * 6)) { Log($"snap assist: zone {zi + 1} already covered, skipped"); s.Index++; continue; }
            foreach (var w in s.Entries) w.Dispose();
            s.Entries = AssistWindows.Enumerate(s.Placed);
            if (s.Entries.Count == 0) { EndAssist("no windows left"); return; }
            if (!_assistForm!.Open(s.Work, zr, s.Scale, s.Entries, $"Zone {zi + 1}"))
            {
                Log($"snap assist: zone {zi + 1} too small for the picker, skipped");
                s.Index++;
                continue;
            }
            _assistKeys!.Install();
            Log($"snap assist: picker for zone {zi + 1}, {s.Entries.Count} window(s)");
            return;
        }
        EndAssist("all zones done");
    }

    void AssistLater(Action a, int ms)
    {
        var t = new System.Windows.Forms.Timer { Interval = ms };
        var s = _as;
        t.Tick += (_, _) => { t.Stop(); t.Dispose(); if (_as == s && s != null && !s.Dead) a(); };
        t.Start();
    }

    void OnAssistPicked(int idx)
    {
        var s = _as;
        if (s == null || idx < 0 || idx >= s.Entries.Count) return;
        var w = s.Entries[idx];
        int zi = s.Zones[s.Index];
        var target = s.Layout.ZoneRect(zi, s.Work);
        _assistForm!.Hide();
        _assistKeys!.Uninstall();
        string zname = s.Layout.Zones[zi].Name.Length > 0 ? s.Layout.Zones[zi].Name : $"zone {zi + 1}";
        string name = $"{s.Layout.Name} / {zname}";
        if (!IsWindow(w.Hwnd)) { Log($"snap assist: \"{w.Title}\" is gone, zone left empty"); }
        else
        {
            if (Native.IsIconic(w.Hwnd)) Native.ShowWindow(w.Hwnd, Native.SW_RESTORE);
            Snap(w.Hwnd, target, name);
            // Bring it above the other windows so the zone shows it.
            Native.SetWindowPos(w.Hwnd, Native.HWND_TOP, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            SnapFlash.Show(target, s.Scale);
            s.Placed.Add(w.Hwnd);
            s.Filled.Add(target);
            Log($"snap assist: {w.Proc} \"{w.Title}\" placed in {name}");
        }
        s.Index++;
        AssistLater(NextAssistStep, 90);   // let the screen repaint before the next picker takes its backdrop
    }

    void OnAssistSkipped(string why)
    {
        var s = _as;
        if (s == null) return;
        Log($"snap assist: zone {s.Zones[s.Index] + 1} skipped ({why})");
        _assistForm!.Hide();
        _assistKeys!.Uninstall();
        s.Index++;
        AssistLater(NextAssistStep, 60);
    }

    void OnAssistKey(int vk)
    {
        if (_as == null || _assistForm == null) return;
        if (vk == 0x1B) { EndAssist("Esc"); return; }
        _assistForm.Key(vk);
    }

    void EndAssist(string why)
    {
        var s = _as;
        if (s == null) return;
        _as = null;
        _assistKeys?.Uninstall();
        _assistForm?.Hide();
        lock (s)
        {
            s.Dead = true;
            foreach (var b in s.Thumbs.Values) b.Dispose();
            s.Thumbs.Clear();
        }
        foreach (var w in s.Entries) w.Dispose();
        s.Entries.Clear();
        Log($"snap assist: ended ({why})");
    }

    void DisposeAssist()
    {
        EndAssist("app stopped");
        _assistKeys?.Dispose();
        _assistForm?.Close();
        _assistForm?.Dispose();
    }
}

/// <summary>One window offered in the picker.</summary>
sealed class AssistWindow : IDisposable
{
    public IntPtr Hwnd;
    public string Title = "", Proc = "";
    public Bitmap? Icon;
    public bool Minimized;
    public void Dispose() { Icon?.Dispose(); Icon = null; }
}

static class AssistWindows
{
    delegate bool EnumProc(IntPtr h, IntPtr lp);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr lp);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] static extern IntPtr GetClassLongPtr(IntPtr h, int idx);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);

    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000;
    const int DWMWA_CLOAKED = 14;
    const uint GW_OWNER = 4, WM_GETICON = 0x7F;

    /// <summary>The windows Alt-Tab would list, top of the z-order first, minus <paramref name="exclude"/>.</summary>
    public static List<AssistWindow> Enumerate(HashSet<IntPtr> exclude)
    {
        var list = new List<AssistWindow>();
        int me = Environment.ProcessId;
        IntPtr shell = GetShellWindowSafe();
        EnumWindows((h, _) =>
        {
            try
            {
                if (h == shell || exclude.Contains(h) || !Native.IsWindowVisible(h)) return true;
                Native.GetWindowThreadProcessId(h, out uint pid);
                if (pid == (uint)me) return true;
                if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true;
                string title = Native.Title(h);
                if (title.Length == 0) return true;
                int ex = GetWindowLong(h, GWL_EXSTYLE);
                bool app = (ex & WS_EX_APPWINDOW) != 0;
                if (!app && (ex & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return true;
                if (!app && GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
                var cls = new StringBuilder(64);
                GetClassName(h, cls, cls.Capacity);
                string c = cls.ToString();
                if (c is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
                bool min = Native.IsIconic(h);
                if (!min) { Native.GetWindowRect(h, out var r); if (r.Width <= 1 || r.Height <= 1) return true; }
                list.Add(new AssistWindow { Hwnd = h, Title = title, Proc = Native.ProcessName(h), Minimized = min, Icon = IconOf(h, pid) });
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    static IntPtr GetShellWindowSafe() { try { return GetShellWindow(); } catch { return IntPtr.Zero; } }

    static Bitmap? IconOf(IntPtr h, uint pid)
    {
        try
        {
            IntPtr ic = IntPtr.Zero;
            foreach (int kind in new[] { 1, 2 })
                if (ic == IntPtr.Zero && Native.SendMessageTimeout(h, WM_GETICON, (IntPtr)kind, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 40, out var r) != IntPtr.Zero) ic = r;
            if (ic == IntPtr.Zero) ic = GetClassLongPtr(h, -14);
            if (ic == IntPtr.Zero) ic = GetClassLongPtr(h, -34);
            if (ic != IntPtr.Zero) { using var i = Icon.FromHandle(ic); return i.ToBitmap(); }
            string? path = System.Diagnostics.Process.GetProcessById((int)pid).MainModule?.FileName;
            if (path != null) { using var i = Icon.ExtractAssociatedIcon(path); return i?.ToBitmap(); }
        }
        catch { }
        return null;
    }

    /// <summary>A picture of the window, at most <paramref name="maxDim"/> pixels on the long side, or null on failure.</summary>
    public static Bitmap? Capture(IntPtr h, int maxDim)
    {
        try
        {
            if (!Native.IsWindowVisible(h) || Native.IsIconic(h) || Native.IsHungAppWindow(h)) return null;
            Native.GetWindowRect(h, out var w);
            if (w.Width < 2 || w.Height < 2 || w.Width > 8000 || w.Height > 8000) return null;
            using var full = new Bitmap(w.Width, w.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            bool ok;
            using (var g = Graphics.FromImage(full))
            {
                IntPtr dc = g.GetHdc();
                try { ok = PrintWindow(h, dc, 2 /* PW_RENDERFULLCONTENT */); }
                finally { g.ReleaseHdc(dc); }
            }
            if (!ok) return null;
            // Crop the invisible resize border.
            var f = Native.FrameBounds(h);
            var src = new Rectangle(f.Left - w.Left, f.Top - w.Top, f.Width, f.Height);
            src.Intersect(new Rectangle(0, 0, full.Width, full.Height));
            if (src.Width < 2 || src.Height < 2) src = new Rectangle(0, 0, full.Width, full.Height);
            double k = Math.Min(1.0, maxDim / (double)Math.Max(src.Width, src.Height));
            int tw = Math.Max(1, (int)Math.Round(src.Width * k)), th = Math.Max(1, (int)Math.Round(src.Height * k));
            var thumb = new Bitmap(tw, th, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(thumb))
            {
                g.Clear(Color.FromArgb(255, 24, 24, 28));
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(full, new Rectangle(0, 0, tw, th), src, GraphicsUnit.Pixel);
            }
            return thumb;
        }
        catch { return null; }
    }
}

/// <summary>
/// Low-level keyboard hook for the picker: swallows Esc, Enter and the arrow keys while it is installed,
/// and reports each key down. Installed only while a picker is open. Runs on the UI thread; work is posted.
/// </summary>
sealed class AssistKeys : IDisposable
{
    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);

    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
    const int VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28, VK_CONTROL = 0x11;

    readonly HookProc _proc;   // kept alive so the GC does not collect the callback
    readonly Action<int> _onKey;
    readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    IntPtr _hook;

    public AssistKeys(Action<int> onKey) { _onKey = onKey; _proc = OnKey; }

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) DragWatcher.Log("snap assist: key hook failed; use the mouse, or click outside to skip");
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int vk = Marshal.ReadInt32(lParam);
            int flags = Marshal.ReadInt32(lParam, 8);
            bool alt = (flags & 0x20) != 0, ctrl = (Native.GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            if (!alt && !ctrl && vk is VK_RETURN or VK_ESCAPE or VK_LEFT or VK_UP or VK_RIGHT or VK_DOWN)
            {
                int msg = (int)wParam;
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    if (_ui != null) _ui.Post(_ => _onKey(vk), null); else _onKey(vk);
                }
                return (IntPtr)1; // swallow key down and key up
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}

/// <summary>
/// The picker: a faint scrim over the monitor's work area (so a click outside is caught), the zone outlined,
/// and a glass panel inside the zone with one tile per window plus a "Skip" tile. Never takes focus.
/// </summary>
sealed class AssistForm : OverlayForm
{
    public Action<int>? Picked;
    public Action? Skipped;
    public Func<IntPtr, Bitmap?> Thumb = _ => null;

    double _sc = 1;
    Rectangle _zone, _panel, _header;
    List<AssistWindow> _items = new();
    Rectangle[] _tiles = Array.Empty<Rectangle>();   // one per item, then the Skip tile; window coordinates
    int _cols = 1, _sel, _hover = -1, _titleH, _pad;
    string _head = "";
    long _openedAt;
    Bitmap? _blur;
    Font? _fTitle, _fHead, _fHint;
    double _fontScale;

    int S(double v) => (int)Math.Round(v * _sc);

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle &= ~0x20; return cp; }   // clickable: drop WS_EX_TRANSPARENT
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0084) { m.Result = (IntPtr)1; return; }   // HTCLIENT
        base.WndProc(ref m);
    }

    int TileHeight(int tw) => (int)(tw * 0.62) + _titleH;

    /// <summary>Lay out and show the picker for a zone. Returns false when the zone is too small. The form must be hidden when called.</summary>
    public bool Open(RECT work, RECT zone, double scale, List<AssistWindow> items, string label)
    {
        _sc = scale;
        _zone = new Rectangle(zone.Left - work.Left, zone.Top - work.Top, zone.Width, zone.Height);
        int inset = _zone.Width > S(240) && _zone.Height > S(200) ? S(10) : S(4);
        var panel = Rectangle.Inflate(_zone, -inset, -inset);
        if (panel.Width < S(130) || panel.Height < S(90)) return false;
        _pad = S(14); _titleH = S(28);
        int headH = panel.Height > S(260) ? S(34) : 0;
        var avail = new Rectangle(panel.X + _pad, panel.Y + _pad + headH, panel.Width - 2 * _pad, panel.Height - 2 * _pad - headH);
        if (avail.Width < S(80) || avail.Height < S(60)) return false;
        int gap = S(10);

        var list = items.ToList();
        int n = list.Count + 1;
        int bestCols = 0, bestTw = 0;
        for (int cols = 1; cols <= n; cols++)
        {
            int tw = Math.Min(S(280), (avail.Width - (cols - 1) * gap) / cols);
            if (tw < S(110)) break;
            int rows = (n + cols - 1) / cols;
            if (rows * TileHeight(tw) + (rows - 1) * gap <= avail.Height && tw > bestTw) { bestTw = tw; bestCols = cols; }
        }
        if (bestCols == 0)
        {
            // Too many windows for this zone: show as many as fit at the smallest size.
            bestTw = Math.Min(S(110), avail.Width);
            bestCols = Math.Max(1, (avail.Width + gap) / (bestTw + gap));
            int cap = bestCols * Math.Max(0, (avail.Height + gap) / (TileHeight(bestTw) + gap));
            if (cap < 2) return false;
            if (list.Count > cap - 1) list = list.Take(cap - 1).ToList();
            n = list.Count + 1;
        }
        _cols = Math.Min(bestCols, n);
        int th = TileHeight(bestTw);
        int gridW = _cols * bestTw + (_cols - 1) * gap;
        int x0 = avail.X + (avail.Width - gridW) / 2;
        _tiles = new Rectangle[n];
        for (int i = 0; i < n; i++)
            _tiles[i] = new Rectangle(x0 + (i % _cols) * (bestTw + gap), avail.Y + (i / _cols) * (th + gap), bestTw, th);

        _items = list; _sel = 0; _hover = -1; _panel = panel;
        _header = new Rectangle(panel.X + _pad, panel.Y + S(10), panel.Width - 2 * _pad, headH);
        _head = headH > 0 ? "Snap a window here" : "";
        if (Math.Abs(_fontScale - scale) > 0.001 || _fTitle == null)
        {
            _fTitle?.Dispose(); _fHead?.Dispose(); _fHint?.Dispose();
            _fTitle = Theme.UiFont(12.5 * scale); _fHead = Theme.DisplayFont(15 * scale); _fHint = Theme.UiFont(11.5 * scale);
            _fontScale = scale;
        }
        _blur?.Dispose();
        _blur = Glass.Backdrop(new Rectangle(work.Left + panel.X, work.Top + panel.Y, panel.Width, panel.Height), Theme.BlurRadiusSmall);

        SetGeometry(new Rectangle(work.Left, work.Top, work.Width, work.Height));
        _openedAt = Environment.TickCount64;
        Anim.Stop();
        ShowRendered(Theme.Animate ? (byte)0 : (byte)255);
        if (Theme.Animate) Anim.Start(Theme.FadeInMs, t => Compose((byte)Math.Round(255 * Theme.Ease(t))));
        return true;
    }

    Rectangle Dirty => Rectangle.Inflate(_zone, S(30), S(30));

    protected override void OnRenderRequest() => Render(Dirty);

    int Hit(Point p)
    {
        for (int i = 0; i < _tiles.Length; i++) if (_tiles[i].Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int i = Hit(e.Location);
        if (i == _hover) return;
        _hover = i;
        if (i >= 0) _sel = i;
        Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || Environment.TickCount64 - _openedAt < 250) return;
        int i = Hit(e.Location);
        if (i >= 0) Choose(i);
        else if (!_panel.Contains(e.Location)) Skipped?.Invoke();
    }

    void Choose(int i)
    {
        if (i == _items.Count) Skipped?.Invoke(); else Picked?.Invoke(i);
    }

    /// <summary>Arrow keys move the selection, Enter chooses it.</summary>
    public void Key(int vk)
    {
        int n = _tiles.Length;
        if (n == 0) return;
        switch (vk)
        {
            case 0x25: _sel = Math.Max(0, _sel - 1); break;
            case 0x27: _sel = Math.Min(n - 1, _sel + 1); break;
            case 0x26: if (_sel - _cols >= 0) _sel -= _cols; break;
            case 0x28: if (_sel + _cols < n) _sel += _cols; else if (_sel / _cols < (n - 1) / _cols) _sel = n - 1; break;
            case 0x0D: Choose(_sel); return;
        }
        Invalidate();
    }

    protected override void PaintLayer(Surface s, Graphics g, Rectangle dirty)
    {
        s.Fill(dirty, Color.FromArgb(Math.Max(16, (int)Theme.Scrim.A), Theme.Scrim.R, Theme.Scrim.G, Theme.Scrim.B));
        if (_tiles.Length == 0 || _fTitle == null) return;
        float f = (float)_sc;
        // The zone being filled.
        var zr = RectangleF.Inflate(_zone, -2 * f, -2 * f);
        using (var zp = Theme.Round(zr, Theme.RadiusZone * f))
        {
            using (var fill = new SolidBrush(Theme.ZoneFill)) g.FillPath(fill, zp);
            using var edge = new Pen(Theme.AccentSoft, 2 * f);
            g.DrawPath(edge, zp);
        }
        var pr = (RectangleF)_panel;
        float radius = Theme.RadiusPanel * f;
        Glass.Shadow(g, pr, radius, Theme.ShadowSize * f, Theme.ShadowOffsetY * f);
        Glass.Panel(g, pr, radius, _blur, pr, f);

        using var sf = new StringFormat(StringFormat.GenericTypographic)
        {
            Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        using var text = new SolidBrush(Theme.Text);
        using var dim = new SolidBrush(Theme.Dim);
        if (_head.Length > 0)
        {
            g.DrawString(_head, _fHead!, text, _header, sf);
            using var right = new StringFormat(sf) { Alignment = StringAlignment.Far };
            g.DrawString("Arrows + Enter, Esc to stop", _fHint!, dim, _header, right);
        }

        var oldInterp = g.InterpolationMode;
        for (int i = 0; i < _tiles.Length; i++)
        {
            var r = (RectangleF)_tiles[i];
            if (!r.IntersectsWith(dirty)) continue;
            bool sel = i == _sel;
            using (var tp = Theme.Round(r, Theme.RadiusCard * f))
            {
                using (var fill = new SolidBrush(sel ? Theme.CardHot : Theme.Card)) g.FillPath(fill, tp);
                if (sel) { using var ring = new Pen(Theme.Accent, 2 * f); g.DrawPath(ring, tp); }
                else { using var line = new Pen(Theme.PanelBorder, Math.Max(1f, f)); g.DrawPath(line, tp); }
            }
            var prev = new RectangleF(r.X + 5 * f, r.Y + 5 * f, r.Width - 10 * f, r.Height - _titleH - 5 * f);
            var titleR = new RectangleF(r.X + 8 * f, r.Bottom - _titleH, r.Width - 16 * f, _titleH);
            if (i == _items.Count)
            {
                // Skip tile: a cross and the word.
                float cx = prev.X + prev.Width / 2, cy = prev.Y + prev.Height / 2, d = 9 * f;
                using (var pen = new Pen(Theme.Dim, 2 * f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(pen, cx - d, cy - d, cx + d, cy + d);
                    g.DrawLine(pen, cx - d, cy + d, cx + d, cy - d);
                }
                using var center = new StringFormat(sf) { Alignment = StringAlignment.Center };
                g.DrawString("Skip this zone", _fTitle, text, titleR, center);
                continue;
            }
            var item = _items[i];
            var thumb = Thumb(item.Hwnd);
            if (thumb != null)
            {
                float k = Math.Min(prev.Width / thumb.Width, prev.Height / thumb.Height);
                float tw = thumb.Width * k, th = thumb.Height * k;
                var dest = new RectangleF(prev.X + (prev.Width - tw) / 2, prev.Y + (prev.Height - th) / 2, tw, th);
                var clip = g.Clip;
                using (var cp = Theme.Round(dest, Theme.RadiusMini * f)) g.SetClip(cp, CombineMode.Intersect);
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.DrawImage(thumb, dest);
                g.Clip = clip;
                g.InterpolationMode = oldInterp;
                using var edge = new Pen(Theme.MiniZoneEdge, Math.Max(1f, f));
                using (var ep = Theme.Round(dest, Theme.RadiusMini * f)) g.DrawPath(edge, ep);
            }
            else if (item.Icon != null)
            {
                float sz = 40 * f;
                g.DrawImage(item.Icon, new RectangleF(prev.X + (prev.Width - sz) / 2, prev.Y + (prev.Height - sz) / 2 - (item.Minimized ? 6 * f : 0), sz, sz));
            }
            if (item.Minimized && thumb == null)
            {
                using var center = new StringFormat(sf) { Alignment = StringAlignment.Center };
                g.DrawString("Minimized", _fHint!, dim, new RectangleF(prev.X, prev.Bottom - 24 * f, prev.Width, 20 * f), center);
            }
            float ix = titleR.X;
            if (item.Icon != null)
            {
                float isz = 16 * f;
                g.DrawImage(item.Icon, new RectangleF(ix, titleR.Y + (titleR.Height - isz) / 2, isz, isz));
                ix += isz + 6 * f;
            }
            g.DrawString(item.Title, _fTitle, text, new RectangleF(ix, titleR.Y, titleR.Right - ix, titleR.Height), sf);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _blur?.Dispose(); _fTitle?.Dispose(); _fHead?.Dispose(); _fHint?.Dispose(); }
        base.Dispose(disposing);
    }
}
