namespace RegionControl;

/// <summary>
/// Unit-style checks for the logic that cannot be tested by dragging: file format, library, zone modes,
/// editor operations, monitor setups and sets. Run: RegionControl.exe --selftest (or src\test.cmd).
/// Uses a temp folder; never touches %APPDATA%.
/// </summary>
public static class SelfTest
{
    static int _pass, _fail;

    static void Check(bool ok, string what)
    {
        if (ok) _pass++; else { _fail++; Console.WriteLine("FAIL  " + what); }
    }

    static void Eq<T>(T actual, T expected, string what) =>
        Check(Equals(actual, expected), $"{what}: expected {expected}, got {actual}");

    static bool Same(RECT a, RECT b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
    static void Rect(RECT actual, RECT expected, string what) => Check(Same(actual, expected), $"{what}: expected {expected}, got {actual}");

    /// <summary>Shared-border resize geometry: who joins the dragged line, and where their edge goes.</summary>
    static void Borders()
    {
        // Left stack (top, bottom) and one right window: x line at 1000.
        var leftTop = new RECT(0, 0, 1000, 500);
        var leftBottom = new RECT(0, 500, 1000, 1000);
        var right = new RECT(1000, 0, 2000, 1000);
        var far = new RECT(3000, 0, 4000, 1000);
        var others = new List<RECT> { leftBottom, right, far };

        // Drag leftTop's right edge: the other left window and the right window both sit on the line.
        var m = BorderMath.Members(true, 1000, leftTop, others, 14, 20);
        Eq(string.Join(",", m), "0,1", "right edge drag: stacked neighbour and right window join, far window does not");

        // A window beside the line but with no vertical overlap does not join.
        var below = new RECT(1000, 1100, 2000, 1500);
        Eq(BorderMath.Members(true, 1000, leftTop, new List<RECT> { below }, 14, 20).Count, 0, "no overlap along the line: not a member");

        // A window that connects only through another member joins (chain along the line).
        var chain = new RECT(1000, 960, 2000, 1500);
        Eq(BorderMath.Members(true, 1000, leftTop, new List<RECT> { right, chain }, 14, 20).Count, 2, "chain along the line joins");

        // A small gap (up to the tolerance) still counts as the same border and is kept.
        var gapRight = new RECT(1008, 0, 2000, 1000);
        Eq(BorderMath.Members(true, 1000, leftTop, new List<RECT> { gapRight }, 14, 20).Count, 1, "8 px gap is within the tolerance");
        Rect(BorderMath.Move(gapRight, true, 1000, 100, 160, 14), new RECT(1108, 0, 2000, 1000), "moving the line keeps the 8 px gap");

        // Move: left edge of the right window and right edge of the left window follow the line.
        Rect(BorderMath.Move(right, true, 1000, -200, 160, 14), new RECT(800, 0, 2000, 1000), "line 200 px left: right window grows left");
        Rect(BorderMath.Move(leftBottom, true, 1000, -200, 160, 14), new RECT(0, 500, 800, 1000), "line 200 px left: left window shrinks");
        Rect(BorderMath.Move(right, true, 1000, 900, 160, 14), new RECT(1840, 0, 2000, 1000), "minimum width stops the squeezed window");
        Rect(BorderMath.Move(far, true, 1000, 50, 160, 14), far, "a window off the line does not move");

        // Horizontal line: top window stack above a bottom window.
        var top = new RECT(0, 0, 1000, 600);
        var bottom = new RECT(0, 600, 1000, 1000);
        Eq(BorderMath.Members(false, 600, top, new List<RECT> { bottom }, 14, 20).Count, 1, "bottom edge drag: window below joins");
        Rect(BorderMath.Move(bottom, false, 600, 100, 160, 14), new RECT(0, 700, 1000, 1000), "line 100 px down: lower window shrinks");
        Rect(BorderMath.Move(bottom, false, 600, 500, 160, 14), new RECT(0, 840, 1000, 1000), "minimum height stops the squeezed window");
    }

    static MonitorInfo Mon(string id, int left, int w, int h, double scale = 1, bool isInternal = false, int taskbar = 48) => new()
    {
        Id = id, Friendly = isInternal ? "" : id, Internal = isInternal, Scale = scale,
        Bounds = new RECT(left, 0, left + w, h), Work = new RECT(left, 0, left + w, h - taskbar),
    };

    public static int Run()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RegionControlSelfTest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            Format(dir);
            ZoneModes();
            Editor();
            Setups(dir);
            Borders();
        }
        catch (Exception ex) { _fail++; Console.WriteLine("FAIL  exception: " + ex); }
        finally { try { Directory.Delete(dir, true); } catch { } }
        Console.WriteLine($"{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    // ---------- file format, library ----------

    static void Format(string dir)
    {
        string path = Path.Combine(dir, "a", "layouts.json");
        var lib = Library.Load(path);
        Check(File.Exists(path), "missing file is created");
        Eq(lib.Data.Layouts.Count, 3, "seed layout count");
        Eq(lib.Data.Layouts[0].Id, "seed-70-30", "first seed id");
        var work = new RECT(0, 0, 2560, 1392);
        Rect(lib.Data.Layouts[0].ZoneRect(0, work), new RECT(0, 0, 1792, 1392), "seed 70|30 left on 2560 wide");
        Rect(lib.Data.Layouts[0].ZoneRect(1, work), new RECT(1792, 0, 2560, 1392), "seed 70|30 right");
        Rect(lib.Data.Layouts[2].ZoneRect(0, work), new RECT(896, 278, 1664, 1114), "seed floating 30% centred");

        string json = File.ReadAllText(path);
        Check(json.Contains("\"mode\": \"percent\""), "modes are written as words");
        Check(json.Contains("\"layouts\"") && json.Contains("\"sets\"") && json.Contains("\"monitors\""), "top-level keys");

        // Round trip with all three modes.
        lib.Upsert(new ZoneLayout
        {
            Id = "t1", Name = "Mixed", Grid = new GridSize { Cols = 6, Rows = 4 },
            Zones = {
                new Zone { Name = "p", Mode = ZoneMode.Percent, X = 0, Y = 0, W = 50, H = 100 },
                new Zone { Name = "x", Mode = ZoneMode.Pixels, X = 100, Y = 50, W = 800, H = 600 },
                new Zone { Name = "g", Mode = ZoneMode.Grid, X = 3, Y = 0, W = 3, H = 2 } },
        });
        lib.Save();
        Check(File.Exists(path + ".bak"), "save keeps a .bak of the previous file");
        var again = Library.Load(path);
        var t1 = again.Find("t1");
        Check(t1 != null, "round trip keeps the layout");
        Eq(t1?.Zones[1].Mode, ZoneMode.Pixels, "round trip keeps pixel mode");
        Eq(t1?.Grid.Cols, 6, "round trip keeps grid cols");
        Eq(t1?.Zones[2].W, 3.0, "round trip keeps grid width");

        // A broken file is moved aside and the seeds come back.
        File.WriteAllText(path, "{ not json");
        var broken = Library.Load(path);
        Eq(broken.Data.Layouts.Count, 3, "broken file: seeds loaded");
        Check(Directory.GetFiles(Path.GetDirectoryName(path)!, "layouts.bad-*.json").Length == 1, "broken file moved aside");
        Check(broken.LoadNote?.Contains("could not read") == true, "broken file note");

        // Hand-edit mistakes are fixed: duplicate id, zero-size zone, bad grid, reference to a missing layout.
        File.WriteAllText(path, """
        { "version": 1,
          "layouts": [
            { "id": "a", "name": "A", "grid": { "cols": 0, "rows": 8 }, "zones": [ { "mode": "percent", "x": 0, "y": 0, "w": 50, "h": 100 }, { "x": 0, "y": 0, "w": 0, "h": 10 } ] },
            { "id": "a", "name": "A2", "zones": [ { "mode": "grid", "x": 0, "y": 0, "w": 1, "h": 1 } ] }
          ],
          "monitors": [ { "key": "M@1x1", "layouts": [ "a", "gone" ] } ],
          "sets": [ { "key": "M@1x1", "main": { "M@1x1": "gone" } } ] }
        """);
        var fixedLib = Library.Load(path);
        Eq(fixedLib.Data.Layouts.Count, 2, "hand edit: both layouts kept");
        Check(fixedLib.Data.Layouts[0].Id != fixedLib.Data.Layouts[1].Id, "hand edit: duplicate id renamed");
        Eq(fixedLib.Data.Layouts[0].Zones.Count, 1, "hand edit: zero-size zone dropped");
        Eq(fixedLib.Data.Layouts[0].Grid.Cols, 12, "hand edit: bad grid reset");
        Eq(fixedLib.Data.Monitors[0].Layouts.Count, 1, "hand edit: missing layout dropped from strip");
        Eq(fixedLib.Data.Sets[0].Main.Count, 0, "hand edit: missing main dropped");

        // Old file: a main layout that is not in its monitor's strip list must not vanish.
        File.WriteAllText(path, """
        { "version": 1,
          "layouts": [ { "id": "a", "name": "A", "zones": [ { "x": 0, "y": 0, "w": 50, "h": 100 } ] },
                       { "id": "b", "name": "B", "zones": [ { "x": 50, "y": 0, "w": 50, "h": 100 } ] } ],
          "monitors": [ { "key": "M@1x1", "layouts": [ "b" ] } ],
          "sets": [ { "key": "M@1x1", "main": { "M@1x1": "a" } } ] }
        """);
        var oldLib = Library.Load(path);
        Eq(string.Join(",", oldLib.StripFor("M@1x1", "M@1x1").Select(l => l.Id)), "a,b", "old main layout stays in the strip, in its old place");
        Eq(oldLib.Data.Sets[0].Main.Count, 1, "old main field is kept in the file");
    }

    // ---------- zone modes, mixed DPI, ultrawide ----------

    static void ZoneModes()
    {
        var grid = new GridSize { Cols = 12, Rows = 8 };
        var ultra = new RECT(0, 0, 3440, 1392);
        Rect(new Zone { X = 25, Y = 0, W = 50, H = 100 }.ToScreen(ultra, grid), new RECT(860, 0, 2580, 1392), "percent on ultrawide");

        var laptop = new RECT(0, 0, 1366, 720);
        Rect(new Zone { Mode = ZoneMode.Pixels, X = 1000, Y = 100, W = 1600, H = 600 }.ToScreen(laptop, grid), new RECT(0, 100, 1366, 700), "pixels wider than the monitor: clamped and shifted");
        Rect(new Zone { Mode = ZoneMode.Pixels, X = 1000, Y = 300, W = 800, H = 600 }.ToScreen(laptop, grid), new RECT(566, 120, 1366, 720), "pixels past the edge: shifted inside");
        Rect(new Zone { Mode = ZoneMode.Pixels, X = 10, Y = 20, W = 300, H = 200 }.ToScreen(new RECT(-1920, 0, 0, 1032), grid), new RECT(-1910, 20, -1610, 220), "pixels on a monitor left of the primary");

        var fhd = new RECT(0, 0, 1920, 1032);
        Rect(new Zone { Mode = ZoneMode.Grid, X = 3, Y = 2, W = 6, H = 4 }.ToScreen(fhd, grid), new RECT(480, 258, 1440, 774), "grid 12x8 cells");
        Rect(new Zone { X = 0, Y = 0, W = 120, H = 100 }.ToScreen(fhd, grid), new RECT(0, 0, 1920, 1032), "percent over 100 is clamped");

        // Mixed DPI: a 4K monitor at 150% and a 1080p one at 100%. Zones are physical pixels on each.
        var layout = new ZoneLayout { Zones = { new Zone { X = 0, Y = 0, W = 50, H = 100 }, new Zone { Mode = ZoneMode.Pixels, X = 0, Y = 0, W = 1200, H = 800 } } };
        var k4 = new RECT(1920, 0, 1920 + 3840, 2088);
        Rect(layout.ZoneRect(0, k4), new RECT(1920, 0, 1920 + 1920, 2088), "percent on 4K at 150%");
        Rect(layout.ZoneRect(1, k4), new RECT(1920, 0, 3120, 800), "pixels on 4K: physical size");
        Rect(layout.ZoneRect(1, fhd), new RECT(0, 0, 1200, 800), "same pixel zone on 1080p: same physical size");

        // Overlap: the smaller zone wins the hit test.
        var over = new ZoneLayout { Zones = { new Zone { X = 0, Y = 0, W = 100, H = 100 }, new Zone { X = 40, Y = 40, W = 20, H = 20 } } };
        Eq(over.HitTest(fhd, new POINT { X = 960, Y = 516 }), 1, "overlap: small floating zone wins");
        Eq(over.HitTest(fhd, new POINT { X = 10, Y = 10 }), 0, "overlap: big zone outside the small one");
        Eq(over.HitTest(fhd, new POINT { X = 5000, Y = 10 }), -1, "hit test outside the monitor");
        Eq(over.PaintOrder(fhd)[0], 0, "paint order: big zone first");
    }

    // ---------- split, draw, start from saved ----------

    static void Editor()
    {
        var m = new EditorModel(2560, 1392) { MinPx = 32, MagnetPx = 10 };
        Eq(m.Zones.Count, 1, "editor starts with one zone");
        Check(!m.Dirty, "new editor is clean");
        Check(m.DefaultVertical(0), "wide zone splits with a vertical line by default");

        Check(m.Split(0, true, 0.7), "split at 70%");
        Eq(m.Zones.Count, 2, "two zones after split");
        Check(m.Dirty, "split makes it dirty");
        var l = m.ToLayout();
        Eq(l.Zones[0].W, 70.0, "left part is 70%");
        Eq(l.Zones[1].X, 70.0, "right part starts at 70%");
        Eq(l.Zones[1].W, 30.0, "right part is 30%");

        Check(m.Split(1, false, 0.5), "split the right part across");
        Eq(m.Zones.Count, 3, "three zones");

        var d = m.FindDivider(0.7, 0.25, true, 6);
        Check(d != null, "finds the vertical divider");
        Eq(d!.Before.Count + d.After.Count, 3, "divider at 70% joins the left zone and both right zones");
        m.Push();
        m.MoveDivider(d, m.SnapDivider(d, 0.6, true));
        Check(Math.Abs(m.Zones[0].R - 0.6) < 1e-9 && Math.Abs(m.Zones[1].L - 0.6) < 1e-9 && Math.Abs(m.Zones[2].L - 0.6) < 1e-9, "dragging the divider moves all three edges");
        m.PopIfUnchanged();
        Check(m.CanUndo, "divider drag is one undo step");

        m.Push();
        m.MoveDivider(d, 1.0);
        Check(m.Zones[1].W * 2560 >= 32 - 1e-6, "divider clamp keeps the minimum zone size");
        m.Undo();
        Check(Math.Abs(m.Zones[0].R - 0.6) < 1e-9, "undo restores the divider");

        var h = m.FindDivider(0.8, 0.5, false, 6);
        Check(h != null && h.Before.Count == 1 && h.After.Count == 1, "horizontal divider on the right joins two zones");
        Check(m.Merge(h!, 0.8, out _), "merge the right pair");
        Eq(m.Zones.Count, 2, "two zones after merge");
        m.Undo();
        Eq(m.Zones.Count, 3, "undo merge");
        m.Redo();
        Eq(m.Zones.Count, 2, "redo merge");
        m.Undo();

        // Lines that only touch end to end stay separate unless Ctrl joins them.
        var c = new EditorModel(2000, 1000) { MinPx = 32 };
        c.Split(0, true, 0.5); c.Split(0, false, 0.5); c.Split(2, false, 0.5);
        Eq(c.Zones.Count, 4, "four quadrants");
        var leftLine = c.FindDivider(0.25, 0.5, false, 6);
        Eq(leftLine!.Before.Count + leftLine.After.Count, 2, "left half-line moves alone");
        var joined = c.FindDivider(0.25, 0.5, false, 6, joinTouching: true);
        Eq(joined!.Before.Count + joined.After.Count, 4, "Ctrl joins the lined-up half-lines");
        var misaligned = c.FindDivider(0.5, 0.25, true, 6);
        Check(misaligned != null, "vertical divider found");

        // Merge refuses zones that do not line up.
        var mm = new EditorModel(2000, 1000) { MinPx = 32 };
        mm.Split(0, true, 0.5); mm.Split(1, false, 0.5);
        var v = mm.FindDivider(0.5, 0.25, true, 6)!;
        Check(!mm.Merge(v, 0.25, out var why) && why.Length > 0, "merge refuses zones that do not line up");

        // Grid snapping.
        var g = new EditorModel(1920, 1080) { MinPx = 32 };
        g.SetMode(0, ZoneMode.Grid);
        Eq(g.SnapSplit(0, true, 0.43, true), 5.0 / 12, "grid split snaps to 5/12");
        g.Split(0, true, g.SnapSplit(0, true, 0.43, true));
        var gl = g.ToLayout();
        Eq(gl.Zones[0].Mode, ZoneMode.Grid, "split keeps grid mode");
        Eq(gl.Zones[0].W, 5.0, "grid zone is 5 cells wide");
        g.SetGrid(6, 8);
        Check(Math.Abs(g.Zones[0].R * 6 - Math.Round(g.Zones[0].R * 6)) < 1e-9, "changing the grid re-snaps grid zones");

        // Magnetic snap to another zone's edge, and Alt (no magnet) for free placement.
        var s = new EditorModel(2000, 1000) { MinPx = 32, MagnetPx = 10 };
        s.Split(0, true, 0.3);
        var snapped = s.SnapRect(0.3022, 0.2, 0.6, 0.6, true);
        Eq(Math.Round(snapped.l, 6), 0.3, "drawn edge snaps to a nearby zone edge");
        var free = s.SnapRect(0.3022, 0.2, 0.6, 0.6, false);
        Eq(Math.Round(free.l * 2000), 604.0, "no magnet: whole pixel only");
        int drawn = s.AddZone(0.302, 0.2, 0.6, 0.6);
        Eq(drawn, 2, "drawn zone is added on top");
        Eq(s.HitZone(0.4, 0.4), 2, "drawn floating zone wins the hit test");
        Eq(s.AddZone(0.1, 0.1, 0.105, 0.5), -1, "too small a drawn zone is refused");

        // Move keeps the zone on screen.
        var start = s.Zones[2].Clone();
        s.Push();
        s.MoveZone(2, start, 0.9, 0.9, false);
        Check(s.Zones[2].R <= 1 + 1e-9 && s.Zones[2].B <= 1 + 1e-9, "move clamps the zone on screen");
        Check(Math.Abs(s.Zones[2].W - start.W) < 1e-9, "move keeps the size");

        // Pixel zones save as whole pixels; minimum size refuses tiny splits.
        var p = new EditorModel(1366, 720) { MinPx = 32, NewMode = ZoneMode.Pixels };
        p.NewBlank();
        Eq(p.Zones.Count, 0, "blank layout has zero zones");
        Eq(p.HitZone(0.5, 0.5), -1, "blank: hit test finds nothing");
        Check(p.FindDivider(0.5, 0.5, true, 6) == null, "blank: no divider");
        p.Delete(0); p.Duplicate(0); p.SetMode(0, ZoneMode.Grid);
        Check(!p.Split(0, true, 0.5), "blank: split does nothing");
        Check(p.ToLayout().Zones.Count == 0, "blank: converts to a layout with no zones");
        _ = p.Dirty; _ = p.Signature();
        Check(p.Undo() && p.Zones.Count == 1, "blank: undo goes back");
        Check(p.Redo() && p.Zones.Count == 0, "blank: redo returns to empty");
        Check(p.AddZone(0, 0, 1, 1) == 0, "blank: draw adds the first zone");
        Eq(p.Zones.Count, 1, "blank: one zone after drawing");
        p.Split(0, true, 683.0 / 1366);
        var pl = p.ToLayout();
        Eq(pl.Zones[0].Mode, ZoneMode.Pixels, "pixel mode saved");
        Eq(pl.Zones[0].W, 683.0, "pixel zone width");
        Check(!p.Split(0, true, 10.0 / 1366), "refuses a split below the minimum size");

        // Start from saved: load, edit, compare.
        var saved = Library.Seeds()[1];
        var f = new EditorModel(1920, 1032);
        f.Load(saved, undoable: false);
        Check(!f.Dirty, "loaded layout is clean");
        Eq(f.LayoutId, "seed-25-50-25", "loaded layout keeps its id");
        var back = f.ToLayout();
        Check(back.Zones.Select(z => (z.X, z.W)).SequenceEqual(saved.Zones.Select(z => (z.X, z.W))), "load then save gives the same percent values");
        f.Delete(1);
        Check(f.Dirty, "delete makes it dirty");
        f.Undo();
        Check(!f.Dirty, "undo back to the saved state is clean");

        // Cancel a drag.
        var k = new EditorModel(1000, 1000) { MinPx = 32 };
        k.Split(0, true, 0.5);
        var dv = k.FindDivider(0.5, 0.5, true, 6)!;
        k.Push(); k.MoveDivider(dv, 0.3); k.CancelDrag();
        Check(Math.Abs(k.Zones[0].R - 0.5) < 1e-9, "Esc during a drag restores the start");
    }

    // ---------- per-monitor layouts, sets ----------

    static void Setups(string dir)
    {
        var lib = Library.Load(Path.Combine(dir, "b", "layouts.json"));
        var laptop = Mon("BOE0A1B#4&1&UID1", 0, 1920, 1200, 1.5, isInternal: true);
        var dellA = Mon("DELA0C3#5&2&UID2", 1920, 2560, 1440);
        var dellB = Mon("DELA0C3#5&3&UID3", 4480, 2560, 1440);
        var ultra = Mon("GSM5BBF#5&4&UID4", 1920, 3440, 1440, 1.25);

        // Laptop alone.
        var alone = new List<MonitorInfo> { laptop };
        Monitors.Number(alone);
        Check(lib.EnsureSetup(alone), "new setup is added");
        Check(!lib.EnsureSetup(alone), "known setup adds nothing");
        string kAlone = Monitors.SetupKey(alone);
        Eq(lib.Set(kAlone)?.Name, "Laptop alone", "auto set name for the laptop");
        Eq(lib.StripFor(laptop.Key, kAlone).Count, 3, "new monitor gets the seed layouts");
        Eq(lib.StripFor(laptop.Key, kAlone)[0].Id, "seed-70-30", "strip starts in seed order");

        // Desk: laptop plus two identical external monitors.
        var desk = new List<MonitorInfo> { dellB, laptop, dellA };
        Monitors.Number(desk);
        Eq(desk[0].Id, laptop.Id, "numbering is left to right");
        string kDesk = Monitors.SetupKey(desk);
        Eq(kDesk, Monitors.SetupKey(new[] { dellA, dellB, laptop }), "setup key ignores order");
        Check(kDesk != kAlone, "desk and laptop alone are different setups");
        Check(dellA.Key != dellB.Key, "identical monitor models get different keys");
        lib.EnsureSetup(desk);
        Eq(lib.Data.Sets.Count, 2, "two sets");
        Check(lib.Set(kDesk)!.Name.StartsWith("3 monitors"), "auto set name for the desk");

        // A custom layout saved on the laptop shows only there until shared.
        var custom = new ZoneLayout { Id = "c1", Name = "Custom", Zones = { new Zone { X = 0, Y = 0, W = 60, H = 100 }, new Zone { X = 60, Y = 0, W = 40, H = 50 } } };
        lib.Upsert(custom);
        lib.SetShown("c1", laptop.Key, true);
        Check(lib.StripFor(laptop.Key, kDesk).Any(x => x.Id == "c1"), "custom layout on the laptop strip");
        Check(!lib.StripFor(dellA.Key, kDesk).Any(x => x.Id == "c1"), "not on monitor A before sharing");
        lib.SetShown("c1", dellA.Key, true);
        Check(lib.StripFor(dellA.Key, kDesk).Any(x => x.Id == "c1"), "shared to monitor A");
        Check(!lib.StripFor(dellB.Key, kDesk).Any(x => x.Id == "c1"), "still not on monitor B");

        // Strip order: SetStripOrder writes exactly the ids shown. No pinned layout.
        lib.SetShown("c1", laptop.Key, false);
        lib.SetStripOrder(laptop.Key, new[] { "c1", "seed-70-30", "seed-25-50-25", "seed-floating-30" });
        var stripAtStart = lib.Monitor(laptop.Key)!.Layouts.ToList();
        string Order() => string.Join(",", lib.StripFor(laptop.Key, kDesk).Select(x => x.Id));
        // Same helper the editor uses: take the shown ids, remove the card, insert it at a final index.
        void Put(string id, int index) { var ids = lib.StripFor(laptop.Key, kDesk).Select(x => x.Id).ToList(); ids.Remove(id); ids.Insert(Math.Clamp(index, 0, ids.Count), id); lib.SetStripOrder(laptop.Key, ids); }
        Eq(Order(), "c1,seed-70-30,seed-25-50-25,seed-floating-30", "strip order is the list order");
        Put("seed-floating-30", 0);
        Eq(Order(), "seed-floating-30,c1,seed-70-30,seed-25-50-25", "any card can land 1st, even before the old main");
        Put("seed-25-50-25", 1);
        Eq(Order(), "seed-floating-30,seed-25-50-25,c1,seed-70-30", "any card can land 2nd");
        Put("seed-floating-30", 2);
        Eq(Order(), "seed-25-50-25,c1,seed-floating-30,seed-70-30", "2nd-to-last, moving later");
        Put("seed-25-50-25", 2);
        Eq(Order(), "c1,seed-floating-30,seed-25-50-25,seed-70-30", "2nd-to-last, moving later (first card)");
        Put("seed-70-30", 2);
        Eq(Order(), "c1,seed-floating-30,seed-70-30,seed-25-50-25", "2nd-to-last, moving earlier");
        Put("c1", 99);
        Eq(Order(), "seed-floating-30,seed-70-30,seed-25-50-25,c1", "any card can land last");
        Put("c1", 0);
        Eq(Order(), "c1,seed-floating-30,seed-70-30,seed-25-50-25", "last card can land first");
        Check(!lib.SetStripOrder(laptop.Key, new[] { "c1", "seed-floating-30", "seed-70-30", "seed-25-50-25" }), "same order reports no change");
        // From outside the strip: a card that is not on this monitor yet.
        lib.SetStripOrder(dellB.Key, new[] { "seed-70-30", "seed-25-50-25", "seed-floating-30" });
        Check(!lib.IsShown("c1", dellB.Key), "c1 starts outside the strip of monitor B");
        string OrderB() => string.Join(",", lib.StripFor(dellB.Key, kDesk).Select(x => x.Id));
        void PutB(string id, int index) { var ids = lib.StripFor(dellB.Key, kDesk).Select(x => x.Id).ToList(); ids.Remove(id); ids.Insert(Math.Clamp(index, 0, ids.Count), id); lib.SetStripOrder(dellB.Key, ids); }
        PutB("c1", 0); Eq(OrderB(), "c1,seed-70-30,seed-25-50-25,seed-floating-30", "from outside: lands 1st");
        lib.SetShown("c1", dellB.Key, false);
        PutB("c1", 1); Eq(OrderB(), "seed-70-30,c1,seed-25-50-25,seed-floating-30", "from outside: lands 2nd");
        lib.SetShown("c1", dellB.Key, false);
        PutB("c1", 2); Eq(OrderB(), "seed-70-30,seed-25-50-25,c1,seed-floating-30", "from outside: lands 2nd-to-last");
        lib.SetShown("c1", dellB.Key, false);
        PutB("c1", 99); Eq(OrderB(), "seed-70-30,seed-25-50-25,seed-floating-30,c1", "from outside: lands last");
        lib.SetShown("c1", dellB.Key, false);
        lib.SetStripOrder(dellB.Key, new[] { "seed-70-30", "nope", "seed-70-30", "seed-25-50-25", "seed-floating-30" });
        Eq(OrderB(), "seed-70-30,seed-25-50-25,seed-floating-30", "unknown and repeated ids are dropped");
        lib.SetStripOrder("new-monitor", new[] { "seed-70-30" });
        Eq(lib.Monitor("new-monitor")!.Layouts[0], "seed-70-30", "unknown monitor key gets an entry");
        lib.Data.Monitors.RemoveAll(m => m.Key == "new-monitor");
        Check(lib.MainFor(kDesk, laptop.Key) == null, "no main layout any more");
        lib.Monitor(laptop.Key)!.Layouts = new List<string> { "c1", "seed-70-30", "seed-25-50-25", "seed-floating-30" }; // order the later checks expect

        // Ultrawide at the desk instead: a new setup; a new monitor gets the seeds.
        var uw = new List<MonitorInfo> { laptop, ultra };
        Monitors.Number(uw);
        lib.EnsureSetup(uw);
        string kUw = Monitors.SetupKey(uw);
        Check(ultra.Ultrawide && ultra.Label.Contains("ultrawide"), "ultrawide is detected");
        Eq(lib.StripFor(ultra.Key, kUw).Count, 3, "ultrawide gets the seeds");

        // The same monitor at another resolution: a new key that copies the strip.
        var dellLow = Mon("DELA0C3#5&2&UID2", 1920, 1920, 1080);
        Check(dellLow.Key != dellA.Key, "another resolution is another monitor key");
        var deskLow = new List<MonitorInfo> { laptop, dellLow, dellB };
        Monitors.Number(deskLow);
        lib.EnsureSetup(deskLow);
        Check(lib.StripFor(dellLow.Key, Monitors.SetupKey(deskLow)).Any(x => x.Id == "c1"), "same monitor at another resolution copies its strip");

        // Mixed DPI does not change the keys: scale is not part of them.
        var laptop100 = Mon("BOE0A1B#4&1&UID1", 0, 1920, 1200, 1.0, isInternal: true);
        Eq(laptop100.Key, laptop.Key, "scale change keeps the monitor key");

        // Save, reload, delete.
        lib.Save();
        var re = Library.Load(lib.Path);
        Eq(re.StripFor(laptop.Key, kDesk)[0].Id, "c1", "strip order survives a reload");
        Check(re.IsShown("c1", dellA.Key), "sharing survives a reload");
        re.Delete("c1");
        Check(re.Find("c1") == null, "delete removes the layout");
        Check(re.Data.Monitors.All(e => !e.Layouts.Contains("c1")), "delete removes it from every strip");
        Check(re.Data.Sets.All(s => !s.Main.ContainsValue("c1")), "delete removes it from every set");
        Eq(re.StripFor(laptop.Key, kDesk)[0].Id, "seed-70-30", "after delete the laptop strip falls back to its next layout");

        // Empty library strip never leaves the picker empty.
        foreach (var x in re.Data.Monitors) x.Layouts.Clear();
        Check(re.StripFor(laptop.Key, kDesk).Count == re.Data.Layouts.Count, "empty strip falls back to all layouts");

        Eq(Monitors.ShortId(@"\\?\DISPLAY#DELA0C3#5&1a2b&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}"), "DELA0C3#5&1a2b&0&UID4352", "short id from a device path");
    }
}
