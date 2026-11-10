using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace RegionControl;

/// <summary>
/// Developer check: paints the picker, zones overlay, editor, snap confirmation and icons into PNG files,
/// over a generated sample wallpaper, without showing or focusing any window. Also times the overlays at 3440x1440.
/// The layouts library goes to a temp folder. Reads no screen.
/// </summary>
public static class RenderCheck
{
    public static int Run(string outDir)
    {
        // "--render media:<docs folder>" writes the README banners and screenshots instead.
        if (outDir.StartsWith("media:", StringComparison.OrdinalIgnoreCase)) return Media(outDir[6..]);
        Directory.CreateDirectory(outDir);
        string tmp = Path.Combine(Path.GetTempPath(), "RegionControl-render");
        Directory.CreateDirectory(tmp);
        var lib = Library.Load(Path.Combine(tmp, "layouts.json"));
        var log = new List<string>();

        foreach (bool dark in new[] { true, false })
        {
            Theme.ForceMode(dark, true);
            string sfx = dark ? "" : "-light";
            var mon = Mon(1, 0, 1920, 1080, 1032);
            var mon2 = Mon(2, 1920, 1920, 1080, 1032);
            var mons = new List<MonitorInfo> { mon, mon2 };
            lib.EnsureSetup(mons);
            string key = Monitors.SetupKey(mons);
            using var wall = Wallpaper(1920, 1080, dark);
            Glass.Source = (r, f) => Glass.Downscale(wall, r, f);

            // Picker: zones overlay plus strip, as the user sees it mid-drag.
            using (var zones = new ZonesForm())
            using (var strip = new StripForm())
            {
                var layouts = lib.StripFor(mon.Key, key);
                strip.Place(mon, layouts, lib.MainFor(key, mon.Key), show: false);
                strip.SetActiveForRender(1, 1);
                zones.ShowLayout(layouts[1], mon.Work, 1, show: false);
                zones.SetHoverForRender(1);
                using var zb = zones.Snapshot();
                using var sb = strip.Snapshot();
                using var pic = Compose(wall, (zb, zones.WindowRect.Location), (sb, strip.WindowRect.Location));
                pic.Save(Path.Combine(outDir, $"picker{sfx}.png"), ImageFormat.Png);
                var crop = Rectangle.Intersect(strip.WindowRect, new Rectangle(0, 0, wall.Width, wall.Height));
                using var c2 = Crop(pic, crop, 2);
                c2.Save(Path.Combine(outDir, $"strip{sfx}-2x.png"), ImageFormat.Png);

                // Strip alone before a layout is chosen.
                strip.SetActiveForRender(-1, -1);
                using var sb0 = strip.Snapshot();
                using var pic0 = Compose(wall, (sb0, strip.WindowRect.Location));
                using var c0 = Crop(pic0, crop, 2);
                c0.Save(Path.Combine(outDir, $"strip-idle{sfx}-2x.png"), ImageFormat.Png);

                // "Layout" pill lit, no card hovered yet: the editor would start from the main layout.
                strip.SetPillForRender(true);
                using var sbp = strip.Snapshot();
                using var picp = Compose(wall, (sbp, strip.WindowRect.Location));
                using var cp = Crop(picp, crop, 2);
                cp.Save(Path.Combine(outDir, $"strip-pill{sfx}-2x.png"), ImageFormat.Png);

                // Pill lit after hovering card 2: its zones stay on screen, none lit; the editor would start from it.
                strip.SetActiveForRender(1, -1);
                strip.SetPillForRender(true);
                zones.SetHoverForRender(-1);
                using var zbp = zones.Snapshot();
                using var sbp2 = strip.Snapshot();
                using var picp2 = Compose(wall, (zbp, zones.WindowRect.Location), (sbp2, strip.WindowRect.Location));
                picp2.Save(Path.Combine(outDir, $"picker-pill{sfx}.png"), ImageFormat.Png);
            }

            // Tray menu, one monitor and two monitors.
            foreach (var set in new[] { new List<MonitorInfo> { mon }, mons })
            {
                using var menu = new ContextMenuStrip();
                DragWatcher.FillTrayMenu(menu, set, true, null);
                var size = menu.GetPreferredSize(Size.Empty);
                menu.Size = size;
                using var bmp = new Bitmap(size.Width, size.Height);
                menu.DrawToBitmap(bmp, new Rectangle(Point.Empty, size));
                using var z2 = Crop(bmp, new Rectangle(Point.Empty, size), 2);
                z2.Save(Path.Combine(outDir, $"tray-menu{(set.Count > 1 ? "-2mon" : "")}{sfx}-2x.png"), ImageFormat.Png);
                log.Add($"tray menu{(set.Count > 1 ? ", 2 monitors" : "")}{(dark ? "" : ", light")}: {size.Width} x {size.Height} px at 100 %");
            }

            // Snap confirmation on the zone the window landed in.
            {
                var target = lib.Data.Layouts[1].ZoneRect(1, mon.Work);
                using var fb = SnapFlash.Render(target, 1);
                int pad = 8;
                using var pic = Compose(wall, (fb, new Point(target.Left - pad, target.Top - pad)));
                pic.Save(Path.Combine(outDir, $"snap-confirm{sfx}.png"), ImageFormat.Png);
            }

            // Editor.
            // Wider monitor for the editor (1720x720 at 200 %, ultrawide) so its toolbar stays on one row.
        var emon = new MonitorInfo { Id = "TEST#1", Friendly = "Test", Number = 1, Scale = 2, Bounds = new RECT(0, 0, 3440, 1440), Work = new RECT(0, 0, 3440, 1344) };
        var emons = new List<MonitorInfo> { emon };
        lib.EnsureSetup(emons);
        using var ewall = Wallpaper(3440, 1440, true, true, 2);
        Glass.Source = (r, f) => Glass.Downscale(ewall, r, f);
        using (var ed = new EditorForm(lib, emon, emons, Monitors.SetupKey(emons), "Test set"))
            {
                Save(ed, Path.Combine(outDir, $"editor-blank{sfx}.png"));
                var m = ed.Model;
                m.Split(0, true, 0.6); m.Split(1, false, 0.5);
                m.SetMode(2, ZoneMode.Grid);
                m.NewMode = ZoneMode.Pixels;
                m.AddZone(0.1, 0.15, 0.35, 0.55);
                m.Name = "Focus + side";
                ed.DemoHover(new Point((int)(0.45 * 1920), (int)(0.78 * 1032)));
                Save(ed, Path.Combine(outDir, $"editor-split{sfx}.png"));
                ed.DemoHover(new Point((int)(0.6 * 1920), (int)(0.8 * 1032)));
                Save(ed, Path.Combine(outDir, $"editor-divider{sfx}.png"));

                // Bottom bar close up: name, Save, ? (2x).
                ed.DemoHover(new Point(5, 5));
                using (var full = Shot(ed))
                using (var c = Crop(full, Rectangle.Inflate(ed.BottomBarArea, 40, 30), 2))
                    c.Save(Path.Combine(outDir, $"editor-bottom{sfx}-2x.png"), ImageFormat.Png);
                // Shortcuts panel opened with ?.
                ed.DemoHelp(true);
                Save(ed, Path.Combine(outDir, $"editor-shortcuts{sfx}.png"));
                using (var full = Shot(ed))
                using (var c = Crop(full, Rectangle.Intersect(Rectangle.Inflate(ed.BottomBarArea, 40, 30), new Rectangle(0, 0, 1920, 1032)), 2))
                    c.Save(Path.Combine(outDir, $"editor-shortcuts{sfx}-2x.png"), ImageFormat.Png);
                ed.DemoHelp(false);
                // Hint under a toolbar button after a short hover.
                ed.DemoTip("tool.split");
                Save(ed, Path.Combine(outDir, $"editor-hint{sfx}.png"));
                ed.DemoOpenPanel();
                Save(ed, Path.Combine(outDir, $"editor-open{sfx}.png"));
            }
        }

        // Icons.
        Theme.ForceMode(null);
        Icons.WriteIco(Path.Combine(outDir, "RegionControl.ico"));
        using (var sheet = IconSheet()) sheet.Save(Path.Combine(outDir, "icons.png"), ImageFormat.Png);

        // Timings on an ultrawide, the slowest case.
        Theme.ForceMode(true, true);
        log.AddRange(Bench(lib));
        Theme.ForceMode(null);
        File.WriteAllLines(Path.Combine(outDir, "timings.txt"), log);
        foreach (var l in log) Console.WriteLine(l);
        Console.WriteLine("rendered to " + outDir);
        return 0;
    }

