namespace RegionControl;

/// <summary>
/// Drag a window by its title bar to the top edge of a monitor: the picker strip appears with that monitor's layouts.
/// Hover a thumbnail to see its zones full screen, then drop on a zone (full screen or mini) to snap the window.
/// The app runs in the tray with no console. Quit from the tray menu.
/// </summary>
public partial class DragWatcher : ApplicationContext
{
    readonly Native.WinEventProc _proc;   // kept alive so the GC does not collect the callback
    readonly IntPtr _hook;
    readonly System.Windows.Forms.Timer _poll = new() { Interval = 10 };
    readonly StripForm _strip = new();
    readonly ZonesForm _zones = new();
    readonly NotifyIcon _tray;
    readonly EscHook _esc = new();
    public readonly Library Lib;

    List<MonitorInfo> _monitors = new();
    IntPtr _drag;            // window being moved, or zero
    MonitorInfo? _mon;       // monitor the strip is shown on
    bool _shown, _cancelled;
    EditorForm? _editor;
    DragOrigin? _origin;     // where the current drag began

    public DragWatcher()
    {
        Lib = Library.Load(Library.DefaultPath);
        Log(Lib.LoadNote ?? "layouts loaded");
        RefreshMonitors(log: true);

        _proc = OnWinEvent;
        _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_MOVESIZESTART, Native.EVENT_SYSTEM_MOVESIZEEND,
            IntPtr.Zero, _proc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        if (_hook == IntPtr.Zero) throw new InvalidOperationException("SetWinEventHook failed");
        _poll.Tick += (_, _) => Track();
        // Create handles now, on this thread, so the first show is fast.
        _ = _strip.Handle; _ = _zones.Handle;

        _tray = CreateTray();

        Log("Ready. Drag a window by its title bar to the top edge of a screen.");
        Log("Tray icon: right-click for the menu (editor per monitor, Start with Windows, Quit); double-click edits the monitor under the cursor.");
    }

    // ---------- monitors and sets ----------

    void RefreshMonitors(bool log = false)
    {
        _monitors = Monitors.Enumerate();
        if (_monitors.Count == 0)
        {
            // Should not happen; fall back to the monitor under the cursor so the picker still works.
            Native.GetCursorPos(out var p);
            var (mon, work, scale) = Native.MonitorAt(p);
            _monitors.Add(new MonitorInfo { Handle = Native.MonitorFromPoint(p, Native.MONITOR_DEFAULTTONEAREST), Id = "FALLBACK", Bounds = mon, Work = work, Scale = scale, Number = 1 });
        }
        if (Lib.EnsureSetup(_monitors))
        {
            Lib.Save();
            log = true;
            Log($"new monitor setup saved: \"{Lib.Set(SetupKey)?.Name}\"");
        }
        if (log) ListSetup();
    }

    public string SetupKey => Monitors.SetupKey(_monitors);
    public IReadOnlyList<MonitorInfo> CurrentMonitors => _monitors;

    MonitorInfo MonitorAt(POINT p)
    {
        IntPtr h = Native.MonitorFromPoint(p, Native.MONITOR_DEFAULTTONEAREST);
        var m = _monitors.FirstOrDefault(x => x.Handle == h);
        if (m == null) { RefreshMonitors(); m = _monitors.FirstOrDefault(x => x.Handle == h) ?? _monitors[0]; }
        return m;
    }

    public void ListSetup()
    {
        var set = Lib.Set(SetupKey);
        Log($"setup \"{set?.Name}\" with {_monitors.Count} monitor(s); library has {Lib.Data.Layouts.Count} layouts, {Lib.Data.Sets.Count} sets");
        foreach (var m in _monitors)
        {
            var strip = Lib.StripFor(m.Key, SetupKey);
            string main = Lib.Find(Lib.MainFor(SetupKey, m.Key))?.Name ?? "-";
            Log($"  {m}  work {m.Work}  main: {main}  strip: {string.Join(", ", strip.Select(l => l.Name))}");
        }
    }

    // ---------- drag tracking ----------

    void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || hwnd == IntPtr.Zero) return;
        if (evt == Native.EVENT_SYSTEM_MOVESIZESTART) OnStart(Native.GetAncestor(hwnd, Native.GA_ROOT));
        else if (evt == Native.EVENT_SYSTEM_MOVESIZEEND) OnEnd();
    }

    void OnStart(IntPtr hwnd)
    {
        Native.GetCursorPos(out var p);
        // Ask the window what is under the cursor. Borders (HTLEFT..HTBOTTOMRIGHT = 10..17) mean a resize.
        int ht = -1;
        IntPtr lp = (IntPtr)(((p.Y & 0xFFFF) << 16) | (p.X & 0xFFFF));
        if (Native.SendMessageTimeout(hwnd, Native.WM_NCHITTEST, IntPtr.Zero, lp, Native.SMTO_ABORTIFHUNG, 100, out var res) != IntPtr.Zero)
            ht = (int)res;
        string who = $"{Native.ProcessName(hwnd)} \"{Native.Title(hwnd)}\"";
        if (ht >= 10 && ht <= 17) { _saved.Remove(hwnd); Log($"resize start (hit {ht}), ignored: {who}"); return; }
        EndAssist("a new drag started");
        _origin = CaptureOrigin(hwnd);
        RestoreOnDragOut(hwnd);
        _drag = hwnd;
        _cancelled = false;
        Log($"move start (hit {ht}{(ht == Native.HTCAPTION ? " caption" : "")}): {who}");
        _poll.Start();
    }

    void ShowStrip(MonitorInfo hit)
    {
        if (Lib.ChangedOnDisk) { Lib.Reload(); Log("layouts file changed on disk: " + Lib.LoadNote); }
        RefreshMonitors();
        var mon = _monitors.FirstOrDefault(m => m.Handle == hit.Handle) ?? hit;
        var layouts = Lib.StripFor(mon.Key, SetupKey);
        _zones.Hide();
        _strip.Place(mon, layouts, Lib.MainFor(SetupKey, mon.Key));
        _mon = mon;
        _shown = true;
        _esc.Install();
        Log($"picker shown on {mon}, work {mon.Work}, {layouts.Count} layouts");
    }

    void Track()
    {
        if (_drag == IntPtr.Zero || _cancelled) return;
        if (_shown)
        {
            // Esc (swallowed by the hook while the picker is open) or a right-click cancels with no snap.
            bool esc = _esc.Pressed || (!_esc.Installed && (Native.GetAsyncKeyState(Native.VK_ESCAPE) & 0x8000) != 0);
            bool right = (Native.GetAsyncKeyState(0x02) & 0x8000) != 0;
            if (esc || right)
            {
                _cancelled = true;
                HideOverlay();
                Log($"picker cancelled with {(esc ? "Esc" : "right-click")}");
                return;
            }
        }
        Native.GetCursorPos(out var p);
        var mon = MonitorAt(p);
        bool atTop = p.Y <= mon.Bounds.Top + (int)Math.Round(6 * mon.Scale);

        if (atTop && (!_shown || mon.Key != _mon!.Key || mon.Handle != _mon.Handle)) ShowStrip(mon);
        if (!_shown) return;
        if (mon.Handle != _mon!.Handle) { HideOverlay(); return; } // moved to another monitor below its top edge

        // "Layout" pill: dropping here opens the editor. It keeps the last card's zones on screen, none lit.
        bool pill = _strip.HitPill(p);
        _strip.SetPillHover(pill);
        if (pill)
        {
            if (_strip.HoverZone != -1) { _strip.HoverZone = -1; _strip.Invalidate(); _zones.Hover = -1; _zones.Invalidate(); }
            return;
        }

        var (li, zi) = _strip.HitTest(p);
        int active = _strip.Active, hover;
        if (li >= 0)
        {
            if (li != active) { active = li; _zones.ShowLayout(_strip.Layouts[li], _mon.Work, _mon.Scale); _strip.ShowAt(_strip.PanelRect); }
            hover = zi;
        }
        else if (_strip.PanelRect.Contains(p)) hover = -1;
        else if (active < 0)
        {
            // No layout chosen yet: hide the strip when the cursor moves away from it.
            int m = (int)Math.Round(50 * _strip.DpiScale);
            var keep = new RECT(_strip.PanelRect.Left - m, _mon.Bounds.Top, _strip.PanelRect.Right + m, _strip.PanelRect.Bottom + m);
            if (!keep.Contains(p) && !atTop) HideOverlay();
            return;
        }
        else hover = _zones.HitTest(p);

        if (active != _strip.Active || hover != _strip.HoverZone)
        {
            _strip.Active = active; _strip.HoverZone = hover; _strip.Invalidate();
            _zones.Hover = hover; _zones.Invalidate();
        }
    }

    void OnEnd()
    {
        if (_drag == IntPtr.Zero) return;
        if (!_cancelled) Track();
        var hwnd = _drag;
        bool toEditor = _shown && !_cancelled && _strip.PillHover;
        ZoneLayout? source = toEditor ? _strip.EditSource : null;
        var editMon = _mon;
        ZoneLayout? layout = _shown && _strip.Active >= 0 ? _strip.Layouts[_strip.Active] : null;
        int zone = layout != null ? _strip.HoverZone : -1;
        var work = _strip.Work;
        double scale = _mon?.Scale ?? 1;
        var origin = _origin;
        _drag = IntPtr.Zero;
        _poll.Stop();
        HideOverlay();
        if (toEditor && editMon != null)
        {
            // Dropped on the "Layout" pill: no snap, the window stays where it is. Open the editor once the move loop ends.
            Log($"move end on the Layout pill: editor from \"{source?.Name ?? "blank"}\"");
            var te = new System.Windows.Forms.Timer { Interval = 30 };
            te.Tick += (_, _) =>
            {
                te.Stop(); te.Dispose();
                if (origin != null) ReturnToOrigin(hwnd, origin, "dropped on the Layout pill");
                OpenEditor(editMon, source);
            };
            te.Start();
            return;
        }
        if (layout == null || zone < 0)
        {
            Log(_cancelled ? "move end, cancelled" : "move end, dropped outside every zone: cancelled, no snap");
            if (_cancelled && origin != null)
            {
                // Esc or right-click with the picker open: put the window back once its move loop ends.
                var tc = new System.Windows.Forms.Timer { Interval = 30 };
                tc.Tick += (_, _) => { tc.Stop(); tc.Dispose(); ReturnToOrigin(hwnd, origin, "picker cancelled"); };
                tc.Start();
            }
            return;
        }
        var target = layout.ZoneRect(zone, work);
        string name = $"{layout.Name} / {(layout.Zones[zone].Name.Length > 0 ? layout.Zones[zone].Name : $"zone {zone + 1}")}";
        // Let the window's move loop finish before we place it.
        var t = new System.Windows.Forms.Timer { Interval = 30 };
        var snapMon = _mon;
        int snapZone = zone;
        t.Tick += (_, _) =>
        {
            t.Stop(); t.Dispose();
            Snap(hwnd, target, name); SnapFlash.Show(target, scale);
            if (snapMon != null) BeginAssist(hwnd, layout, snapZone, snapMon);
        };
        t.Start();
    }

    void HideOverlay()
    {
        _shown = false;
        _esc.Uninstall();
        _strip.Active = -1; _strip.HoverZone = -1;
        _strip.Hide();
        _zones.Hide();
    }

    void Snap(IntPtr hwnd, RECT z, string name)
    {
        if (Native.IsZoomed(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
        RememberBeforeSnap(hwnd);
        // Two passes: the invisible border size can change when the window lands on a monitor with another DPI.
        for (int i = 0; i < 2; i++)
        {
            Native.GetWindowRect(hwnd, out var w);
            var f = Native.FrameBounds(hwnd);
            int l = f.Left - w.Left, t = f.Top - w.Top, r = w.Right - f.Right, b = w.Bottom - f.Bottom;
            Native.SetWindowPos(hwnd, IntPtr.Zero, z.Left - l, z.Top - t, z.Width + l + r, z.Height + t + b,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        var got = Native.FrameBounds(hwnd);
        RememberZoneFrame(hwnd, got);
        bool ok = got.Left == z.Left && got.Top == z.Top && got.Right == z.Right && got.Bottom == z.Bottom;
        Log($"snapped to {name}: target {z}, frame now {got}{(ok ? "" : "  <-- MISMATCH (app may enforce a minimum size or be elevated)")}");
    }

    // ---------- editor and tray ----------

    public void OpenEditor(MonitorInfo? mon, ZoneLayout? startFrom = null)
    {
        RefreshMonitors();
        if (mon == null) { Native.GetCursorPos(out var p); mon = MonitorAt(p); }
        else mon = _monitors.FirstOrDefault(m => m.Key == mon.Key) ?? mon;
        if (_editor != null && !_editor.IsDisposed)
        {
            _editor.Activate();
            _editor.Toast("Close this editor (Esc) before you edit another monitor.");
            return;
        }
        _editor = new EditorForm(Lib, mon, _monitors.ToList(), SetupKey, Lib.Set(SetupKey)?.Name ?? "");
        _editor.FormClosed += (_, _) => { _editor = null; Log("editor closed"); };
        if (startFrom != null) _editor.StartFromCopy(startFrom);
        _editor.Show();
        _editor.Activate();
        Log($"editor opened on {mon}");
    }

    public static readonly string LogDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RegionControl", "logs");
    static readonly object _logLock = new();

    /// <summary>Writes to a daily log file under %APPDATA%\RegionControl\logs, and to the console when one is attached.</summary>
    public static void Log(string s)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff}  {s}";
        try { Console.WriteLine(line); } catch { }
        try
        {
            lock (_logLock)
            {
                Directory.CreateDirectory(LogDir);
                File.AppendAllText(System.IO.Path.Combine(LogDir, $"regioncontrol-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
            }
        }
        catch { }
    }

    /// <summary>Deletes log files older than 14 days.</summary>
    public static void PruneLogs()
    {
        try
        {
            foreach (var f in Directory.GetFiles(LogDir, "regioncontrol-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14)) File.Delete(f);
        }
        catch { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _poll.Stop();
            _esc.Dispose();
            DisposeAssist();
            Native.UnhookWinEvent(_hook);
            _strip.Close(); _strip.Dispose();
            _zones.Close(); _zones.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            Log("stopped: hook removed, overlays closed, tray icon removed");
        }
        base.Dispose(disposing);
    }
}

static class Program
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern IntPtr GetStdHandle(int n);

    [STAThread]
    static int Main(string[] args)
    {
        // The app has no console of its own. The test modes borrow the console of the cmd window that started them.
        bool cli = args.Contains("--selftest") || args.Contains("--render") || args.Contains("--monitors");
        // Attach only when output is not already piped or redirected, so "| more" keeps working.
        if (cli && (GetStdHandle(-11) == IntPtr.Zero)) AttachConsole(-1);

        if (args.Contains("--selftest")) return SelfTest.Run();
        int ri = Array.IndexOf(args, "--render");
        if (ri >= 0 && ri + 1 < args.Length)
        {
            // Draw the editor and picker into PNG files with hidden windows. No input, temp library only.
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            return RenderCheck.Run(args[ri + 1]);
        }
        if (args.Contains("--monitors"))
        {
            // Read only: print the monitors and the setup key. Writes no files.
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            var mons = Monitors.Enumerate();
            foreach (var m in mons) Console.WriteLine($"{m}  key {m.Key}  bounds {m.Bounds}  work {m.Work}{(m.Primary ? "  primary" : "")}");
            Console.WriteLine("setup key: " + Monitors.SetupKey(mons));
            return 0;
        }

        // One copy only. A second copy signals the first, which shows a balloon, then exits.
        using var mutex = new Mutex(true, @"Local\RegionControl.Single", out bool first);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\RegionControl.Show");
        if (!first)
        {
            showEvent.Set();
            return 0;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => DragWatcher.Log("error: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => DragWatcher.Log("fatal: " + e.ExceptionObject);

        DragWatcher.PruneLogs();
        DragWatcher.Log("started");
        Theme.Watch();
        var ctx = new DragWatcher();
        var ui = SynchronizationContext.Current;
        var watcher = new Thread(() =>
        {
            while (showEvent.WaitOne()) ui?.Post(_ => ctx.ShowAlreadyRunning(), null);
        }) { IsBackground = true };
        watcher.Start();

        Application.Run(ctx);
        ctx.Dispose();
        return 0;
    }
}