    static MonitorInfo Mon(int n, int left, int w, int h, int workH) => new()
    {
        Id = $"TEST#{n}", Friendly = n == 1 ? "Test" : "Second", Number = n, Scale = 1,
        Bounds = new RECT(left, 0, left + w, h), Work = new RECT(left, 0, left + w, workH),
    };

    static List<string> Bench(Library lib)
    {
        var res = new List<string> { $"Timings {DateTime.Now:yyyy-MM-dd HH:mm}, 3440x1440 at 100%, hidden windows, median of 7 runs (ms):" };
        var mon = Mon(1, 0, 3440, 1440, 1440);
        var mons = new List<MonitorInfo> { mon };
        lib.EnsureSetup(mons);
        string key = Monitors.SetupKey(mons);
        using var wall = Wallpaper(3440, 1440, true);
        Glass.Source = (r, f) => Glass.Downscale(wall, r, f);
        var layouts = lib.StripFor(mon.Key, key);

        using (var strip = new StripForm())
        {
            var sw = Stopwatch.StartNew();
            strip.Place(mon, layouts, lib.MainFor(key, mon.Key), show: false);
            double place = sw.Elapsed.TotalMilliseconds;
            res.Add($"  strip place (layout, blur of backdrop from the sample image): {place:0.0}");
            res.Add($"  strip paint: {Median(() => strip.TimePaint()):0.0}");
        }
        using (var zones = new ZonesForm())
        {
            _ = zones.Handle; // real window, never shown: UpdateLayeredWindow runs but nothing appears
            zones.ShowLayout(layouts[1], mon.Work, 1, show: false);
            res.Add($"  zones overlay full paint: {Median(() => zones.TimePaint()):0.0}");
            res.Add($"  zones overlay full paint + push: {Median(() => { var s = Stopwatch.StartNew(); zones.Render(); return s.Elapsed.TotalMilliseconds; }):0.0}");
            var z = layouts[1].ZoneRect(1, new RECT(0, 0, 3440, 1440));
            var dirty = new Rectangle(z.Left - 10, z.Top - 10, z.Width + 20, z.Height + 20);
            res.Add($"  zones overlay hover repaint (one zone, {z.Width}x{z.Height}) + push: {Median(() => { var s = Stopwatch.StartNew(); zones.Render(dirty); return s.Elapsed.TotalMilliseconds; }):0.0}");
        }
        using (var ed = new EditorForm(lib, mon, mons, key, "Bench"))
        {
            var m = ed.Model;
            m.Split(0, true, 0.6); m.Split(1, false, 0.5); m.AddZone(0.1, 0.15, 0.35, 0.55);
            using var bmp = new Bitmap(3440, 1440);
            double first = Median(() =>
            {
                m.SetGrid(m.Cols == 12 ? 13 : 12, m.Rows); // forces a new scene
                var s = Stopwatch.StartNew(); ed.DrawToBitmap(bmp, new Rectangle(0, 0, 3440, 1440)); return s.Elapsed.TotalMilliseconds;
            });
            double cached = Median(() => { var s = Stopwatch.StartNew(); ed.DrawToBitmap(bmp, new Rectangle(0, 0, 3440, 1440)); return s.Elapsed.TotalMilliseconds; });
            res.Add($"  editor frame, scene changed (zones moved): {first:0.0}");
            m.SetGrid(m.Cols == 12 ? 13 : 12, m.Rows); ed.DrawToBitmap(bmp, new Rectangle(0, 0, 3440, 1440));
            res.Add($"    split: {ed.LastPaint}");
            ed.DrawToBitmap(bmp, new Rectangle(0, 0, 3440, 1440));
            res.Add($"    cached split: {ed.LastPaint}");
            res.Add($"  editor frame, scene cached (cursor moved): {cached:0.0}");
            int flip = 0;
            double hover = Median(() =>
            {
                ed.DemoHover(flip++ % 2 == 0 ? new Point(400, 1200) : new Point(3000, 300)); // cursor moves from one zone to another
                var s = Stopwatch.StartNew(); ed.DrawToBitmap(bmp, new Rectangle(0, 0, 3440, 1440)); return s.Elapsed.TotalMilliseconds;
            });
            res.Add($"  editor frame, hover moves to another zone: {hover:0.0}");
            res.Add($"    split: {ed.LastPaint}, scene area {ed.LastSceneArea / 1000}k px");
            res.Add("  (editor frames include DrawToBitmap overhead, so the real window is a little faster)");
        }
        return res;
    }

    static double Median(Func<double> f)
    {
        f(); // warm up
        var v = Enumerable.Range(0, 7).Select(_ => f()).OrderBy(x => x).ToList();
        return v[3];
    }

    static Bitmap Shot(Form f)
    {
        var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
        return bmp;
    }

    static void Save(Form f, string path)
    {
        if (f.ClientSize.Width < 2) f.ClientSize = f.Size;
        using var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
        bmp.Save(path, ImageFormat.Png);
    }

    static Bitmap Compose(Bitmap wall, params (Bitmap layer, Point at)[] layers)
    {
        var bmp = new Bitmap(wall.Width, wall.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.DrawImageUnscaled(wall, 0, 0);
        foreach (var (layer, at) in layers) g.DrawImage(layer, new Rectangle(at, layer.Size));
        return bmp;
    }

    static Bitmap Crop(Bitmap src, Rectangle r, int zoom)
    {
        var bmp = new Bitmap(r.Width * zoom, r.Height * zoom);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(src, new Rectangle(0, 0, bmp.Width, bmp.Height), r, GraphicsUnit.Pixel);
        return bmp;
    }

    /// <summary>Sample desktop: soft colour blooms and two app windows, so the glass has something to blur.</summary>
    public static Bitmap Wallpaper(int w, int h, bool dark, bool windows = true, float s = 1)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bg = new LinearGradientBrush(new Rectangle(0, 0, w, h),
                   dark ? Color.FromArgb(10, 16, 40) : Color.FromArgb(150, 186, 236), dark ? Color.FromArgb(28, 12, 52) : Color.FromArgb(214, 196, 240), 35f))
            g.FillRectangle(bg, 0, 0, w, h);
        void Bloom(float cx, float cy, float r, Color c)
        {
            using var p = new GraphicsPath();
            p.AddEllipse(cx * w - r * h, cy * h - r * h, 2 * r * h, 2 * r * h);
            using var b = new PathGradientBrush(p) { CenterColor = c, SurroundColors = new[] { Color.FromArgb(0, c) } };
            g.FillPath(b, p);
        }
        Bloom(0.30f, 0.30f, 0.65f, dark ? Color.FromArgb(200, 40, 110, 255) : Color.FromArgb(200, 70, 140, 255));
        Bloom(0.72f, 0.62f, 0.55f, dark ? Color.FromArgb(170, 160, 60, 220) : Color.FromArgb(170, 200, 120, 255));
        Bloom(0.55f, 0.05f, 0.35f, dark ? Color.FromArgb(150, 30, 200, 220) : Color.FromArgb(150, 120, 230, 240));
        Bloom(0.10f, 0.85f, 0.40f, dark ? Color.FromArgb(140, 250, 120, 80) : Color.FromArgb(140, 255, 170, 120));
        if (!windows) return bmp;
        AppWindow(g, new Rectangle((int)(w * 0.06), (int)(h * 0.04), (int)(w * 0.42), (int)(h * 0.62)), false, s);
        AppWindow(g, new Rectangle((int)(w * 0.52), (int)(h * 0.22), (int)(w * 0.38), (int)(h * 0.58)), true, s);
        return bmp;
    }

    static void AppWindow(Graphics g, Rectangle r, bool dark, float s = 1)
    {
        var back = dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(250, 250, 250);
        var bar = dark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(238, 238, 240);
        var line = dark ? Color.FromArgb(80, 80, 84) : Color.FromArgb(200, 200, 206);
        int S(float v) => (int)Math.Round(v * s);
        using (var p = Theme.Round(r, 8 * s)) using (var b = new SolidBrush(back)) g.FillPath(b, p);
        using (var b = new SolidBrush(bar)) g.FillRectangle(b, r.X + 1, r.Y + 1, r.Width - 2, S(36));
        using (var b = new SolidBrush(Color.FromArgb(0, 120, 212))) g.FillRectangle(b, r.X + S(24), r.Y + S(64), r.Width / 3, S(22));
        using var lb = new SolidBrush(line);
        for (int i = 0; i < 12; i++) g.FillRectangle(lb, r.X + S(24), r.Y + S(110 + i * 26), (int)(r.Width * (0.5 + 0.4 * ((i * 37) % 10) / 10.0)), S(10));
    }

    // ---- README media: banners and screenshots (dark theme, 2x) ----

    static readonly Color BrandA = Color.FromArgb(37, 99, 235);   // same as the app icon
    static readonly Color BrandB = Color.FromArgb(34, 200, 238);

    /// <summary>Writes docs/banner.png, docs/banner-wide.png and docs/media/{picker,editor,tray}.png. Hidden windows only.</summary>
    public static int Media(string docs)
    {
        string media = Path.Combine(docs, "media");
        Directory.CreateDirectory(media);
        string tmp = Path.Combine(Path.GetTempPath(), "RegionControl-media");
        Directory.CreateDirectory(tmp);
        var lib = Library.Load(Path.Combine(tmp, "layouts.json"));
        Theme.ForceMode(true, true);

        using (var b = Banner(1280, 640, false)) b.Save(Path.Combine(docs, "banner.png"), ImageFormat.Png);
        using (var b = Banner(1600, 400, true)) b.Save(Path.Combine(docs, "banner-wide.png"), ImageFormat.Png);

        // A 1280x720 monitor at 200 %: every surface draws at true 2x, no upscaling.
        // Kept at 2560x1440 px because WinForms clamps a form (the editor) to about the size of the real screen.
        var mon = new MonitorInfo { Id = "TEST#1", Friendly = "Test", Number = 1, Scale = 2, Bounds = new RECT(0, 0, 2560, 1440), Work = new RECT(0, 0, 2560, 1344) };
        var mons = new List<MonitorInfo> { mon };
        lib.EnsureSetup(mons);
        string key = Monitors.SetupKey(mons);
        using var wall = Wallpaper(2560, 1440, true, true, 2);
        Glass.Source = (r, f) => Glass.Downscale(wall, r, f);

        // Picker: strip with the Layout pill at the right, card 2 hovered, its zones below with one lit.
        using (var zones = new ZonesForm())
        using (var strip = new StripForm())
        {
            var layouts = lib.StripFor(mon.Key, key);
            strip.Place(mon, layouts, lib.MainFor(key, mon.Key), show: false);
            strip.SetActiveForRender(1, 0);
            zones.ShowLayout(layouts[1], mon.Work, 2, show: false);
            zones.SetHoverForRender(0);
            using var zb = zones.Snapshot();
            using var sb = strip.Snapshot();
            using var pic = Compose(wall, (zb, zones.WindowRect.Location), (sb, strip.WindowRect.Location));
            var sr = strip.WindowRect;
            var crop = new Rectangle(0, 0, wall.Width, Math.Min(wall.Height, sr.Bottom + 560));
            using var c = Crop(pic, crop, 1);
            c.Save(Path.Combine(media, "picker.png"), ImageFormat.Png);
        }

        // Editor: the same "Focus + side" state as editor-split, hint line on the big zone.
        // Wider monitor for the editor (1720x720 at 200 %, ultrawide) so its toolbar stays on one row.
        var emon = new MonitorInfo { Id = "TEST#1", Friendly = "Test", Number = 1, Scale = 2, Bounds = new RECT(0, 0, 3440, 1440), Work = new RECT(0, 0, 3440, 1344) };
        var emons = new List<MonitorInfo> { emon };
        lib.EnsureSetup(emons);
        using var ewall = Wallpaper(3440, 1440, true, true, 2);
        Glass.Source = (r, f) => Glass.Downscale(ewall, r, f);
        using (var ed = new EditorForm(lib, emon, emons, Monitors.SetupKey(emons), "Test set"))
        {
            if (ed.ClientSize.Width < 2) ed.ClientSize = ed.Size;
            var m = ed.Model;
            m.Split(0, true, 0.6); m.Split(1, false, 0.5);
            m.SetMode(2, ZoneMode.Grid);
            m.NewMode = ZoneMode.Pixels;
            m.AddZone(0.1, 0.15, 0.35, 0.55);
            m.Name = "Focus + side";
            ed.DemoHover(new Point((int)(0.45 * 3440), (int)(0.78 * 1344)));
            using var full = Shot(ed);
            full.Save(Path.Combine(media, "editor.png"), ImageFormat.Png);
        }

        // Tray: the menu above the notification area, at 2x.
        using (var t = TrayShot()) t.Save(Path.Combine(media, "tray.png"), ImageFormat.Png);

        Theme.ForceMode(null);
        Console.WriteLine("media rendered to " + docs);
        return 0;
    }

    static Bitmap TrayShot()
    {
        using var menu = new ContextMenuStrip();
        DragWatcher.FillTrayMenu(menu, new List<MonitorInfo> { Mon(1, 0, 1920, 1080, 1032) }, true, null);
        menu.Font = new Font(menu.Font.FontFamily, menu.Font.Size * 2, menu.Font.Style, menu.Font.Unit);
        menu.ImageScalingSize = new Size(32, 32);
        menu.Padding = new Padding(menu.Padding.Left * 2, menu.Padding.Top * 2, menu.Padding.Right * 2, menu.Padding.Bottom * 2);
        foreach (ToolStripItem it in menu.Items)
        {
            it.Padding = new Padding(it.Padding.Left * 2, it.Padding.Top * 2 + 4, it.Padding.Right * 2, it.Padding.Bottom * 2 + 4);
            it.Margin = new Padding(it.Margin.Left * 2, it.Margin.Top * 2, it.Margin.Right * 2, it.Margin.Bottom * 2);
            if (it is ToolStripSeparator sep) sep.AutoSize = false;
            if (it is ToolStripSeparator sep2) sep2.Height = 14;
        }
        var size = menu.GetPreferredSize(Size.Empty);
        menu.Size = size;
        using var mb = new Bitmap(size.Width, size.Height);
        menu.DrawToBitmap(mb, new Rectangle(Point.Empty, size));

        // Scene: corner of the sample desktop, a taskbar 96 px tall, the tray icon lit, the menu above it.
        int w = size.Width + 520, h = size.Height + 96 + 140;
        using var wall = Wallpaper(3840, 2160, true, false, 2);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawImage(wall, new Rectangle(0, 0, w, h), new Rectangle(3840 - w, 2160 - h, w, h), GraphicsUnit.Pixel);
        var bar = new Rectangle(0, h - 96, w, 96);
        using (var b = new SolidBrush(Color.FromArgb(236, 28, 28, 30))) g.FillRectangle(b, bar);
        using (var p = new Pen(Color.FromArgb(40, 255, 255, 255), 2)) g.DrawLine(p, 0, bar.Top, w, bar.Top);
        using var clockFont = Theme.UiFont(24);
        using var tb = new SolidBrush(Color.FromArgb(235, 255, 255, 255));
        var fmt = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.DrawString("14:05\n2026-11-09", clockFont, tb, new RectangleF(w - 220, bar.Top, 196, 96), fmt);
        // Tray icon button, lit as when its menu is open.
        int ix = w - 330;
        var btn = new RectangleF(ix - 12, bar.Top + 14, 72, 68);
        using (var b = new SolidBrush(Color.FromArgb(28, 255, 255, 255))) Glass.FillRound(g, b, btn, 8);
        using (var ic = Icons.RenderApp(48)) g.DrawImage(ic, ix, bar.Top + 24, 48, 48);
        // Two plain tray glyphs to the left, drawn as simple shapes.
        using (var p = new Pen(Color.FromArgb(200, 255, 255, 255), 3))
        {
            g.DrawArc(p, ix - 110, bar.Top + 34, 36, 36, 200, 140);
            g.DrawArc(p, ix - 102, bar.Top + 44, 20, 20, 200, 140);
            g.DrawRectangle(p, ix - 190, bar.Top + 38, 40, 22);
            g.FillRectangle(Brushes.White, ix - 150, bar.Top + 44, 5, 10);
        }
        // Menu with a soft shadow, its right edge over the tray icon.
        var mr = new RectangleF(ix + 60 - size.Width, bar.Top - 16 - size.Height, size.Width, size.Height);
        Glass.Shadow(g, mr, 16, 40, 12);
        g.DrawImage(mb, mr.X, mr.Y, size.Width, size.Height);
        return bmp;
    }

    /// <summary>Social preview (stacked) or README header (wide): glass, icon, name, tagline and a strip of uneven layouts.</summary>
    static Bitmap Banner(int w, int h, bool wide)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using var back = Wallpaper(w, h, true, false);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.DrawImageUnscaled(back, 0, 0);
        Glass.Source = (r, f) => Glass.Downscale(back, r, f);

        // Title block.
        int icon = wide ? 132 : 124;
        using var nameFont = Theme.DisplayFont(wide ? 70 : 76, true);
        using var tagFont = Theme.UiFont(wide ? 28 : 30);
        var nameSz = g.MeasureString("Region Control", nameFont, PointF.Empty, StringFormat.GenericTypographic);
        var tagSz = g.MeasureString("Custom snap layouts for Windows 11", tagFont, PointF.Empty, StringFormat.GenericTypographic);
        float gap = 30;
        float rowW = icon + gap + nameSz.Width;
        float bx, by;
        if (wide) { bx = 72; by = (h - icon) / 2f - 26; }
        else { bx = (w - rowW) / 2f; by = 70; }
        using (var ic = Icons.RenderApp(icon)) g.DrawImage(ic, bx, by, icon, icon);
        using var white = new SolidBrush(Theme.Text);
        using var dim = new SolidBrush(Theme.A(Theme.Text, 0.72));
        float nx = bx + icon + gap;
        g.DrawString("Region Control", nameFont, white, nx, by + (icon - nameSz.Height) / 2f - 4, StringFormat.GenericTypographic);
        if (wide) g.DrawString("Custom snap layouts for Windows 11", tagFont, dim, bx + 6, by + icon + 26, StringFormat.GenericTypographic);
        else g.DrawString("Custom snap layouts for Windows 11", tagFont, dim, (w - tagSz.Width) / 2f, by + icon + 28, StringFormat.GenericTypographic);

        // Strip: a glass panel holding three cards, each a mini monitor with uneven zones. The middle card is chosen.
        RectangleF panel = wide ? new RectangleF(w - 72 - 760, 62, 760, h - 124) : new RectangleF(100, 330, w - 200, 250);
        Glass.Shadow(g, panel, 22, 36, 14);
        using (var blur = Glass.Backdrop(Rectangle.Round(panel), 24))
            Glass.Panel(g, panel, 22, blur, panel, 1);

        var layouts = new[]
        {
            ("Focus + side", new[] { new RectangleF(0, 0, 0.62f, 1), new RectangleF(0.62f, 0, 0.38f, 0.5f), new RectangleF(0.62f, 0.5f, 0.38f, 0.5f) }, -1),
            ("Code, docs, chat", new[] { new RectangleF(0, 0, 0.22f, 1), new RectangleF(0.22f, 0, 0.5f, 1), new RectangleF(0.72f, 0, 0.28f, 0.6f), new RectangleF(0.72f, 0.6f, 0.28f, 0.4f) }, 1),
            ("Top wide", new[] { new RectangleF(0, 0, 0.36f, 0.58f), new RectangleF(0.36f, 0, 0.64f, 0.58f), new RectangleF(0, 0.58f, 1, 0.42f) }, -1),
        };
        float pad = 24, cg = 20, labelH = 38;
        float cw = (panel.Width - 2 * pad - 2 * cg) / 3f;
        float ch = panel.Height - 2 * pad;
        using var labelFont = Theme.UiFont(19);
        for (int i = 0; i < 3; i++)
        {
            var (name, zs, hot) = layouts[i];
            var card = new RectangleF(panel.X + pad + i * (cw + cg), panel.Y + pad, cw, ch);
            bool chosen = hot >= 0;
            if (chosen)
            {
                for (int k = 4; k >= 1; k--)
                    using (var gb = new SolidBrush(Color.FromArgb(14, BrandB))) Glass.FillRound(g, gb, RectangleF.Inflate(card, k * 3, k * 3), 14 + k * 3);
            }
            using (var cb = new SolidBrush(chosen ? Theme.CardHot : Theme.Card)) Glass.FillRound(g, cb, card, 14);
            if (chosen)
            {
                using var ring = new Pen(BrandB, 2.5f);
                using var rp = Theme.Round(RectangleF.Inflate(card, -1, -1), 13);
                g.DrawPath(ring, rp);
            }
            // Mini monitor: 16:9 inside the card, above the label.
            float mw = card.Width - 28, mh = card.Height - 28 - labelH;
            if (mw / mh > 16f / 9) mw = mh * 16 / 9; else mh = mw * 9 / 16;
            var screen = new RectangleF(card.X + (card.Width - mw) / 2, card.Y + 14 + (card.Height - 28 - labelH - mh) / 2, mw, mh);
            float zg = 5;
            for (int z = 0; z < zs.Length; z++)
            {
                var f = zs[z];
                var zr = new RectangleF(screen.X + f.X * screen.Width + zg / 2, screen.Y + f.Y * screen.Height + zg / 2, f.Width * screen.Width - zg, f.Height * screen.Height - zg);
                bool lit = z == hot;
                using var zp = Theme.Round(zr, 6);
                if (lit)
                {
                    using var lb = new LinearGradientBrush(zr, BrandA, BrandB, LinearGradientMode.ForwardDiagonal);
                    g.FillPath(lb, zp);
                    using var sheen = new LinearGradientBrush(new RectangleF(zr.X, zr.Y - 1, zr.Width, zr.Height + 2), Color.FromArgb(56, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical);
                    g.FillPath(sheen, zp);
                    using var e = new Pen(Color.FromArgb(150, 255, 255, 255), 1.2f);
                    g.DrawPath(e, zp);
                }
                else
                {
                    using var zb = new SolidBrush(Theme.MiniZone);
                    g.FillPath(zb, zp);
                    using var e = new Pen(Theme.MiniZoneEdge, 1f);
                    g.DrawPath(e, zp);
                }
            }
            using var lbBrush = new SolidBrush(chosen ? Theme.Text : Theme.A(Theme.Text, 0.7));
            var lsz = g.MeasureString(name, labelFont, PointF.Empty, StringFormat.GenericTypographic);
            g.DrawString(name, labelFont, lbBrush, card.X + (card.Width - lsz.Width) / 2, card.Bottom - labelH + (labelH - lsz.Height) / 2 - 8, StringFormat.GenericTypographic);
        }
        return bmp;
    }

    static Bitmap IconSheet()
    {
        var sizes = Icons.IcoSizes;
        int w = 40 + sizes.Sum(s => s + 24), h = 2 * (256 + 60) + 120;
        var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe UI", 12, GraphicsUnit.Pixel);
        for (int row = 0; row < 2; row++)
        {
            var back = row == 0 ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243);
            using (var b = new SolidBrush(back)) g.FillRectangle(b, 0, row * 316, w, 316);
            int x = 20;
            foreach (int s in sizes)
            {
                using var ic = Icons.RenderApp(s);
                g.DrawImageUnscaled(ic, x, row * 316 + 20 + (256 - s));
                using var tb = new SolidBrush(row == 0 ? Color.Gainsboro : Color.DimGray);
                g.DrawString($"{s}", font, tb, x, row * 316 + 286);
                x += s + 24;
            }
        }
        // Taskbar mock-ups: tray icon at 16, 20, 24 and 32 px (100 %, 125 %, 150 %, 200 %).
        int ty = 2 * 316;
        foreach (var (back, y) in new[] { (Color.FromArgb(28, 28, 28), ty), (Color.FromArgb(238, 238, 238), ty + 60) })
        {
            using (var b = new SolidBrush(back)) g.FillRectangle(b, 0, y, w, 60);
            int x = 20;
            foreach (int s in new[] { 16, 20, 24, 32 })
            {
                using var ic = Icons.RenderApp(s);
                g.DrawImageUnscaled(ic, x, y + (60 - s) / 2);
                x += s + 30;
            }
        }
        return bmp;
    }
}
